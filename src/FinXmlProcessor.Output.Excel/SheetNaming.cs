using System.Text;

namespace FinXmlProcessor.Output.Excel;

/// <summary>Enforces Excel worksheet naming rules: 1–31 chars, no []:*?/\, no leading/trailing apostrophe, unique per workbook.</summary>
public static class SheetNaming
{
    public const int MaxLength = 31;
    private static readonly char[] Forbidden = ['[', ']', ':', '*', '?', '/', '\\'];

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Sheet";
        }

        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            sb.Append(Array.IndexOf(Forbidden, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        string result = sb.ToString().Trim('\'');
        if (result.Length == 0)
        {
            return "Sheet";
        }

        if (string.Equals(result, "History", StringComparison.OrdinalIgnoreCase))
        {
            // "History" is reserved by Excel for shared-workbook change tracking.
            result = "History_";
        }

        return result.Length > MaxLength ? result[..MaxLength] : result;
    }

    /// <summary>Returns "{base} ({n})" trimmed so the suffix always fits within 31 characters.</summary>
    public static string WithSuffix(string baseName, int n)
    {
        string suffix = $" ({n})";
        int room = MaxLength - suffix.Length;
        string stem = baseName.Length > room ? baseName[..room] : baseName;
        return stem + suffix;
    }

    /// <summary>Allocates unique names case-insensitively, as Excel does.</summary>
    public sealed class Allocator
    {
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);

        public string Allocate(string desired)
        {
            string candidate = Sanitize(desired);
            if (_used.Add(candidate))
            {
                return candidate;
            }

            for (int n = 2; ; n++)
            {
                string next = WithSuffix(candidate, n);
                if (_used.Add(next))
                {
                    return next;
                }
            }
        }
    }
}
