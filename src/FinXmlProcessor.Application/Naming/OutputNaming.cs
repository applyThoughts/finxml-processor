using System.Globalization;
using System.Text;

namespace FinXmlProcessor.Application.Naming;

/// <summary>Deterministic, collision-resistant names and filename sanitization shared by outputs, reports and quarantine.</summary>
public static class OutputNaming
{
    private static readonly HashSet<char> InvalidChars = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>e.g. <c>demo-fintech-v1_2026-09-03_5f3a9c1e.xlsx</c>.</summary>
    public static string WorkbookFileName(string profileId, DateOnly businessDate, Guid jobId) =>
        $"{SanitizeFileNameComponent(profileId)}_{businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{ShortJobId(jobId)}.xlsx";

    public static string ReportFileName(DateOnly businessDate, Guid jobId) =>
        $"report_{businessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{ShortJobId(jobId)}.json";

    public static string ShortJobId(Guid jobId) => jobId.ToString("N")[..8];

    /// <summary>
    /// Produces a filesystem-safe component: strips path separators and control characters, avoids reserved
    /// device names and leading dots, and caps the length. Never returns an empty string.
    /// </summary>
    public static string SanitizeFileNameComponent(string? input, int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "unnamed";
        }

        var sb = new StringBuilder(Math.Min(input.Length, maxLength));
        foreach (char c in input.Trim())
        {
            if (InvalidChars.Contains(c) || char.IsControl(c))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }

            if (sb.Length >= maxLength)
            {
                break;
            }
        }

        string result = sb.ToString().TrimEnd('.', ' ');
        while (result.StartsWith('.'))
        {
            result = result[1..];
        }

        if (result.Length == 0)
        {
            return "unnamed";
        }

        string stem = Path.GetFileNameWithoutExtension(result);
        if (ReservedNames.Contains(stem))
        {
            result = "_" + result;
        }

        return result;
    }

    /// <summary>Returns the file name portion only, sanitized, so a remote or user-supplied name can never traverse directories.</summary>
    public static string SafeFileNameFromPath(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName))
        {
            return "unnamed";
        }

        string name = pathOrName.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return SanitizeFileNameComponent(name, 200);
    }
}
