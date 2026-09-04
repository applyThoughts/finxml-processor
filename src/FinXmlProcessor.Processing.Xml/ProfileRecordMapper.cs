using System.Xml.Linq;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Mapping;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Domain.Cells;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Sources;
using FinXmlProcessor.Domain.Tables;

namespace FinXmlProcessor.Processing.Xml;

/// <summary>
/// Declarative mapper driven entirely by the compiled profile. Produces exactly one row per output table in
/// table order so callers can index rows by table position. Never retains the record fragment.
/// </summary>
public sealed class ProfileRecordMapper : IRecordMapper
{
    private readonly CompiledProfile _profile;

    public ProfileRecordMapper(CompiledProfile profile)
    {
        _profile = profile;
    }

    public MappedRecord Map(SourceRecordEnvelope record)
    {
        XElement fragment = record.Fragment;
        long ordinal = record.SourceOrdinal;
        var cellsPerTable = new CellValue[_profile.Tables.Count][];
        for (int t = 0; t < _profile.Tables.Count; t++)
        {
            IReadOnlyList<OutputColumnDefinition> columns = _profile.Tables[t].Columns;
            var cells = new CellValue[columns.Count];
            for (int c = 0; c < cells.Length; c++)
            {
                cells[c] = CellValue.Blank(columns[c].CellType);
            }

            cellsPerTable[t] = cells;
        }

        List<RecordIssue>? issues = null;
        string? safeIdentifier = null;

        for (int f = 0; f < _profile.Fields.Count; f++)
        {
            CompiledField field = _profile.Fields[f];
            string? text = field.Source?.Evaluate(fragment);
            if (field.Transforms.Count > 0)
            {
                text = TextTransforms.Apply(text, field.Transforms, fragment);
            }

            if (field.Trim)
            {
                text = text?.Trim();
            }

            if (string.IsNullOrEmpty(text))
            {
                text = field.DefaultValue;
            }

            if (string.IsNullOrEmpty(text))
            {
                if (field.Required)
                {
                    (issues ??= []).Add(RecordIssue.Rejection(IssueCodes.MapRequiredMissing, field.Id, "Required value is missing or empty.", ordinal));
                }

                continue;
            }

            if (!CellConverter.TryConvert(text, field.CellType, field.Parse, out CellValue value, out string? code, out string? message))
            {
                (issues ??= []).Add(RecordIssue.Rejection(code!, field.Id, message!, ordinal));
                continue;
            }

            cellsPerTable[field.TableIndex][field.ColumnIndex] = value;
            if (f == _profile.SafeIdentifierFieldIndex)
            {
                safeIdentifier = value.ToInvariantString();
            }
        }

        var rows = new OutputRow[_profile.Tables.Count];
        for (int t = 0; t < rows.Length; t++)
        {
            rows[t] = new OutputRow(_profile.Tables[t].Id, ordinal, safeIdentifier, cellsPerTable[t]);
        }

        return new MappedRecord(rows, issues ?? (IReadOnlyList<RecordIssue>)[], safeIdentifier);
    }
}

public sealed class ProfileRecordMapperFactory : IRecordMapperFactory
{
    public string MapperType => "profile";

    public IRecordMapper Create(CompiledProfile profile) => new ProfileRecordMapper(profile);
}
