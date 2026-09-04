using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Sources;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Processing.Xml;

/// <summary>
/// Forward-only, bounded-memory XML record reader. Tracks the open-element path with namespace URIs; when the
/// configured record path is reached it materialises exactly one record subtree as an <see cref="XElement"/>,
/// yields it, and drops it. Nothing grows with the record count.
/// </summary>
public sealed class StreamingXmlRecordReader : IRecordReader
{
    private const int FileBufferSize = 1 << 17; // 128 KiB sequential reads

    private readonly string _path;
    private readonly CompiledProfile _profile;
    private readonly int _maxRecordBytes;
    private CountingStream? _counting;
    private XmlReader? _reader;
    private long? _totalBytes;

    public StreamingXmlRecordReader(string path, CompiledProfile profile, int maxRecordFragmentChars)
    {
        _path = path;
        _profile = profile;
        // Fragment size is enforced on bytes consumed while reading a record; a UTF-8 character is at most 4 bytes.
        _maxRecordBytes = checked(maxRecordFragmentChars * 4);
    }

    public long? TotalBytes => _totalBytes;

    public long BytesRead => _counting?.BytesRead ?? 0;

    public static XmlReaderSettings CreateSecureSettings(string? xsdPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            CheckCharacters = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            CloseInput = false,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = 0,
            Async = false,
        };

        if (xsdPath is not null)
        {
            var schemas = new XmlSchemaSet { XmlResolver = null };
            using (XmlReader schemaReader = XmlReader.Create(xsdPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                schemas.Add(null, schemaReader);
            }

            schemas.Compile();
            settings.Schemas = schemas;
            settings.ValidationType = ValidationType.Schema;
            settings.ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings;
            settings.ValidationEventHandler += (_, e) =>
            {
                if (e.Severity == XmlSeverityType.Error)
                {
                    throw new ProcessingFatalException(IssueCodes.XmlSchemaViolation, $"XML schema validation failed at line {e.Exception?.LineNumber ?? 0}, position {e.Exception?.LinePosition ?? 0}.", quarantine: true, e.Exception);
                }
            };
        }

        return settings;
    }

    public async IAsyncEnumerable<SourceRecordEnvelope> ReadRecordsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Open();
        XmlReader reader = _reader!;
        CountingStream counting = _counting!;
        IReadOnlyList<XName> recordPath = _profile.RecordPath;
        var openElements = new List<XName>(32);
        long ordinal = 0;
        bool rootChecked = false;

        bool needRead = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (needRead)
            {
                bool moved;
                try
                {
                    moved = reader.Read();
                }
                catch (XmlException ex)
                {
                    throw Malformed(ex);
                }

                if (!moved)
                {
                    break;
                }
            }
            else if (reader.EOF || reader.NodeType == XmlNodeType.None)
            {
                break;
            }

            needRead = true;

            if (reader.NodeType == XmlNodeType.Element)
            {
                XName name = XName.Get(reader.LocalName, reader.NamespaceURI);
                if (!rootChecked)
                {
                    rootChecked = true;
                    if (name != recordPath[0])
                    {
                        throw new ProcessingFatalException(IssueCodes.XmlUnexpectedRoot, $"Root element is '{Describe(name)}' but the profile expects '{Describe(recordPath[0])}'.", quarantine: true);
                    }
                }

                openElements.Add(name);
                if (openElements.Count == recordPath.Count && PathMatches(openElements, recordPath))
                {
                    long before = counting.BytesRead;
                    XElement fragment;
                    try
                    {
                        // Consumes the whole record element and positions the reader after it (no EndElement event).
                        fragment = (XElement)XNode.ReadFrom(reader);
                    }
                    catch (XmlException ex)
                    {
                        throw Malformed(ex);
                    }

                    openElements.RemoveAt(openElements.Count - 1);
                    long consumed = counting.BytesRead - before;
                    if (consumed > _maxRecordBytes)
                    {
                        throw new ProcessingFatalException(IssueCodes.XmlRecordTooLarge, $"Record {ordinal + 1} exceeds the maximum record size.", quarantine: false);
                    }

                    ordinal++;
                    yield return new SourceRecordEnvelope(ordinal, counting.BytesRead, fragment);
                    // ReadFrom leaves the reader positioned on the node after the record; process it without another Read().
                    needRead = false;
                    continue;
                }

                if (reader.IsEmptyElement)
                {
                    openElements.RemoveAt(openElements.Count - 1);
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                openElements.RemoveAt(openElements.Count - 1);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _reader?.Dispose();
        _reader = null;
        _counting?.Dispose();
        _counting = null;
        return ValueTask.CompletedTask;
    }

    private void Open()
    {
        if (_reader is not null)
        {
            throw new InvalidOperationException("The reader can be enumerated only once.");
        }

        var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize, FileOptions.SequentialScan);
        _totalBytes = file.Length;
        _counting = new CountingStream(file);
        try
        {
            _reader = XmlReader.Create(_counting, CreateSecureSettings(_profile.ResolvedXsdPath));
        }
        catch (XmlException ex)
        {
            throw Malformed(ex);
        }
        catch (XmlSchemaException ex)
        {
            throw new ProcessingFatalException(IssueCodes.XmlSchemaViolation, $"The configured XSD could not be loaded (line {ex.LineNumber}).", quarantine: false, ex);
        }
    }

    private static bool PathMatches(List<XName> open, IReadOnlyList<XName> expected)
    {
        for (int i = 0; i < expected.Count; i++)
        {
            if (open[i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    private static ProcessingFatalException Malformed(XmlException ex)
    {
        // Deliberately excludes ex.Message: it can echo document content.
        string code = ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase) ? IssueCodes.XmlDtdProhibited : IssueCodes.XmlMalformed;
        string message = code == IssueCodes.XmlDtdProhibited
            ? $"The document declares a DTD, which is prohibited (line {ex.LineNumber}, position {ex.LinePosition})."
            : $"Malformed XML at line {ex.LineNumber}, position {ex.LinePosition}.";
        return new ProcessingFatalException(code, message, quarantine: true, ex);
    }

    private static string Describe(XName name) => string.IsNullOrEmpty(name.NamespaceName) ? name.LocalName : $"{{{name.NamespaceName}}}{name.LocalName}";
}

public sealed class StreamingXmlRecordReaderFactory : IRecordReaderFactory
{
    private readonly IOptionsMonitor<ProcessingOptions> _options;

    public StreamingXmlRecordReaderFactory(IOptionsMonitor<ProcessingOptions> options)
    {
        _options = options;
    }

    public IRecordReader Create(string sourcePath, CompiledProfile profile) =>
        new StreamingXmlRecordReader(sourcePath, profile, _options.CurrentValue.MaxRecordFragmentChars);
}
