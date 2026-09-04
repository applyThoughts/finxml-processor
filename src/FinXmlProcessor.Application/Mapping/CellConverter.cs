using System.Globalization;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Domain.Cells;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Security;

namespace FinXmlProcessor.Application.Mapping;

/// <summary>
/// Converts source text into typed cells. Error messages describe the shape of the input, never the value,
/// so they are safe to log and report even for sensitive fields.
/// </summary>
public static class CellConverter
{
    public static bool TryConvert(string text, CellType type, CompiledParseOptions parse, out CellValue value, out string? errorCode, out string? errorMessage)
    {
        errorCode = null;
        errorMessage = null;
        switch (type)
        {
            case CellType.Text:
                value = CellValue.FromText(text);
                return true;

            case CellType.Integer:
                {
                    NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
                    if (parse.AllowThousands)
                    {
                        styles |= NumberStyles.AllowThousands;
                    }

                    if (long.TryParse(text, styles, parse.Culture, out long l))
                    {
                        value = CellValue.FromInteger(l);
                        return true;
                    }

                    value = CellValue.Blank(type);
                    errorCode = IssueCodes.MapInvalidInteger;
                    errorMessage = $"Value is not a valid integer ({Masking.DescribeShape(text)}).";
                    return false;
                }

            case CellType.Decimal:
                {
                    NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
                    if (parse.AllowThousands)
                    {
                        styles |= NumberStyles.AllowThousands;
                    }

                    if (decimal.TryParse(text, styles, parse.Culture, out decimal d))
                    {
                        value = CellValue.FromDecimal(d);
                        return true;
                    }

                    value = CellValue.Blank(type);
                    errorCode = IssueCodes.MapInvalidDecimal;
                    errorMessage = $"Value is not a valid decimal ({Masking.DescribeShape(text)}).";
                    return false;
                }

            case CellType.Date:
                {
                    foreach (string format in parse.DateFormats)
                    {
                        if (DateTime.TryParseExact(text, format, parse.Culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AdjustToUniversal, out DateTime dt))
                        {
                            value = CellValue.FromDate(DateOnly.FromDateTime(dt));
                            return true;
                        }
                    }

                    value = CellValue.Blank(type);
                    errorCode = IssueCodes.MapInvalidDate;
                    errorMessage = $"Value does not match any declared date format ({Masking.DescribeShape(text)}).";
                    return false;
                }

            case CellType.DateTime:
                {
                    foreach (string format in parse.DateFormats)
                    {
                        // Offsets are normalised to UTC so a workbook never mixes zones silently.
                        if (DateTimeOffset.TryParseExact(text, format, parse.Culture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
                        {
                            value = CellValue.FromDateTime(DateTime.SpecifyKind(dto.UtcDateTime, DateTimeKind.Unspecified));
                            return true;
                        }
                    }

                    value = CellValue.Blank(type);
                    errorCode = IssueCodes.MapInvalidDateTime;
                    errorMessage = $"Value does not match any declared date-time format ({Masking.DescribeShape(text)}).";
                    return false;
                }

            case CellType.Boolean:
                {
                    if (MatchesAny(text, parse.TrueValues) || (parse.TrueValues.Count == 0 && IsDefaultTrue(text)))
                    {
                        value = CellValue.FromBoolean(true);
                        return true;
                    }

                    if (MatchesAny(text, parse.FalseValues) || (parse.FalseValues.Count == 0 && IsDefaultFalse(text)))
                    {
                        value = CellValue.FromBoolean(false);
                        return true;
                    }

                    value = CellValue.Blank(type);
                    errorCode = IssueCodes.MapInvalidBoolean;
                    errorMessage = $"Value is not a recognised boolean ({Masking.DescribeShape(text)}).";
                    return false;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown cell type.");
        }
    }

    private static bool MatchesAny(string text, IReadOnlyList<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            if (string.Equals(text, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDefaultTrue(string text) =>
        text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("1", StringComparison.Ordinal)
        || text.Equals("y", StringComparison.OrdinalIgnoreCase) || text.Equals("yes", StringComparison.OrdinalIgnoreCase);

    private static bool IsDefaultFalse(string text) =>
        text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.Equals("0", StringComparison.Ordinal)
        || text.Equals("n", StringComparison.OrdinalIgnoreCase) || text.Equals("no", StringComparison.OrdinalIgnoreCase);
}
