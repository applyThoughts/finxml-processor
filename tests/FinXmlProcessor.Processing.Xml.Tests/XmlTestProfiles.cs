using FinXmlProcessor.Application.Profiles;

namespace FinXmlProcessor.Processing.Xml.Tests;

internal static class XmlTestProfiles
{
    public static string DemoProfilePath => Path.Combine(AppContext.BaseDirectory, "samples", "profiles", "demo-fintech-v1.json");

    public static string DemoInputPath => Path.Combine(AppContext.BaseDirectory, "samples", "input", "demo-transactions.xml");

    public static CompiledProfile Demo()
    {
        ProfileValidationResult result = new ProfileLoader().Load(File.ReadAllText(DemoProfilePath), Path.GetDirectoryName(DemoProfilePath));
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        return result.Profile!;
    }

    public static CompiledProfile Simple(string ns = "urn:test", string? xsdPath = null)
    {
        var model = new MappingProfile
        {
            Id = "simple-test",
            DisplayName = "Simple",
            Version = "1.0.0",
            Namespaces = new Dictionary<string, string>(StringComparer.Ordinal) { ["p"] = ns },
            RecordPath = ["p:Batch", "p:Items", "p:Item"],
            XsdPath = xsdPath,
            SafeIdentifierField = "id",
            Tables =
            [
                new ProfileTable
                {
                    Id = "items",
                    SheetName = "Items",
                    Columns =
                    [
                        new ProfileColumn { Id = "id", Heading = "ID", CellType = "text" },
                        new ProfileColumn { Id = "amount", Heading = "Amount", CellType = "decimal" },
                        new ProfileColumn { Id = "flag", Heading = "Flag", CellType = "boolean" },
                        new ProfileColumn { Id = "note", Heading = "Note", CellType = "text" },
                    ],
                },
            ],
            Fields =
            [
                new ProfileField { Id = "id", Source = "p:Id", Table = "items", Column = "id", Required = true },
                new ProfileField { Id = "amount", Source = "p:Amount", Table = "items", Column = "amount", Required = true },
                new ProfileField { Id = "flag", Source = "@flag", Table = "items", Column = "flag" },
                new ProfileField { Id = "note", Source = "p:Nested/p:Note", Table = "items", Column = "note", Transforms = [new ProfileTransform { Type = "normalizeWhitespace" }] },
            ],
        };
        ProfileValidationResult result = ProfileLoader.Compile(model, baseDirectory: AppContext.BaseDirectory);
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        return result.Profile!;
    }

    public static string WriteTemp(string content, string extension = ".xml")
    {
        string path = Path.Combine(Path.GetTempPath(), "finxml-tests", Guid.NewGuid().ToString("N") + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
