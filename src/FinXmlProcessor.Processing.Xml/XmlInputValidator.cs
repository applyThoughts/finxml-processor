using System.Security.Cryptography;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Domain.Issues;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Processing.Xml;

/// <summary>
/// File-level validation and hashing. Sniffs the first bytes to reject compressed, encrypted or binary inputs
/// early with a clear message instead of a confusing XML parse error.
/// </summary>
public sealed class XmlInputValidator : IInputValidator
{
    private readonly IOptionsMonitor<ProcessingOptions> _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public XmlInputValidator(IOptionsMonitor<ProcessingOptions> options)
        : this(options, Task.Delay)
    {
    }

    internal XmlInputValidator(IOptionsMonitor<ProcessingOptions> options, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _options = options;
        _delay = delay;
    }

    public async Task<InputValidationResult> ValidateFileAsync(string path, CancellationToken cancellationToken)
    {
        ProcessingOptions options = _options.CurrentValue;
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return InputValidationResult.Fatal(RecordIssue.Fatal(IssueCodes.FileNotFound, "Input file does not exist."));
        }

        string extension = info.Extension;
        if (!options.AllowedExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
        {
            return InputValidationResult.Fatal(RecordIssue.Fatal(IssueCodes.FileUnsupportedExtension, $"Extension '{extension}' is not accepted (allowed: {string.Join(", ", options.AllowedExtensions)})."), info.Length);
        }

        if (info.Length == 0)
        {
            return InputValidationResult.Fatal(RecordIssue.Fatal(IssueCodes.FileEmpty, "Input file is empty."), 0);
        }

        if (info.Length > options.MaxInputBytes)
        {
            return InputValidationResult.Fatal(RecordIssue.Fatal(IssueCodes.FileTooLarge, $"Input is {info.Length:N0} bytes, above the configured maximum of {options.MaxInputBytes:N0} bytes."), info.Length);
        }

        long firstLength = info.Length;
        DateTime firstWrite = info.LastWriteTimeUtc;
        if (options.StabilityWindowMilliseconds > 0)
        {
            await _delay(TimeSpan.FromMilliseconds(options.StabilityWindowMilliseconds), cancellationToken).ConfigureAwait(false);
            info.Refresh();
            if (!info.Exists || info.Length != firstLength || info.LastWriteTimeUtc != firstWrite)
            {
                return InputValidationResult.Fatal(RecordIssue.Fatal(IssueCodes.FileUnstable, "Input file is still changing; it may still be downloading or being written."), info.Length);
            }
        }

        var issues = new List<RecordIssue>();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
            byte[] head = new byte[512];
            int headLength = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            string? formatProblem = SniffFormat(head.AsSpan(0, headLength));
            if (formatProblem is not null)
            {
                return InputValidationResult.Fatal(RecordIssue.Fatal(IssueCodes.FileUnsupportedFormat, formatProblem), info.Length);
            }

            stream.Position = 0;
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return new InputValidationResult(true, issues, info.Length, Convert.ToHexStringLower(hash));
        }
        catch (UnauthorizedAccessException)
        {
            return InputValidationResult.Fatal(RecordIssue.Fatal(IssueCodes.FileNotAccessible, "Input file cannot be opened for reading (permission denied)."), info.Length);
        }
        catch (IOException ex)
        {
            return InputValidationResult.Fatal(RecordIssue.Fatal(IssueCodes.FileNotAccessible, $"Input file cannot be read ({ex.GetType().Name})."), info.Length);
        }
    }

    /// <summary>Returns a user-facing problem description, or null when the head looks like plain XML.</summary>
    public static string? SniffFormat(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
        {
            return "Input is gzip-compressed. Plain XML is required; decompress the file first.";
        }

        if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && (head[2] == 0x03 || head[2] == 0x05 || head[2] == 0x07))
        {
            return "Input is a ZIP archive. Plain XML is required; extract the file first.";
        }

        if (head.Length >= 6 && head[0] == 0xFD && head[1] == 0x37 && head[2] == 0x7A && head[3] == 0x58 && head[4] == 0x5A && head[5] == 0x00)
        {
            return "Input is xz-compressed. Plain XML is required; decompress the file first.";
        }

        if (head.Length >= 3 && head[0] == 0x42 && head[1] == 0x5A && head[2] == 0x68)
        {
            return "Input is bzip2-compressed. Plain XML is required; decompress the file first.";
        }

        if (head.Length >= 4 && head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21)
        {
            return "Input is a RAR archive. Plain XML is required; extract the file first.";
        }

        if (StartsWithAscii(head, "-----BEGIN PGP") || (head.Length >= 2 && (head[0] == 0x85 || head[0] == 0x8C || head[0] == 0xC1) && head[1] != 0x00 && IsMostlyBinary(head)))
        {
            return "Input appears to be PGP-encrypted. Plain XML is required; decrypt the file first.";
        }

        if (IsMostlyBinary(head))
        {
            return "Input does not look like a text file (binary content). Plain XML is required.";
        }

        int offset = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            offset = 3;
        }
        else if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
        {
            // UTF-16 with BOM: let the XML reader handle it.
            return null;
        }

        for (int i = offset; i < head.Length; i++)
        {
            byte b = head[i];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return b == (byte)'<' ? null : "Input does not start with an XML declaration or element. Plain XML is required.";
        }

        return null;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> head, string prefix)
    {
        if (head.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (head[i] != (byte)prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMostlyBinary(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0)
        {
            return false;
        }

        // UTF-16 text has many zero bytes; only treat as binary if there are control characters other than
        // tab/LF/CR/NUL, which never appear in well-formed XML text encodings.
        int suspicious = 0;
        foreach (byte b in head)
        {
            if (b < 0x09 && b != 0x00)
            {
                suspicious++;
            }
            else if (b is > 0x0D and < 0x20)
            {
                suspicious++;
            }
        }

        return suspicious > head.Length / 32;
    }
}
