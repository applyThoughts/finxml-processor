using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Tables;
using Microsoft.Extensions.Logging;
using CellType = FinXmlProcessor.Domain.Cells.CellType;
using CellValue = FinXmlProcessor.Domain.Cells.CellValue;

namespace FinXmlProcessor.Output.Excel;

/// <summary>
/// Forward-only XLSX writer with bounded memory.
/// <para>
/// Why not <c>OpenXmlWriter</c> for the data path: the SDK opens its package in read/write mode, which makes
/// System.IO.Packaging buffer every worksheet part in memory until the package is closed (measured: ~400 MB
/// managed heap for a 200 MB input). This writer therefore streams worksheet XML through <see cref="XmlWriter"/>
/// into compressed spool files and assembles the package with <see cref="ZipArchive"/> in create mode, one entry
/// at a time. The OpenXml SDK still produces the style table and performs read-side structural verification.
/// </para>
/// Inline strings avoid an unbounded shared-string table; sheets split at the row limit; no formula element is ever emitted.
/// </summary>
public sealed class StreamingXlsxWorkbookWriter : IWorkbookWriter
{
    private readonly ILogger<StreamingXlsxWorkbookWriter> _logger;

    public StreamingXlsxWorkbookWriter(ILogger<StreamingXlsxWorkbookWriter> logger)
    {
        _logger = logger;
    }

    public Task<IWorkbookSession> BeginAsync(string finalPath, IReadOnlyList<OutputTableDefinition> tables, WorkbookWriterOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = new Session(finalPath, tables, options, _logger);
        return Task.FromResult<IWorkbookSession>(session);
    }

    public Task<IReadOnlyList<RecordIssue>> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        var issues = new List<RecordIssue>();
        try
        {
            using SpreadsheetDocument document = SpreadsheetDocument.Open(path, isEditable: false);
            WorkbookPart? workbook = document.WorkbookPart;
            if (workbook?.Workbook?.Sheets is null)
            {
                issues.Add(RecordIssue.Fatal(IssueCodes.OutputPackageInvalid, "Workbook has no sheet list."));
                return Task.FromResult<IReadOnlyList<RecordIssue>>(issues);
            }

            if (workbook.WorkbookStylesPart?.Stylesheet is null)
            {
                issues.Add(RecordIssue.Fatal(IssueCodes.OutputPackageInvalid, "Workbook has no stylesheet."));
            }

            foreach (Sheet sheet in workbook.Workbook.Sheets.Elements<Sheet>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sheet.Id?.Value is null || workbook.GetPartById(sheet.Id.Value) is not WorksheetPart part)
                {
                    issues.Add(RecordIssue.Fatal(IssueCodes.OutputPackageInvalid, $"Sheet '{sheet.Name}' has no worksheet part."));
                    continue;
                }

                // Forward-only walk: verifies the part is well-formed XML without loading it as a DOM.
                using OpenXmlReader reader = OpenXmlReader.Create(part);
                long rows = 0;
                while (reader.Read())
                {
                    if (reader.ElementType == typeof(Row) && reader.IsStartElement)
                    {
                        rows++;
                    }
                }

                if (rows == 0)
                {
                    issues.Add(RecordIssue.Warning(IssueCodes.OutputPackageInvalid, null, $"Sheet '{sheet.Name}' contains no rows."));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OpenXmlPackageException or XmlException or FileFormatException)
        {
            issues.Add(RecordIssue.Fatal(IssueCodes.OutputPackageInvalid, $"Workbook could not be opened ({ex.GetType().Name})."));
        }

        return Task.FromResult<IReadOnlyList<RecordIssue>>(issues);
    }

    /// <summary>One worksheet being streamed: XML goes through a fast deflate spool on disk, never into memory.</summary>
    private sealed class SheetSpool : IDisposable
    {
        private readonly FileStream _file;
        private readonly DeflateStream _deflate;

        public SheetSpool(string spoolPath, string name, OutputTableDefinition table, uint[] styles, bool autoFilter)
        {
            Path = spoolPath;
            Name = name;
            Table = table;
            Styles = styles;
            AutoFilter = autoFilter;
            _file = new FileStream(spoolPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
            _deflate = new DeflateStream(_file, CompressionLevel.Fastest, leaveOpen: false);
            Writer = XmlWriter.Create(_deflate, new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false, CloseOutput = false, CheckCharacters = false, NewLineHandling = NewLineHandling.None });
        }

        public string Path { get; }

        public string Name { get; }

        public OutputTableDefinition Table { get; }

        public uint[] Styles { get; }

        public bool AutoFilter { get; }

        public XmlWriter Writer { get; }

        public uint RowsInSheet { get; set; }

        public bool Closed { get; private set; }

        public void Close()
        {
            if (Closed)
            {
                return;
            }

            Closed = true;
            Writer.Dispose();
            _deflate.Dispose();
        }

        public void Dispose()
        {
            Close();
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class Session : IWorkbookSession
    {
        private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string PackageRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

        private static readonly OutputTableDefinition RejectedTable = new(
            "__rejected",
            "Rejected Records",
            [
                new OutputColumnDefinition("ordinal", "Source Record #", CellType.Integer, Width: 16),
                new OutputColumnDefinition("identifier", "Identifier", CellType.Text, Width: 26),
                new OutputColumnDefinition("codes", "Error Codes", CellType.Text, Width: 20),
                new OutputColumnDefinition("messages", "Messages", CellType.Text, Width: 80),
                new OutputColumnDefinition("fields", "Safe Field Values", CellType.Text, Width: 80),
            ]);

        private static readonly OutputTableDefinition SummaryTable = new("__summary", "Summary",
        [
            new OutputColumnDefinition("item", "Item", CellType.Text, Width: 28),
            new OutputColumnDefinition("value", "Value", CellType.Text, Width: 90),
        ]);

        private readonly string _finalPath;
        private readonly string _stagingPath;
        private readonly string _spoolDirectory;
        private readonly WorkbookWriterOptions _options;
        private readonly ILogger _logger;
        private readonly XlsxStyles _styles;
        private readonly SheetNaming.Allocator _names = new();
        private readonly Dictionary<string, TableState> _tables = new(StringComparer.Ordinal);
        private readonly List<SheetSpool> _completedSheets = [];
        private TableState? _rejected;
        private int _spoolCounter;
        private long _rowsWritten;
        private long _truncatedCells;
        private bool _completed;
        private bool _disposed;

        public Session(string finalPath, IReadOnlyList<OutputTableDefinition> tables, WorkbookWriterOptions options, ILogger logger)
        {
            _finalPath = finalPath;
            _stagingPath = finalPath + ".part";
            _spoolDirectory = finalPath + ".spool";
            _options = options;
            _logger = logger;
            _names.Allocate(SummaryTable.SheetName); // reserved: written outside the allocator
            if (File.Exists(_stagingPath))
            {
                File.Delete(_stagingPath);
            }

            if (Directory.Exists(_spoolDirectory))
            {
                Directory.Delete(_spoolDirectory, recursive: true);
            }

            Directory.CreateDirectory(_spoolDirectory);
            _styles = XlsxStyles.Create(tables.Concat([RejectedTable, SummaryTable]).ToList());
            foreach (OutputTableDefinition table in tables)
            {
                var state = new TableState(table, _styles.ColumnStyles(table));
                _tables[table.Id] = state;
                OpenNextSheet(state);
            }
        }

        public long RowsWritten => _rowsWritten;

        public long TruncatedCells => _truncatedCells;

        public void WriteRow(OutputRow row)
        {
            ThrowIfUnusable();
            if (!_tables.TryGetValue(row.TableId, out TableState? state))
            {
                throw new InvalidOperationException($"Unknown output table '{row.TableId}'.");
            }

            if (state.Current!.RowsInSheet >= _options.MaxRowsPerSheet)
            {
                CloseSheet(state);
                OpenNextSheet(state);
            }

            WriteCells(state.Current!, row.Cells);
            _rowsWritten++;
        }

        public void WriteRejected(RejectedRecordLine line)
        {
            ThrowIfUnusable();
            if (!_options.IncludeRejectedSheet)
            {
                return;
            }

            if (_rejected is null)
            {
                _rejected = new TableState(RejectedTable, _styles.ColumnStyles(RejectedTable));
                OpenNextSheet(_rejected);
            }

            if (_rejected.Current!.RowsInSheet >= _options.MaxRowsPerSheet)
            {
                CloseSheet(_rejected);
                OpenNextSheet(_rejected);
            }

            var fields = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in line.SafeFields)
            {
                if (fields.Length > 0)
                {
                    fields.Append("; ");
                }

                fields.Append(pair.Key).Append('=').Append(pair.Value);
            }

            WriteCells(_rejected.Current!,
            [
                CellValue.FromInteger(line.SourceOrdinal),
                CellValue.FromText(line.SafeIdentifier ?? string.Empty),
                CellValue.FromText(line.Codes),
                CellValue.FromText(line.Messages),
                CellValue.FromText(fields.ToString()),
            ]);
        }

        public async Task<string> CompleteAsync(IReadOnlyList<SummaryEntry> summary, IReadOnlyList<RecordIssue> jobIssues, CancellationToken cancellationToken)
        {
            ThrowIfUnusable();
            cancellationToken.ThrowIfCancellationRequested();
            foreach (TableState state in _tables.Values)
            {
                CloseSheet(state);
            }

            if (_rejected is not null)
            {
                CloseSheet(_rejected);
            }

            SheetSpool summarySheet = WriteSummarySheet(summary, jobIssues);
            var sheets = new List<SheetSpool> { summarySheet };
            sheets.AddRange(_completedSheets);

            await using (FileStream package = new(_stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteEntry(zip, "[Content_Types].xml", w => WriteContentTypes(w, sheets.Count));
                WriteEntry(zip, "_rels/.rels", WriteRootRelationships);
                WriteEntry(zip, "xl/workbook.xml", w => WriteWorkbook(w, sheets));
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", w => WriteWorkbookRelationships(w, sheets.Count));
                WriteRawEntry(zip, "xl/styles.xml", _styles.StylesheetXml);
                for (int i = 0; i < sheets.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ZipArchiveEntry entry = zip.CreateEntry($"xl/worksheets/sheet{i + 1}.xml", CompressionLevel.Optimal);
                    await using Stream target = entry.Open();
                    await using FileStream spool = new(sheets[i].Path, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    await using var inflate = new DeflateStream(spool, CompressionMode.Decompress);
                    await inflate.CopyToAsync(target, 1 << 16, cancellationToken).ConfigureAwait(false);
                }
            }

            _completed = true;
            summarySheet.Dispose();
            DisposeSpools();
            if (File.Exists(_finalPath))
            {
                throw new IOException($"Output file already exists: {Path.GetFileName(_finalPath)}");
            }

            File.Move(_stagingPath, _finalPath, overwrite: false);
            return _finalPath;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            DisposeSpools();
            if (!_completed)
            {
                try
                {
                    if (File.Exists(_stagingPath))
                    {
                        File.Delete(_stagingPath);
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not delete staging workbook {Path}", _stagingPath);
                }
            }

            return ValueTask.CompletedTask;
        }

        private void DisposeSpools()
        {
            foreach (TableState state in _tables.Values)
            {
                state.Current?.Dispose();
                state.Current = null;
            }

            if (_rejected is not null)
            {
                _rejected.Current?.Dispose();
                _rejected.Current = null;
            }

            foreach (SheetSpool spool in _completedSheets)
            {
                spool.Dispose();
            }

            _completedSheets.Clear();
            try
            {
                if (Directory.Exists(_spoolDirectory))
                {
                    Directory.Delete(_spoolDirectory, recursive: true);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete spool folder {Path}", _spoolDirectory);
            }
        }

        private void ThrowIfUnusable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                throw new InvalidOperationException("The workbook session is already completed.");
            }
        }

        private void OpenNextSheet(TableState state)
        {
            state.PartNumber++;
            string name = _names.Allocate(state.PartNumber == 1 ? state.Table.SheetName : SheetNaming.WithSuffix(SheetNaming.Sanitize(state.Table.SheetName), state.PartNumber));
            var spool = new SheetSpool(Path.Combine(_spoolDirectory, $"sheet-{++_spoolCounter}.xml.deflate"), name, state.Table, state.Styles, autoFilter: true);
            state.Current = spool;
            XmlWriter w = spool.Writer;
            w.WriteStartDocument(true);
            w.WriteStartElement("worksheet", MainNs);
            w.WriteAttributeString("xmlns", "r", null, RelNs);
            w.WriteStartElement("sheetViews", MainNs);
            w.WriteStartElement("sheetView", MainNs);
            w.WriteAttributeString("workbookViewId", "0");
            w.WriteStartElement("pane", MainNs);
            w.WriteAttributeString("ySplit", "1");
            w.WriteAttributeString("topLeftCell", "A2");
            w.WriteAttributeString("activePane", "bottomLeft");
            w.WriteAttributeString("state", "frozen");
            w.WriteEndElement();
            w.WriteStartElement("selection", MainNs);
            w.WriteAttributeString("pane", "bottomLeft");
            w.WriteEndElement();
            w.WriteEndElement(); // sheetView
            w.WriteEndElement(); // sheetViews
            WriteColumns(w, state.Table);
            w.WriteStartElement("sheetData", MainNs);
            WriteHeaderRow(spool);
        }

        private static void WriteColumns(XmlWriter w, OutputTableDefinition table)
        {
            w.WriteStartElement("cols", MainNs);
            for (int i = 0; i < table.Columns.Count; i++)
            {
                OutputColumnDefinition column = table.Columns[i];
                double width = Math.Clamp(column.Width ?? DefaultWidth(column), 4, 255);
                w.WriteStartElement("col", MainNs);
                w.WriteAttributeString("min", (i + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("max", (i + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("width", width.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("customWidth", "1");
                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        private static double DefaultWidth(OutputColumnDefinition column) => column.CellType switch
        {
            CellType.Text => Math.Clamp(column.Heading.Length + 4, 12, 40),
            CellType.Integer => 12,
            CellType.Decimal => 16,
            CellType.Date => 12,
            CellType.DateTime => 20,
            CellType.Boolean => 10,
            _ => 14,
        };

        private void WriteHeaderRow(SheetSpool spool)
        {
            XmlWriter w = spool.Writer;
            spool.RowsInSheet++;
            w.WriteStartElement("row", MainNs);
            w.WriteAttributeString("r", spool.RowsInSheet.ToString(CultureInfo.InvariantCulture));
            foreach (OutputColumnDefinition column in spool.Table.Columns)
            {
                WriteInlineString(w, column.Heading, _styles.HeaderStyle);
            }

            w.WriteEndElement();
        }

        private void WriteCells(SheetSpool spool, IReadOnlyList<CellValue> cells)
        {
            XmlWriter w = spool.Writer;
            spool.RowsInSheet++;
            w.WriteStartElement("row", MainNs);
            w.WriteAttributeString("r", spool.RowsInSheet.ToString(CultureInfo.InvariantCulture));
            int count = Math.Min(cells.Count, spool.Table.Columns.Count);
            for (int i = 0; i < count; i++)
            {
                CellValue cell = cells[i];
                uint style = spool.Styles[i];
                if (cell.IsBlank)
                {
                    w.WriteStartElement("c", MainNs);
                    w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
                    w.WriteEndElement();
                    continue;
                }

                switch (cell.Type)
                {
                    case CellType.Text:
                        WriteInlineString(w, cell.TextValue, style);
                        break;
                    case CellType.Integer:
                        WriteValueCell(w, cell.IntegerValue.ToString(CultureInfo.InvariantCulture), style, null);
                        break;
                    case CellType.Decimal:
                        WriteValueCell(w, cell.DecimalValue.ToString(CultureInfo.InvariantCulture), style, null);
                        break;
                    case CellType.Date:
                        WriteValueCell(w, cell.DateValue.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(CultureInfo.InvariantCulture), style, null);
                        break;
                    case CellType.DateTime:
                        WriteValueCell(w, cell.DateTimeValue.ToOADate().ToString("R", CultureInfo.InvariantCulture), style, null);
                        break;
                    case CellType.Boolean:
                        WriteValueCell(w, cell.BooleanValue ? "1" : "0", style, "b");
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown cell type {cell.Type}.");
                }
            }

            w.WriteEndElement();
        }

        private static void WriteValueCell(XmlWriter w, string value, uint style, string? type)
        {
            w.WriteStartElement("c", MainNs);
            if (type is not null)
            {
                w.WriteAttributeString("t", type);
            }

            w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            w.WriteStartElement("v", MainNs);
            w.WriteString(value);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        /// <summary>
        /// Inline strings are always literal: Excel never evaluates them, so text beginning with =, +, - or @ cannot
        /// become a formula. No <c>&lt;f&gt;</c> element is ever emitted by this writer.
        /// </summary>
        private void WriteInlineString(XmlWriter w, string text, uint style)
        {
            string safe = SanitizeText(text, _options.MaxCellTextLength, ref _truncatedCells);
            w.WriteStartElement("c", MainNs);
            w.WriteAttributeString("t", "inlineStr");
            w.WriteAttributeString("s", style.ToString(CultureInfo.InvariantCulture));
            w.WriteStartElement("is", MainNs);
            w.WriteStartElement("t", MainNs);
            if (safe.Length > 0 && (char.IsWhiteSpace(safe[0]) || char.IsWhiteSpace(safe[^1])))
            {
                w.WriteAttributeString("xml", "space", null, "preserve");
            }

            w.WriteString(safe);
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static string SanitizeText(string text, int maxLength, ref long truncated)
        {
            bool needsCopy = text.Length > maxLength;
            if (!needsCopy)
            {
                foreach (char c in text)
                {
                    if (IsInvalidXmlChar(c))
                    {
                        needsCopy = true;
                        break;
                    }
                }
            }

            if (!needsCopy)
            {
                return text;
            }

            int length = Math.Min(text.Length, maxLength);
            if (text.Length > maxLength)
            {
                truncated++;
            }

            var sb = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                char c = text[i];
                sb.Append(IsInvalidXmlChar(c) ? '�' : c);
            }

            return sb.ToString();
        }

        private static bool IsInvalidXmlChar(char c) =>
            (c < 0x20 && c != 0x09 && c != 0x0A && c != 0x0D) || c == 0xFFFE || c == 0xFFFF;

        private void CloseSheet(TableState state)
        {
            SheetSpool? spool = state.Current;
            if (spool is null || spool.Closed)
            {
                return;
            }

            XmlWriter w = spool.Writer;
            w.WriteEndElement(); // sheetData
            if (spool.AutoFilter && spool.RowsInSheet > 0)
            {
                w.WriteStartElement("autoFilter", MainNs);
                w.WriteAttributeString("ref", $"A1:{ColumnLetter(spool.Table.Columns.Count)}{spool.RowsInSheet}");
                w.WriteEndElement();
            }

            w.WriteEndElement(); // worksheet
            w.WriteEndDocument();
            spool.Close();
            _completedSheets.Add(spool);
            state.Current = null;
        }

        private SheetSpool WriteSummarySheet(IReadOnlyList<SummaryEntry> summary, IReadOnlyList<RecordIssue> jobIssues)
        {
            var spool = new SheetSpool(Path.Combine(_spoolDirectory, "summary.xml.deflate"), SummaryTable.SheetName, SummaryTable, _styles.ColumnStyles(SummaryTable), autoFilter: false);
            XmlWriter w = spool.Writer;
            w.WriteStartDocument(true);
            w.WriteStartElement("worksheet", MainNs);
            w.WriteAttributeString("xmlns", "r", null, RelNs);
            WriteColumns(w, SummaryTable);
            w.WriteStartElement("sheetData", MainNs);
            WriteHeaderRow(spool);
            foreach (SummaryEntry entry in summary)
            {
                WriteCells(spool, [CellValue.FromText(entry.Label), CellValue.FromText(entry.Value)]);
            }

            var notable = jobIssues.Where(i => i.Severity >= IssueSeverity.Warning && i.SourceOrdinal is null).Take(200).ToList();
            if (notable.Count > 0)
            {
                spool.RowsInSheet++;
                w.WriteStartElement("row", MainNs);
                w.WriteAttributeString("r", spool.RowsInSheet.ToString(CultureInfo.InvariantCulture));
                WriteInlineString(w, "Job issues", _styles.HeaderStyle);
                WriteInlineString(w, string.Empty, _styles.HeaderStyle);
                w.WriteEndElement();
                foreach (RecordIssue issue in notable)
                {
                    spool.RowsInSheet++;
                    w.WriteStartElement("row", MainNs);
                    w.WriteAttributeString("r", spool.RowsInSheet.ToString(CultureInfo.InvariantCulture));
                    WriteInlineString(w, $"{issue.Severity} {issue.Code}", _styles.WarningStyle);
                    WriteInlineString(w, issue.Message, _styles.WarningStyle);
                    w.WriteEndElement();
                }
            }

            w.WriteEndElement(); // sheetData
            w.WriteEndElement(); // worksheet
            w.WriteEndDocument();
            spool.Close();
            return spool;
        }

        private static void WriteEntry(ZipArchive zip, string name, Action<XmlWriter> write)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using Stream stream = entry.Open();
            using XmlWriter w = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = false });
            w.WriteStartDocument(true);
            write(w);
            w.WriteEndDocument();
        }

        private static void WriteRawEntry(ZipArchive zip, string name, string xml)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using Stream stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(xml);
        }

        private static void WriteContentTypes(XmlWriter w, int sheetCount)
        {
            const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
            w.WriteStartElement("Types", ns);
            w.WriteStartElement("Default", ns);
            w.WriteAttributeString("Extension", "rels");
            w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml");
            w.WriteEndElement();
            w.WriteStartElement("Default", ns);
            w.WriteAttributeString("Extension", "xml");
            w.WriteAttributeString("ContentType", "application/xml");
            w.WriteEndElement();
            Override(w, ns, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            Override(w, ns, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            for (int i = 1; i <= sheetCount; i++)
            {
                Override(w, ns, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            }

            w.WriteEndElement();

            static void Override(XmlWriter w, string ns, string partName, string contentType)
            {
                w.WriteStartElement("Override", ns);
                w.WriteAttributeString("PartName", partName);
                w.WriteAttributeString("ContentType", contentType);
                w.WriteEndElement();
            }
        }

        private static void WriteRootRelationships(XmlWriter w)
        {
            w.WriteStartElement("Relationships", PackageRelNs);
            w.WriteStartElement("Relationship", PackageRelNs);
            w.WriteAttributeString("Id", "rId1");
            w.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument");
            w.WriteAttributeString("Target", "xl/workbook.xml");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void WriteWorkbookRelationships(XmlWriter w, int sheetCount)
        {
            w.WriteStartElement("Relationships", PackageRelNs);
            for (int i = 1; i <= sheetCount; i++)
            {
                w.WriteStartElement("Relationship", PackageRelNs);
                w.WriteAttributeString("Id", $"rId{i}");
                w.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet");
                w.WriteAttributeString("Target", $"worksheets/sheet{i}.xml");
                w.WriteEndElement();
            }

            w.WriteStartElement("Relationship", PackageRelNs);
            w.WriteAttributeString("Id", $"rId{sheetCount + 1}");
            w.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles");
            w.WriteAttributeString("Target", "styles.xml");
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void WriteWorkbook(XmlWriter w, List<SheetSpool> sheets)
        {
            w.WriteStartElement("workbook", MainNs);
            w.WriteAttributeString("xmlns", "r", null, RelNs);
            w.WriteStartElement("bookViews", MainNs);
            w.WriteStartElement("workbookView", MainNs);
            w.WriteAttributeString("activeTab", "0");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("sheets", MainNs);
            for (int i = 0; i < sheets.Count; i++)
            {
                w.WriteStartElement("sheet", MainNs);
                w.WriteAttributeString("name", sheets[i].Name);
                w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("r", "id", RelNs, $"rId{i + 1}");
                w.WriteEndElement();
            }

            w.WriteEndElement();
            var filtered = sheets.Select((s, index) => (s, index)).Where(x => x.s.AutoFilter && x.s.RowsInSheet > 0).ToList();
            if (filtered.Count > 0)
            {
                w.WriteStartElement("definedNames", MainNs);
                foreach ((SheetSpool sheet, int index) in filtered)
                {
                    w.WriteStartElement("definedName", MainNs);
                    w.WriteAttributeString("name", "_xlnm._FilterDatabase");
                    w.WriteAttributeString("localSheetId", index.ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("hidden", "1");
                    w.WriteString($"'{sheet.Name.Replace("'", "''", StringComparison.Ordinal)}'!$A$1:${ColumnLetter(sheet.Table.Columns.Count)}${sheet.RowsInSheet}");
                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            w.WriteEndElement();
        }

        private static string ColumnLetter(int columnCount)
        {
            int n = Math.Max(1, columnCount);
            var sb = new StringBuilder();
            while (n > 0)
            {
                int rem = (n - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                n = (n - 1) / 26;
            }

            return sb.ToString();
        }

        private sealed class TableState
        {
            public TableState(OutputTableDefinition table, uint[] styles)
            {
                Table = table;
                Styles = styles;
            }

            public OutputTableDefinition Table { get; }

            public uint[] Styles { get; }

            public SheetSpool? Current { get; set; }

            public int PartNumber { get; set; }
        }
    }
}
