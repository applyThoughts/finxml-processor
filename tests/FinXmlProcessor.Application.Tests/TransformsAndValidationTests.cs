using System.Xml.Linq;
using FinXmlProcessor.Application.Mapping;
using FinXmlProcessor.Application.Naming;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Application.Scheduling;
using FinXmlProcessor.Application.Tests.Helpers;
using FinXmlProcessor.Application.Validation;
using FinXmlProcessor.Domain.Cells;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Tables;
using NodaTime;

namespace FinXmlProcessor.Application.Tests;

public class TransformsAndValidationTests
{
    [Fact]
    public void Transforms_apply_in_order()
    {
        var transforms = new List<CompiledTransform>
        {
            new(TransformKind.NormalizeWhitespace, null, [], string.Empty),
            new(TransformKind.Upper, null, [], string.Empty),
        };
        TextTransforms.Apply("  hello \t\n world ", transforms, null).Should().Be("HELLO WORLD");
        TextTransforms.Apply(null, transforms, null).Should().BeNull();
    }

    [Fact]
    public void Constant_replaces_and_concat_appends_from_record()
    {
        var ns = new Dictionary<string, string>(StringComparer.Ordinal);
        var record = new XElement("R", new XElement("A", "x"), new XElement("B", "y"));
        var concat = new CompiledTransform(TransformKind.Concat, null, [XmlPath.Parse("A", ns), XmlPath.Parse("B", ns)], "-");
        TextTransforms.Apply("p", [concat], record).Should().Be("p-x-y");
        TextTransforms.Apply(null, [concat], record).Should().Be("x-y");
        TextTransforms.Apply("anything", [new CompiledTransform(TransformKind.Constant, "K", [], string.Empty)], null).Should().Be("K");
    }

    [Fact]
    public void XmlPath_evaluates_elements_attributes_and_self()
    {
        var ns = new Dictionary<string, string>(StringComparer.Ordinal) { ["t"] = "urn:t" };
        XNamespace t = "urn:t";
        var record = new XElement(t + "Rec", new XAttribute("id", "7"), new XElement(t + "Amount", new XAttribute("currency", "USD"), "1.5"), "tail");
        XmlPath.Parse("t:Amount", ns).Evaluate(record).Should().Be("1.5");
        XmlPath.Parse("t:Amount/@currency", ns).Evaluate(record).Should().Be("USD");
        XmlPath.Parse("@id", ns).Evaluate(record).Should().Be("7");
        XmlPath.Parse("t:Missing", ns).Evaluate(record).Should().BeNull();
        XmlPath.Parse("Amount", ns).Evaluate(record).Should().BeNull("unprefixed names are not in the t namespace");
        FluentActions.Invoking(() => XmlPath.Parse("@a/b", ns)).Should().Throw<ProfileValidationException>();
    }

    [Fact]
    public void Validator_applies_length_pattern_allowed_range_and_date_rules()
    {
        CompiledProfile profile = TestProfiles.CompileMinimal(m =>
        {
            m.Fields.Single(f => f.Id == "id").Validation = new ProfileValidation { MinLength = 2, MaxLength = 4, Pattern = "[A-Z]+", AllowedValues = ["AB", "ABC"] };
            m.Fields.Single(f => f.Id == "amount").Validation = new ProfileValidation { Min = 0, Max = 100 };
            m.Fields.Single(f => f.Id == "when").Validation = new ProfileValidation { MinDate = "2026-01-01", MaxDate = "2026-12-31" };
        });
        var validator = new ProfileRecordValidator(profile);

        var good = Row(profile, "AB", 50m, new DateOnly(2026, 6, 1));
        var issues = new List<RecordIssue>();
        validator.Validate([good], 1, issues);
        issues.Should().BeEmpty();

        var bad = Row(profile, "abcde", 500m, new DateOnly(2027, 1, 1));
        validator.Validate([bad], 2, issues);
        issues.Select(i => i.Code).Should().BeEquivalentTo([IssueCodes.ValMaxLength, IssueCodes.ValPattern, IssueCodes.ValAllowedValues, IssueCodes.ValDecimalRange, IssueCodes.ValDateRange]);
        issues.Should().OnlyContain(i => i.Severity == IssueSeverity.RecordRejected && i.SourceOrdinal == 2);
        issues.Should().OnlyContain(i => !i.Message.Contains("abcde", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_skips_blank_cells()
    {
        CompiledProfile profile = TestProfiles.CompileMinimal(m => m.Fields.Single(f => f.Id == "id").Validation = new ProfileValidation { MinLength = 5 });
        var validator = new ProfileRecordValidator(profile);
        var row = new OutputRow("items", 1, null, profile.Tables[0].Columns.Select(c => CellValue.Blank(c.CellType)).ToList());
        var issues = new List<RecordIssue>();
        validator.Validate([row], 1, issues);
        issues.Should().BeEmpty();
    }

    [Fact]
    public void Output_names_are_deterministic_and_safe()
    {
        var id = Guid.Parse("5f3a9c1e-0000-0000-0000-000000000000");
        OutputNaming.WorkbookFileName("demo-fintech-v1", new DateOnly(2026, 9, 3), id).Should().Be("demo-fintech-v1_2026-09-03_5f3a9c1e.xlsx");
        OutputNaming.ReportFileName(new DateOnly(2026, 9, 3), id).Should().Be("report_2026-09-03_5f3a9c1e.json");
        OutputNaming.SanitizeFileNameComponent("../../etc/passwd").Should().NotContain("/").And.NotContain("\\");
        OutputNaming.SanitizeFileNameComponent("CON").Should().Be("_CON");
        OutputNaming.SanitizeFileNameComponent(".hidden").Should().Be("hidden");
        OutputNaming.SanitizeFileNameComponent("a\u0000b:c*d").Should().Be("a_b_c_d");
        OutputNaming.SanitizeFileNameComponent(new string('x', 500)).Length.Should().Be(120);
        OutputNaming.SafeFileNameFromPath("/remote/dir/../file name.xml").Should().Be("file name.xml");
        OutputNaming.SafeFileNameFromPath("C:\\temp\\x.xml").Should().Be("x.xml");
        OutputNaming.SafeFileNameFromPath("   ").Should().Be("unnamed");
    }

    [Fact]
    public void Business_date_uses_eastern_zone_regardless_of_host()
    {
        // 2026-09-04 02:30 UTC is still 2026-09-03 22:30 in New York (EDT, UTC-4).
        Instant instant = Instant.FromUtc(2026, 9, 4, 2, 30);
        BusinessCalendar.BusinessDateFor(instant).Should().Be(new DateOnly(2026, 9, 3));
        // 2026-01-15 04:30 UTC is 2026-01-14 23:30 EST (UTC-5).
        BusinessCalendar.BusinessDateFor(Instant.FromUtc(2026, 1, 15, 4, 30)).Should().Be(new DateOnly(2026, 1, 14));
        BusinessCalendar.BusinessDateFor(Instant.FromUtc(2026, 1, 15, 5, 0)).Should().Be(new DateOnly(2026, 1, 15));
    }

    private static OutputRow Row(CompiledProfile profile, string id, decimal amount, DateOnly when)
    {
        OutputTableDefinition table = profile.Tables[0];
        var cells = table.Columns.Select(c => CellValue.Blank(c.CellType)).ToArray();
        cells[table.IndexOfColumn("id")] = CellValue.FromText(id);
        cells[table.IndexOfColumn("amount")] = CellValue.FromDecimal(amount);
        cells[table.IndexOfColumn("when")] = CellValue.FromDate(when);
        return new OutputRow(table.Id, 1, id, cells);
    }
}
