using System.Globalization;
using System.Text.RegularExpressions;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Domain.Cells;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Tables;

namespace FinXmlProcessor.Application.Validation;

/// <summary>
/// Applies the field rules declared in the profile to already-typed rows. Required-ness is enforced by the
/// mapper (a missing required value is a conversion failure); this validator covers everything after that.
/// </summary>
public sealed class ProfileRecordValidator : IRecordValidator
{
    private readonly CompiledProfile _profile;
    private readonly Dictionary<string, List<CompiledField>> _fieldsByTable;

    public ProfileRecordValidator(CompiledProfile profile)
    {
        _profile = profile;
        _fieldsByTable = profile.Fields
            .Where(f => f.Validation is { HasRules: true })
            .GroupBy(f => profile.Tables[f.TableIndex].Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    public void Validate(IReadOnlyList<OutputRow> rows, long sourceOrdinal, ICollection<RecordIssue> issues)
    {
        if (_fieldsByTable.Count == 0)
        {
            return;
        }

        foreach (OutputRow row in rows)
        {
            if (!_fieldsByTable.TryGetValue(row.TableId, out List<CompiledField>? fields))
            {
                continue;
            }

            foreach (CompiledField field in fields)
            {
                CellValue cell = row.Cells[field.ColumnIndex];
                if (cell.IsBlank)
                {
                    continue;
                }

                ValidateCell(field, cell, sourceOrdinal, issues);
            }
        }
    }

    private static void ValidateCell(CompiledField field, CellValue cell, long sourceOrdinal, ICollection<RecordIssue> issues)
    {
        CompiledValidation rules = field.Validation!;
        switch (cell.Type)
        {
            case CellType.Text:
                {
                    string text = cell.TextValue;
                    if (rules.MinLength.HasValue && text.Length < rules.MinLength.Value)
                    {
                        issues.Add(RecordIssue.Rejection(IssueCodes.ValMinLength, field.Id, $"Value shorter than minimum length {rules.MinLength.Value} (actual {text.Length}).", sourceOrdinal));
                    }

                    if (rules.MaxLength.HasValue && text.Length > rules.MaxLength.Value)
                    {
                        issues.Add(RecordIssue.Rejection(IssueCodes.ValMaxLength, field.Id, $"Value longer than maximum length {rules.MaxLength.Value} (actual {text.Length}).", sourceOrdinal));
                    }

                    if (rules.Pattern is not null)
                    {
                        bool matches;
                        try
                        {
                            matches = rules.Pattern.IsMatch(text);
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            matches = false;
                        }

                        if (!matches)
                        {
                            issues.Add(RecordIssue.Rejection(IssueCodes.ValPattern, field.Id, "Value does not match the required pattern.", sourceOrdinal));
                        }
                    }

                    if (rules.AllowedValues.Count > 0)
                    {
                        StringComparison comparison = rules.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                        bool allowed = false;
                        foreach (string candidate in rules.AllowedValues)
                        {
                            if (string.Equals(text, candidate, comparison))
                            {
                                allowed = true;
                                break;
                            }
                        }

                        if (!allowed)
                        {
                            issues.Add(RecordIssue.Rejection(IssueCodes.ValAllowedValues, field.Id, "Value is not one of the allowed values.", sourceOrdinal));
                        }
                    }

                    break;
                }

            case CellType.Integer:
            case CellType.Decimal:
                {
                    decimal number = cell.Type == CellType.Integer ? cell.IntegerValue : cell.DecimalValue;
                    if (rules.Min.HasValue && number < rules.Min.Value)
                    {
                        issues.Add(RecordIssue.Rejection(IssueCodes.ValDecimalRange, field.Id, $"Value is below the minimum {rules.Min.Value.ToString(CultureInfo.InvariantCulture)}.", sourceOrdinal));
                    }

                    if (rules.Max.HasValue && number > rules.Max.Value)
                    {
                        issues.Add(RecordIssue.Rejection(IssueCodes.ValDecimalRange, field.Id, $"Value is above the maximum {rules.Max.Value.ToString(CultureInfo.InvariantCulture)}.", sourceOrdinal));
                    }

                    break;
                }

            case CellType.Date:
            case CellType.DateTime:
                {
                    DateOnly date = cell.Type == CellType.Date ? cell.DateValue : DateOnly.FromDateTime(cell.DateTimeValue);
                    if (rules.MinDate.HasValue && date < rules.MinDate.Value)
                    {
                        issues.Add(RecordIssue.Rejection(IssueCodes.ValDateRange, field.Id, $"Date is before the minimum {rules.MinDate.Value:yyyy-MM-dd}.", sourceOrdinal));
                    }

                    if (rules.MaxDate.HasValue && date > rules.MaxDate.Value)
                    {
                        issues.Add(RecordIssue.Rejection(IssueCodes.ValDateRange, field.Id, $"Date is after the maximum {rules.MaxDate.Value:yyyy-MM-dd}.", sourceOrdinal));
                    }

                    break;
                }

            case CellType.Boolean:
            default:
                break;
        }
    }
}
