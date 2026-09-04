using FinXmlProcessor.Domain.Tables;

namespace FinXmlProcessor.Domain.Security;

/// <summary>Centralized masking so sensitive values are rendered the same way everywhere (logs, reports, UI).</summary>
public static class Masking
{
    public const string RestrictedPlaceholder = "[restricted]";

    /// <summary>Masks all but the last four characters. Short values are fully masked.</summary>
    public static string MaskTail(string? value, int visibleTail = 4)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= visibleTail)
        {
            return new string('*', value.Length);
        }

        return string.Concat(new string('*', Math.Min(value.Length - visibleTail, 8)), value.AsSpan(value.Length - visibleTail));
    }

    public static string ForClassification(string? value, SensitivityClassification classification) => classification switch
    {
        SensitivityClassification.None => value ?? string.Empty,
        SensitivityClassification.Sensitive => MaskTail(value),
        _ => RestrictedPlaceholder,
    };

    /// <summary>Describes the shape of a value (length and character classes) without revealing it. Safe for conversion errors.</summary>
    public static string DescribeShape(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value.Length == 0)
        {
            return "empty";
        }

        bool digits = false, letters = false, spaces = false, other = false;
        foreach (char c in value)
        {
            if (char.IsDigit(c))
            {
                digits = true;
            }
            else if (char.IsLetter(c))
            {
                letters = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                spaces = true;
            }
            else
            {
                other = true;
            }
        }

        var classes = new List<string>(4);
        if (digits)
        {
            classes.Add("digits");
        }

        if (letters)
        {
            classes.Add("letters");
        }

        if (spaces)
        {
            classes.Add("whitespace");
        }

        if (other)
        {
            classes.Add("symbols");
        }

        return $"length {value.Length}, {string.Join('+', classes)}";
    }
}
