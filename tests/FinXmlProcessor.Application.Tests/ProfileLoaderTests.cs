using System.Text.Json;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Application.Tests.Helpers;
using FinXmlProcessor.Domain.Cells;
using FinXmlProcessor.Domain.Tables;

namespace FinXmlProcessor.Application.Tests;

public class ProfileLoaderTests
{
    private readonly ProfileLoader _loader = new();

    [Fact]
    public void Demo_profile_loads_and_compiles()
    {
        CompiledProfile profile = TestProfiles.Demo();
        profile.Id.Should().Be("demo-fintech-v1");
        profile.IsSynthetic.Should().BeTrue();
        profile.RecordPath.Should().HaveCount(3);
        profile.RecordElementName.LocalName.Should().Be("Transaction");
        profile.RecordElementName.NamespaceName.Should().Be("urn:example:fintech:demo:v1");
        profile.Tables.Should().ContainSingle().Which.Columns.Should().HaveCount(14);
        profile.Fields.Should().HaveCount(14);
        profile.HasDuplicateKey.Should().BeTrue();
        profile.SafeIdentifierFieldIndex.Should().Be(0);
        profile.Hash.Should().HaveLength(64);
        profile.Tables[0].Columns.Single(c => c.Id == "amount").Sensitivity.Should().Be(SensitivityClassification.Restricted);
        profile.Tables[0].Columns.Single(c => c.Id == "transactionId").Required.Should().BeTrue();
    }

    [Fact]
    public void Hash_is_stable_across_line_endings()
    {
        string json = File.ReadAllText(TestProfiles.DemoProfilePath);
        string lf = json.Replace("\r\n", "\n", StringComparison.Ordinal);
        string crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);
        _loader.Load(lf).Profile!.Hash.Should().Be(_loader.Load(crlf).Profile!.Hash);
    }

    [Fact]
    public void Invalid_json_is_reported_not_thrown()
    {
        ProfileValidationResult result = _loader.Load("{ not json");
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().StartWith("Profile is not valid JSON");
    }

    [Fact]
    public void Schema_violations_are_reported_with_location()
    {
        string json = """
            { "schemaVersion": 1, "id": "BAD ID", "displayName": "x", "version": "1", "recordPath": [], "tables": [], "fields": [] }
            """;
        ProfileValidationResult result = _loader.Load(json);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("/id", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("/version", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_properties_are_rejected_by_schema()
    {
        MappingProfile model = TestProfiles.Minimal();
        string json = JsonSerializer.Serialize(model).Replace("\"displayName\"", "\"surprise\":1,\"displayName\"", StringComparison.Ordinal);
        _loader.Load(json).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Undeclared_namespace_prefix_is_a_semantic_error()
    {
        MappingProfile model = TestProfiles.Minimal();
        model.Fields[0].Source = "x:Id";
        ProfileValidationResult result = ProfileLoader.Compile(model);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("undeclared namespace prefix 'x'");
    }

    [Fact]
    public void Cross_references_are_validated()
    {
        MappingProfile model = TestProfiles.Minimal();
        model.Fields[1].Column = "nope";
        model.SafeIdentifierField = "missing";
        model.DuplicateKeyFields = ["ghost"];
        ProfileValidationResult result = ProfileLoader.Compile(model);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("unknown column 'nope'", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("safeIdentifierField 'missing'", StringComparison.Ordinal));
        result.Errors.Should().Contain(e => e.Contains("duplicateKeyFields entry 'ghost'", StringComparison.Ordinal));
    }

    [Fact]
    public void Safe_identifier_must_not_be_sensitive()
    {
        MappingProfile model = TestProfiles.Minimal();
        model.SafeIdentifierField = "note";
        ProfileValidationResult result = ProfileLoader.Compile(model);
        result.Errors.Should().ContainSingle().Which.Should().Contain("classified 'none'");
    }

    [Fact]
    public void Date_columns_require_declared_formats()
    {
        MappingProfile model = TestProfiles.Minimal();
        model.Fields.Single(f => f.Id == "when").Parse = null;
        ProfileValidationResult result = ProfileLoader.Compile(model);
        result.Errors.Should().ContainSingle().Which.Should().Contain("parse.dateFormats");
    }

    [Fact]
    public void Type_incompatible_validation_rules_are_rejected()
    {
        MappingProfile model = TestProfiles.Minimal();
        model.Fields.Single(f => f.Id == "amount").Validation = new ProfileValidation { Pattern = "[0-9]+" };
        model.Fields.Single(f => f.Id == "id").Validation = new ProfileValidation { Min = 1 };
        ProfileValidationResult result = ProfileLoader.Compile(model);
        result.Errors.Should().HaveCount(2);
    }

    [Fact]
    public void Constant_field_needs_no_source_and_a_column_cannot_be_bound_twice()
    {
        MappingProfile model = TestProfiles.Minimal();
        model.Fields.Add(new ProfileField { Id = "const", Table = "items", Column = "note", Transforms = [new ProfileTransform { Type = "constant", Value = "X" }] });
        ProfileValidationResult result = ProfileLoader.Compile(model);
        result.Errors.Should().ContainSingle().Which.Should().Contain("bound by more than one field");
    }

    [Fact]
    public void Reserved_sheet_names_are_rejected()
    {
        MappingProfile model = TestProfiles.Minimal();
        model.Tables[0].SheetName = "Summary";
        ProfileLoader.Compile(model).Errors.Should().ContainSingle().Which.Should().Contain("reserved");
    }

    [Fact]
    public void Relative_path_grammar_is_enforced_by_schema()
    {
        MappingProfile model = TestProfiles.Minimal();
        model.Fields[0].Source = "../Escape";
        _loader.Load(JsonSerializer.Serialize(model)).IsValid.Should().BeFalse();
        model.Fields[0].Source = "Item[1]";
        _loader.Load(JsonSerializer.Serialize(model)).IsValid.Should().BeFalse();
        model.Fields[0].Source = "Parent/Child/@attr";
        _loader.Load(JsonSerializer.Serialize(model)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Compiled_cell_types_follow_columns()
    {
        CompiledProfile profile = TestProfiles.CompileMinimal();
        profile.Fields.Single(f => f.Id == "amount").CellType.Should().Be(CellType.Decimal);
        profile.Fields.Single(f => f.Id == "flag").Source!.Attribute.Should().NotBeNull();
        profile.Fields.Single(f => f.Id == "flag").Source!.Attribute!.LocalName.Should().Be("flag");
        profile.Fields.Single(f => f.Id == "id").Source!.Elements.Single().NamespaceName.Should().Be("urn:test");
    }

    [Fact]
    public void Missing_xsd_is_reported()
    {
        MappingProfile model = TestProfiles.Minimal();
        model.XsdPath = "does-not-exist.xsd";
        ProfileLoader.Compile(model, baseDirectory: AppContext.BaseDirectory).Errors.Should().ContainSingle().Which.Should().Contain("xsdPath");
    }
}
