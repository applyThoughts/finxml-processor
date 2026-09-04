using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FinXmlProcessor.Domain.Cells;
using FinXmlProcessor.Domain.Tables;
using Json.Schema;

namespace FinXmlProcessor.Application.Profiles;

public sealed record ProfileValidationResult(bool IsValid, IReadOnlyList<string> Errors, CompiledProfile? Profile)
{
    public static ProfileValidationResult Failure(IReadOnlyList<string> errors) => new(false, errors, null);

    public static ProfileValidationResult Success(CompiledProfile profile) => new(true, [], profile);
}

/// <summary>
/// Parses profile JSON, validates it against the embedded JSON Schema, applies semantic checks the schema cannot
/// express (cross references, namespace prefixes, type-compatible rules) and compiles it for runtime use.
/// </summary>
public sealed class ProfileLoader
{
    private const string SchemaResourceName = "FinXmlProcessor.Application.mapping-profile.schema.json";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions JsonOptions = new() { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly Lazy<JsonSchema> Schema = new(LoadSchema);

    public static string SchemaJson
    {
        get
        {
            using Stream stream = typeof(ProfileLoader).Assembly.GetManifestResourceStream(SchemaResourceName)
                ?? throw new InvalidOperationException("Embedded profile schema missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public async Task<ProfileValidationResult> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return ProfileValidationResult.Failure([$"Cannot read profile file: {ex.Message}"]);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ProfileValidationResult.Failure([$"Cannot read profile file: {ex.Message}"]);
        }

        return Load(json, Path.GetDirectoryName(Path.GetFullPath(path)));
    }

    public ProfileValidationResult Load(string json, string? baseDirectory = null)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            return ProfileValidationResult.Failure([$"Profile is not valid JSON: {ex.Message}"]);
        }

        using (document)
        {
            EvaluationResults evaluation = Schema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!evaluation.IsValid)
            {
                List<string> errors = (evaluation.Details ?? [])
                    .Where(d => d.Errors is { Count: > 0 })
                    .SelectMany(d => d.Errors!.Select(e => $"{(d.InstanceLocation.ToString() is { Length: > 0 } loc ? loc : "/")}: {e.Value}"))
                    .Distinct(StringComparer.Ordinal)
                    .Take(50)
                    .ToList();
                if (errors.Count == 0)
                {
                    errors.Add("Profile does not conform to the profile schema.");
                }

                return ProfileValidationResult.Failure(errors);
            }
        }

        MappingProfile? model;
        try
        {
            model = JsonSerializer.Deserialize<MappingProfile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return ProfileValidationResult.Failure([$"Profile could not be deserialized: {ex.Message}"]);
        }

        if (model is null)
        {
            return ProfileValidationResult.Failure(["Profile is empty."]);
        }

        return Compile(model, ProfileHasher.ComputeForText(json), baseDirectory);
    }

    public static ProfileValidationResult Compile(MappingProfile model, string? hash = null, string? baseDirectory = null)
    {
        var errors = new List<string>();
        hash ??= ProfileHasher.Compute(model);

        // Namespaces
        var namespaces = new Dictionary<string, string>(model.Namespaces, StringComparer.Ordinal);

        // Record path
        var recordPath = new List<XName>(model.RecordPath.Count);
        foreach (string segment in model.RecordPath)
        {
            try
            {
                recordPath.Add(XmlPath.ResolveQName(segment, namespaces, "recordPath", isAttribute: false));
            }
            catch (ProfileValidationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        // Tables and columns
        var tables = new List<OutputTableDefinition>(model.Tables.Count);
        var tableIds = new HashSet<string>(StringComparer.Ordinal);
        var sheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProfileTable table in model.Tables)
        {
            if (!tableIds.Add(table.Id))
            {
                errors.Add($"Duplicate table id '{table.Id}'.");
            }

            if (!sheetNames.Add(table.SheetName))
            {
                errors.Add($"Duplicate sheet name '{table.SheetName}'.");
            }

            if (string.Equals(table.SheetName, "Summary", StringComparison.OrdinalIgnoreCase) || string.Equals(table.SheetName, "Rejected Records", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Sheet name '{table.SheetName}' is reserved.");
            }

            var columns = new List<OutputColumnDefinition>(table.Columns.Count);
            var columnIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ProfileColumn column in table.Columns)
            {
                if (!columnIds.Add(column.Id))
                {
                    errors.Add($"Table '{table.Id}' has duplicate column id '{column.Id}'.");
                }

                columns.Add(new OutputColumnDefinition(
                    column.Id,
                    column.Heading,
                    ParseCellType(column.CellType),
                    Required: false,
                    column.Width,
                    column.NumberFormat,
                    ParseSensitivity(column.Sensitivity),
                    column.AllowInRejectionOutput));
            }

            tables.Add(new OutputTableDefinition(table.Id, table.SheetName, columns));
        }

        // Fields
        var fields = new List<CompiledField>(model.Fields.Count);
        var fieldIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var boundColumns = new HashSet<(int, int)>();
        foreach (ProfileField field in model.Fields)
        {
            if (fieldIds.ContainsKey(field.Id))
            {
                errors.Add($"Duplicate field id '{field.Id}'.");
                continue;
            }

            int tableIndex = tables.FindIndex(t => string.Equals(t.Id, field.Table, StringComparison.Ordinal));
            if (tableIndex < 0)
            {
                errors.Add($"Field '{field.Id}' references unknown table '{field.Table}'.");
                continue;
            }

            int columnIndex = tables[tableIndex].IndexOfColumn(field.Column);
            if (columnIndex < 0)
            {
                errors.Add($"Field '{field.Id}' references unknown column '{field.Column}' in table '{field.Table}'.");
                continue;
            }

            if (!boundColumns.Add((tableIndex, columnIndex)))
            {
                errors.Add($"Column '{field.Table}.{field.Column}' is bound by more than one field.");
            }

            OutputColumnDefinition column = tables[tableIndex].Columns[columnIndex];
            if (column.Required != field.Required)
            {
                column = column with { Required = field.Required };
                var updated = tables[tableIndex].Columns.ToList();
                updated[columnIndex] = column;
                tables[tableIndex] = tables[tableIndex] with { Columns = updated };
            }

            XmlPath? source = null;
            bool hasConstant = field.Transforms.Any(t => string.Equals(t.Type, "constant", StringComparison.Ordinal));
            if (field.Source is not null)
            {
                try
                {
                    source = XmlPath.Parse(field.Source, namespaces);
                }
                catch (ProfileValidationException ex)
                {
                    errors.Add($"Field '{field.Id}': {ex.Message}");
                }
            }
            else if (!hasConstant)
            {
                errors.Add($"Field '{field.Id}' has no source and no constant transform.");
            }

            var transforms = new List<CompiledTransform>(field.Transforms.Count);
            foreach (ProfileTransform transform in field.Transforms)
            {
                TransformKind kind = transform.Type switch
                {
                    "upper" => TransformKind.Upper,
                    "lower" => TransformKind.Lower,
                    "trim" => TransformKind.Trim,
                    "normalizeWhitespace" => TransformKind.NormalizeWhitespace,
                    "constant" => TransformKind.Constant,
                    "concat" => TransformKind.Concat,
                    _ => throw new ProfileValidationException($"Unknown transform '{transform.Type}'."),
                };
                if (kind == TransformKind.Constant && transform.Value is null)
                {
                    errors.Add($"Field '{field.Id}': constant transform requires a value.");
                }

                var sources = new List<XmlPath>();
                if (kind == TransformKind.Concat)
                {
                    if (transform.Sources.Count == 0)
                    {
                        errors.Add($"Field '{field.Id}': concat transform requires at least one source.");
                    }

                    foreach (string extra in transform.Sources)
                    {
                        try
                        {
                            sources.Add(XmlPath.Parse(extra, namespaces));
                        }
                        catch (ProfileValidationException ex)
                        {
                            errors.Add($"Field '{field.Id}': {ex.Message}");
                        }
                    }
                }

                transforms.Add(new CompiledTransform(kind, transform.Value, sources, transform.Separator));
            }

            CompiledParseOptions parse = CompiledParseOptions.Default;
            if (field.Parse is not null)
            {
                CultureInfo culture = CultureInfo.InvariantCulture;
                if (!string.IsNullOrEmpty(field.Parse.Culture))
                {
                    try
                    {
                        culture = CultureInfo.GetCultureInfo(field.Parse.Culture, predefinedOnly: true);
                    }
                    catch (CultureNotFoundException)
                    {
                        errors.Add($"Field '{field.Id}': unknown culture '{field.Parse.Culture}'.");
                    }
                }

                parse = new CompiledParseOptions(field.Parse.DateFormats, culture, field.Parse.TrueValues, field.Parse.FalseValues, field.Parse.AllowThousands);
            }

            if (column.CellType is CellType.Date or CellType.DateTime && parse.DateFormats.Count == 0)
            {
                errors.Add($"Field '{field.Id}' maps to a {column.CellType} column but declares no parse.dateFormats.");
            }

            CompiledValidation? validation = null;
            if (field.Validation is not null)
            {
                validation = CompileValidation(field, column.CellType, errors);
            }

            fieldIds[field.Id] = fields.Count;
            fields.Add(new CompiledField(field.Id, source, tableIndex, columnIndex, column, field.Required, field.Default, field.Trim, transforms, parse, validation));
        }

        // Cross references
        int safeIdIndex = -1;
        if (!string.IsNullOrEmpty(model.SafeIdentifierField))
        {
            if (!fieldIds.TryGetValue(model.SafeIdentifierField, out safeIdIndex))
            {
                errors.Add($"safeIdentifierField '{model.SafeIdentifierField}' is not a declared field.");
                safeIdIndex = -1;
            }
            else if (fields[safeIdIndex].Column.Sensitivity != SensitivityClassification.None)
            {
                errors.Add($"safeIdentifierField '{model.SafeIdentifierField}' must map to a column classified 'none'.");
            }
        }

        var duplicateKey = new List<int>();
        foreach (string keyField in model.DuplicateKeyFields)
        {
            if (fieldIds.TryGetValue(keyField, out int index))
            {
                duplicateKey.Add(index);
            }
            else
            {
                errors.Add($"duplicateKeyFields entry '{keyField}' is not a declared field.");
            }
        }

        string? xsd = null;
        if (!string.IsNullOrWhiteSpace(model.XsdPath))
        {
            xsd = Path.IsPathRooted(model.XsdPath) || baseDirectory is null ? model.XsdPath : Path.GetFullPath(Path.Combine(baseDirectory, model.XsdPath));
            if (!File.Exists(xsd))
            {
                errors.Add($"xsdPath '{model.XsdPath}' does not exist.");
            }
        }

        if (errors.Count > 0)
        {
            return ProfileValidationResult.Failure(errors);
        }

        return ProfileValidationResult.Success(new CompiledProfile(model, hash, recordPath, tables, fields, safeIdIndex, duplicateKey, xsd));
    }

    private static CompiledValidation CompileValidation(ProfileField field, CellType cellType, List<string> errors)
    {
        ProfileValidation v = field.Validation!;
        Regex? regex = null;
        if (!string.IsNullOrEmpty(v.Pattern))
        {
            try
            {
                string anchored = v.Pattern.StartsWith('^') ? v.Pattern : "^(?:" + v.Pattern + ")$";
                regex = new Regex(anchored, RegexOptions.CultureInvariant | (v.CaseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None), RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                errors.Add($"Field '{field.Id}': invalid pattern ({ex.Message}).");
            }
        }

        if ((v.MinLength.HasValue || v.MaxLength.HasValue || regex is not null || v.AllowedValues.Count > 0) && cellType != CellType.Text)
        {
            errors.Add($"Field '{field.Id}': length/pattern/allowedValues rules apply to text columns only.");
        }

        if ((v.Min.HasValue || v.Max.HasValue) && cellType is not (CellType.Integer or CellType.Decimal))
        {
            errors.Add($"Field '{field.Id}': min/max rules apply to integer or decimal columns only.");
        }

        DateOnly? minDate = null, maxDate = null;
        if (v.MinDate is not null || v.MaxDate is not null)
        {
            if (cellType is not (CellType.Date or CellType.DateTime))
            {
                errors.Add($"Field '{field.Id}': minDate/maxDate rules apply to date or dateTime columns only.");
            }

            if (v.MinDate is not null)
            {
                if (DateOnly.TryParseExact(v.MinDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly min))
                {
                    minDate = min;
                }
                else
                {
                    errors.Add($"Field '{field.Id}': minDate is not yyyy-MM-dd.");
                }
            }

            if (v.MaxDate is not null)
            {
                if (DateOnly.TryParseExact(v.MaxDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly max))
                {
                    maxDate = max;
                }
                else
                {
                    errors.Add($"Field '{field.Id}': maxDate is not yyyy-MM-dd.");
                }
            }
        }

        if (v.MinLength > v.MaxLength)
        {
            errors.Add($"Field '{field.Id}': minLength exceeds maxLength.");
        }

        if (v.Min > v.Max)
        {
            errors.Add($"Field '{field.Id}': min exceeds max.");
        }

        return new CompiledValidation(v.MinLength, v.MaxLength, regex, v.AllowedValues, v.CaseInsensitive, v.Min, v.Max, minDate, maxDate);
    }

    private static CellType ParseCellType(string value) => value switch
    {
        "text" => CellType.Text,
        "integer" => CellType.Integer,
        "decimal" => CellType.Decimal,
        "date" => CellType.Date,
        "dateTime" => CellType.DateTime,
        "boolean" => CellType.Boolean,
        _ => throw new ProfileValidationException($"Unknown cellType '{value}'."),
    };

    private static SensitivityClassification ParseSensitivity(string value) => value switch
    {
        "none" => SensitivityClassification.None,
        "sensitive" => SensitivityClassification.Sensitive,
        "restricted" => SensitivityClassification.Restricted,
        _ => throw new ProfileValidationException($"Unknown sensitivity '{value}'."),
    };

    private static JsonSchema LoadSchema()
    {
        return JsonSchema.FromText(SchemaJson);
    }
}
