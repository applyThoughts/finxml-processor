using System.Xml.Linq;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Domain.Cells;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Sources;

namespace FinXmlProcessor.Processing.Xml.Tests;

public class ProfileRecordMapperTests
{
    private static readonly XNamespace P = "urn:test";

    [Fact]
    public void Maps_elements_attributes_nested_paths_and_transforms()
    {
        CompiledProfile profile = XmlTestProfiles.Simple();
        var mapper = new ProfileRecordMapper(profile);
        var record = new XElement(P + "Item", new XAttribute("flag", "Y"),
            new XElement(P + "Id", " ID-1 "),
            new XElement(P + "Amount", "12.345"),
            new XElement(P + "Nested", new XElement(P + "Note", "  multi   space\n text ")));

        MappedRecord mapped = mapper.Map(new SourceRecordEnvelope(7, 100, record));

        mapped.IsRejected.Should().BeFalse();
        mapped.Issues.Should().BeEmpty();
        mapped.SafeIdentifier.Should().Be("ID-1");
        mapped.Rows.Should().ContainSingle();
        IReadOnlyList<CellValue> cells = mapped.Rows[0].Cells;
        cells[0].TextValue.Should().Be("ID-1");
        cells[1].DecimalValue.Should().Be(12.345m);
        cells[2].BooleanValue.Should().BeTrue();
        cells[3].TextValue.Should().Be("multi space text");
        mapped.Rows[0].SourceOrdinal.Should().Be(7);
        mapped.Rows[0].TableId.Should().Be("items");
    }

    [Fact]
    public void Missing_required_field_rejects_the_record_but_still_returns_partial_row()
    {
        var mapper = new ProfileRecordMapper(XmlTestProfiles.Simple());
        var record = new XElement(P + "Item", new XElement(P + "Amount", "1"));
        MappedRecord mapped = mapper.Map(new SourceRecordEnvelope(1, 0, record));
        mapped.IsRejected.Should().BeTrue();
        mapped.Issues.Should().ContainSingle().Which.Should().Match<RecordIssue>(i => i.Code == IssueCodes.MapRequiredMissing && i.FieldId == "id" && i.SourceOrdinal == 1);
        mapped.Rows[0].Cells[0].IsBlank.Should().BeTrue();
        mapped.Rows[0].Cells[1].DecimalValue.Should().Be(1m);
        mapped.SafeIdentifier.Should().BeNull();
    }

    [Fact]
    public void Conversion_failures_are_reported_per_field_without_values()
    {
        var mapper = new ProfileRecordMapper(XmlTestProfiles.Simple());
        var record = new XElement(P + "Item", new XAttribute("flag", "maybe"), new XElement(P + "Id", "X"), new XElement(P + "Amount", "12,34.5.6"));
        MappedRecord mapped = mapper.Map(new SourceRecordEnvelope(3, 0, record));
        mapped.IsRejected.Should().BeTrue();
        mapped.Issues.Select(i => i.Code).Should().BeEquivalentTo([IssueCodes.MapInvalidDecimal, IssueCodes.MapInvalidBoolean]);
        mapped.Issues.Should().OnlyContain(i => !i.Message.Contains("12,34.5.6", StringComparison.Ordinal));
    }

    [Fact]
    public void Optional_missing_values_stay_blank_and_defaults_apply()
    {
        CompiledProfile profile = XmlTestProfiles.Simple();
        var mapper = new ProfileRecordMapper(profile);
        var record = new XElement(P + "Item", new XElement(P + "Id", "X"), new XElement(P + "Amount", "0"));
        MappedRecord mapped = mapper.Map(new SourceRecordEnvelope(1, 0, record));
        mapped.IsRejected.Should().BeFalse();
        mapped.Rows[0].Cells[2].IsBlank.Should().BeTrue();
        mapped.Rows[0].Cells[3].IsBlank.Should().BeTrue();
    }

    [Fact]
    public void Demo_profile_maps_demo_record_including_constant_and_attribute_sources()
    {
        CompiledProfile profile = XmlTestProfiles.Demo();
        var mapper = new ProfileRecordMapper(profile);
        XNamespace t = "urn:example:fintech:demo:v1";
        var record = new XElement(t + "Transaction", new XAttribute("sequence", "12"),
            new XElement(t + "TransactionId", "txn-20260901-000000012"),
            new XElement(t + "Account", new XElement(t + "Reference", "ACC-0001234567")),
            new XElement(t + "PostedAt", "2026-09-01T14:22:31Z"),
            new XElement(t + "ValueDate", "2026-09-01"),
            new XElement(t + "Amount", new XAttribute("currency", "usd"), "1234.56"),
            new XElement(t + "Direction", "credit"),
            new XElement(t + "Status", "posted"),
            new XElement(t + "Counterparty", new XElement(t + "Name", "Fictional Grocers Ltd")),
            new XElement(t + "Description", "Card purchase"),
            new XElement(t + "IsReversal", "N"),
            new XElement(t + "BatchRef", new XAttribute("id", "BATCH-1")));

        MappedRecord mapped = mapper.Map(new SourceRecordEnvelope(12, 0, record));
        mapped.IsRejected.Should().BeFalse(string.Join("; ", mapped.Issues.Select(i => i.Message)));
        var table = profile.Tables[0];
        IReadOnlyList<CellValue> cells = mapped.Rows[0].Cells;
        cells[table.IndexOfColumn("transactionId")].TextValue.Should().Be("TXN-20260901-000000012");
        cells[table.IndexOfColumn("currency")].TextValue.Should().Be("USD");
        cells[table.IndexOfColumn("amount")].DecimalValue.Should().Be(1234.56m);
        cells[table.IndexOfColumn("postedAt")].DateTimeValue.Should().Be(new DateTime(2026, 9, 1, 14, 22, 31));
        cells[table.IndexOfColumn("valueDate")].DateValue.Should().Be(new DateOnly(2026, 9, 1));
        cells[table.IndexOfColumn("isReversal")].BooleanValue.Should().BeFalse();
        cells[table.IndexOfColumn("sequence")].IntegerValue.Should().Be(12);
        cells[table.IndexOfColumn("batchId")].TextValue.Should().Be("BATCH-1");
        cells[table.IndexOfColumn("sourceSystem")].TextValue.Should().Be("DEMO");
        mapped.SafeIdentifier.Should().Be("TXN-20260901-000000012");
    }
}
