using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FinXmlProcessor.Domain.Cells;
using FinXmlProcessor.Domain.Tables;

namespace FinXmlProcessor.Application.Profiles;

/// <summary>Runtime form of a validated profile: resolved names, compiled regexes, index lookups. Immutable.</summary>
public sealed class CompiledProfile
{
    internal CompiledProfile(
        MappingProfile source,
        string hash,
        IReadOnlyList<XName> recordPath,
        IReadOnlyList<OutputTableDefinition> tables,
        IReadOnlyList<CompiledField> fields,
        int safeIdentifierFieldIndex,
        IReadOnlyList<int> duplicateKeyFieldIndexes,
        string? resolvedXsdPath)
    {
        Source = source;
        Hash = hash;
        RecordPath = recordPath;
        Tables = tables;
        Fields = fields;
        SafeIdentifierFieldIndex = safeIdentifierFieldIndex;
        DuplicateKeyFieldIndexes = duplicateKeyFieldIndexes;
        ResolvedXsdPath = resolvedXsdPath;
    }

    public MappingProfile Source { get; }

    public string Id => Source.Id;

    public string Version => Source.Version;

    public string DisplayName => Source.DisplayName;

    public string MapperType => Source.MapperType;

    public bool IsSynthetic => Source.IsSynthetic;

    /// <summary>SHA-256 (hex) of the canonical profile JSON. Recorded on every job.</summary>
    public string Hash { get; }

    public IReadOnlyList<XName> RecordPath { get; }

    public XName RecordElementName => RecordPath[^1];

    public IReadOnlyList<OutputTableDefinition> Tables { get; }

    public IReadOnlyList<CompiledField> Fields { get; }

    public int SafeIdentifierFieldIndex { get; }

    public IReadOnlyList<int> DuplicateKeyFieldIndexes { get; }

    public bool HasDuplicateKey => DuplicateKeyFieldIndexes.Count > 0;

    public string? ResolvedXsdPath { get; }

    public OutputTableDefinition TableById(string id) => Tables.First(t => string.Equals(t.Id, id, StringComparison.Ordinal));
}

public enum TransformKind
{
    Upper,
    Lower,
    Trim,
    NormalizeWhitespace,
    Constant,
    Concat,
}

public sealed record CompiledTransform(TransformKind Kind, string? Value, IReadOnlyList<XmlPath> Sources, string Separator);

public sealed class CompiledParseOptions
{
    public static CompiledParseOptions Default { get; } = new([], CultureInfo.InvariantCulture, [], [], false);

    public CompiledParseOptions(IReadOnlyList<string> dateFormats, CultureInfo culture, IReadOnlyList<string> trueValues, IReadOnlyList<string> falseValues, bool allowThousands)
    {
        DateFormats = dateFormats;
        Culture = culture;
        TrueValues = trueValues;
        FalseValues = falseValues;
        AllowThousands = allowThousands;
    }

    public IReadOnlyList<string> DateFormats { get; }

    public CultureInfo Culture { get; }

    public IReadOnlyList<string> TrueValues { get; }

    public IReadOnlyList<string> FalseValues { get; }

    public bool AllowThousands { get; }
}

public sealed record CompiledValidation(
    int? MinLength,
    int? MaxLength,
    Regex? Pattern,
    IReadOnlyList<string> AllowedValues,
    bool CaseInsensitive,
    decimal? Min,
    decimal? Max,
    DateOnly? MinDate,
    DateOnly? MaxDate)
{
    public bool HasRules => MinLength.HasValue || MaxLength.HasValue || Pattern is not null || AllowedValues.Count > 0
        || Min.HasValue || Max.HasValue || MinDate.HasValue || MaxDate.HasValue;
}

public sealed class CompiledField
{
    public CompiledField(
        string id,
        XmlPath? source,
        int tableIndex,
        int columnIndex,
        OutputColumnDefinition column,
        bool required,
        string? defaultValue,
        bool trim,
        IReadOnlyList<CompiledTransform> transforms,
        CompiledParseOptions parse,
        CompiledValidation? validation)
    {
        Id = id;
        Source = source;
        TableIndex = tableIndex;
        ColumnIndex = columnIndex;
        Column = column;
        Required = required;
        DefaultValue = defaultValue;
        Trim = trim;
        Transforms = transforms;
        Parse = parse;
        Validation = validation;
    }

    public string Id { get; }

    public XmlPath? Source { get; }

    public int TableIndex { get; }

    public int ColumnIndex { get; }

    public OutputColumnDefinition Column { get; }

    public CellType CellType => Column.CellType;

    public bool Required { get; }

    public string? DefaultValue { get; }

    public bool Trim { get; }

    public IReadOnlyList<CompiledTransform> Transforms { get; }

    public CompiledParseOptions Parse { get; }

    public CompiledValidation? Validation { get; }
}
