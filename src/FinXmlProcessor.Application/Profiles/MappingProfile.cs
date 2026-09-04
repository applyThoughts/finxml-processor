using System.Text.Json.Serialization;

namespace FinXmlProcessor.Application.Profiles;

/// <summary>
/// The on-disk mapping profile (JSON). This is a data contract only; see <see cref="CompiledProfile"/> for the
/// resolved, validated form used at runtime. Keep property names stable: they are part of the profile schema.
/// </summary>
public sealed class MappingProfile
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>True for shipped demo profiles so the UI can flag that rules are synthetic.</summary>
    [JsonPropertyName("isSynthetic")]
    public bool IsSynthetic { get; set; }

    /// <summary>Selects the <c>IRecordMapperFactory</c>. "profile" is the built-in declarative mapper.</summary>
    [JsonPropertyName("mapperType")]
    public string MapperType { get; set; } = "profile";

    /// <summary>Prefix to namespace URI. Use an empty-string key for the default namespace of unprefixed names.</summary>
    [JsonPropertyName("namespaces")]
    public Dictionary<string, string> Namespaces { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Absolute element path from the document root to the repeating record element, one segment per entry.</summary>
    [JsonPropertyName("recordPath")]
    public List<string> RecordPath { get; set; } = [];

    /// <summary>Optional XSD path (absolute, or relative to the profile file).</summary>
    [JsonPropertyName("xsdPath")]
    public string? XsdPath { get; set; }

    /// <summary>Field whose value may identify a record in reports and rejection output. Must be classified "none".</summary>
    [JsonPropertyName("safeIdentifierField")]
    public string? SafeIdentifierField { get; set; }

    /// <summary>Fields forming the composite record duplicate key. Empty disables record duplicate detection.</summary>
    [JsonPropertyName("duplicateKeyFields")]
    public List<string> DuplicateKeyFields { get; set; } = [];

    [JsonPropertyName("tables")]
    public List<ProfileTable> Tables { get; set; } = [];

    [JsonPropertyName("fields")]
    public List<ProfileField> Fields { get; set; } = [];
}

public sealed class ProfileTable
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("sheetName")]
    public string SheetName { get; set; } = string.Empty;

    [JsonPropertyName("columns")]
    public List<ProfileColumn> Columns { get; set; } = [];
}

public sealed class ProfileColumn
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("heading")]
    public string Heading { get; set; } = string.Empty;

    /// <summary>text | integer | decimal | date | dateTime | boolean</summary>
    [JsonPropertyName("cellType")]
    public string CellType { get; set; } = "text";

    [JsonPropertyName("width")]
    public double? Width { get; set; }

    /// <summary>Excel number format code, e.g. "#,##0.00" or "yyyy-mm-dd".</summary>
    [JsonPropertyName("numberFormat")]
    public string? NumberFormat { get; set; }

    /// <summary>none | sensitive | restricted</summary>
    [JsonPropertyName("sensitivity")]
    public string Sensitivity { get; set; } = "none";

    [JsonPropertyName("allowInRejectionOutput")]
    public bool AllowInRejectionOutput { get; set; } = true;
}

public sealed class ProfileField
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Path relative to the record element: "t:Amount", "t:Header/t:Id", "t:Amount/@currency", "@id" or "." for the
    /// record element text. Omitted for constant fields.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("table")]
    public string Table { get; set; } = string.Empty;

    [JsonPropertyName("column")]
    public string Column { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>Text used when the source is missing or empty (applied before parsing).</summary>
    [JsonPropertyName("default")]
    public string? Default { get; set; }

    [JsonPropertyName("trim")]
    public bool Trim { get; set; } = true;

    [JsonPropertyName("transforms")]
    public List<ProfileTransform> Transforms { get; set; } = [];

    [JsonPropertyName("parse")]
    public ProfileParseOptions? Parse { get; set; }

    [JsonPropertyName("validation")]
    public ProfileValidation? Validation { get; set; }
}

public sealed class ProfileTransform
{
    /// <summary>upper | lower | trim | normalizeWhitespace | constant | concat</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>For "constant".</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>For "concat": additional relative source paths appended after the primary source.</summary>
    [JsonPropertyName("sources")]
    public List<string> Sources { get; set; } = [];

    [JsonPropertyName("separator")]
    public string Separator { get; set; } = string.Empty;
}

public sealed class ProfileParseOptions
{
    /// <summary>Exact .NET custom format strings tried in order for date and date-time fields.</summary>
    [JsonPropertyName("dateFormats")]
    public List<string> DateFormats { get; set; } = [];

    /// <summary>Culture name for decimal/integer parsing; defaults to invariant.</summary>
    [JsonPropertyName("culture")]
    public string? Culture { get; set; }

    [JsonPropertyName("trueValues")]
    public List<string> TrueValues { get; set; } = [];

    [JsonPropertyName("falseValues")]
    public List<string> FalseValues { get; set; } = [];

    /// <summary>Whether thousands separators are accepted for numeric parsing. Default false to stay strict.</summary>
    [JsonPropertyName("allowThousands")]
    public bool AllowThousands { get; set; }
}

public sealed class ProfileValidation
{
    [JsonPropertyName("minLength")]
    public int? MinLength { get; set; }

    [JsonPropertyName("maxLength")]
    public int? MaxLength { get; set; }

    /// <summary>.NET regular expression, anchored by the validator, evaluated with a timeout.</summary>
    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    [JsonPropertyName("allowedValues")]
    public List<string> AllowedValues { get; set; } = [];

    [JsonPropertyName("caseInsensitive")]
    public bool CaseInsensitive { get; set; }

    [JsonPropertyName("min")]
    public decimal? Min { get; set; }

    [JsonPropertyName("max")]
    public decimal? Max { get; set; }

    /// <summary>ISO-8601 date (yyyy-MM-dd).</summary>
    [JsonPropertyName("minDate")]
    public string? MinDate { get; set; }

    [JsonPropertyName("maxDate")]
    public string? MaxDate { get; set; }
}
