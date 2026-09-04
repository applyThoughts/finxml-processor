using FinXmlProcessor.Application.Profiles;

namespace FinXmlProcessor.Application.Tests.Helpers;

public static class TestProfiles
{
    public static string DemoProfilePath => Path.Combine(AppContext.BaseDirectory, "samples", "profiles", "demo-fintech-v1.json");

    public static CompiledProfile Demo()
    {
        var loader = new ProfileLoader();
        ProfileValidationResult result = loader.Load(File.ReadAllText(DemoProfilePath), Path.GetDirectoryName(DemoProfilePath));
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        return result.Profile!;
    }

    /// <summary>A minimal single-table profile with an unprefixed default namespace, built in code.</summary>
    public static MappingProfile Minimal(string ns = "urn:test") => new()
    {
        Id = "minimal-test",
        DisplayName = "Minimal",
        Version = "1.0.0",
        Namespaces = new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = ns },
        RecordPath = ["Batch", "Items", "Item"],
        SafeIdentifierField = "id",
        DuplicateKeyFields = ["id"],
        Tables =
        [
            new ProfileTable
            {
                Id = "items",
                SheetName = "Items",
                Columns =
                [
                    new ProfileColumn { Id = "id", Heading = "ID", CellType = "text" },
                    new ProfileColumn { Id = "amount", Heading = "Amount", CellType = "decimal", NumberFormat = "#,##0.00" },
                    new ProfileColumn { Id = "when", Heading = "When", CellType = "date" },
                    new ProfileColumn { Id = "flag", Heading = "Flag", CellType = "boolean" },
                    new ProfileColumn { Id = "count", Heading = "Count", CellType = "integer" },
                    new ProfileColumn { Id = "note", Heading = "Note", CellType = "text", Sensitivity = "sensitive", AllowInRejectionOutput = false },
                ],
            },
        ],
        Fields =
        [
            new ProfileField { Id = "id", Source = "Id", Table = "items", Column = "id", Required = true, Validation = new ProfileValidation { MaxLength = 10 } },
            new ProfileField { Id = "amount", Source = "Amount", Table = "items", Column = "amount", Required = true, Validation = new ProfileValidation { Min = 0 } },
            new ProfileField { Id = "when", Source = "When", Table = "items", Column = "when", Parse = new ProfileParseOptions { DateFormats = ["yyyy-MM-dd"] } },
            new ProfileField { Id = "flag", Source = "@flag", Table = "items", Column = "flag" },
            new ProfileField { Id = "count", Source = "Count", Table = "items", Column = "count" },
            new ProfileField { Id = "note", Source = "Note", Table = "items", Column = "note" },
        ],
    };

    public static CompiledProfile CompileMinimal(Action<MappingProfile>? customize = null)
    {
        MappingProfile model = Minimal();
        customize?.Invoke(model);
        ProfileValidationResult result = ProfileLoader.Compile(model);
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        return result.Profile!;
    }
}
