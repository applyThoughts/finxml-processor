using System.Xml.Linq;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Sources;

namespace FinXmlProcessor.Processing.Xml.Tests;

public class StreamingXmlRecordReaderTests
{
    private const string Ns = "urn:test";

    [Fact]
    public async Task Reads_every_record_in_document_order_with_prefix_namespace()
    {
        string xml = $"""
            <?xml version="1.0"?>
            <p:Batch xmlns:p="{Ns}">
              <p:Header><p:Note>ignored</p:Note></p:Header>
              <p:Items>
                <p:Item flag="true"><p:Id>A1</p:Id><p:Amount>1.50</p:Amount><p:Nested><p:Note>  a   b </p:Note></p:Nested></p:Item>
                <p:Item><p:Id>A2</p:Id><p:Amount>2</p:Amount></p:Item>
                <p:Other/>
                <p:Item/>
              </p:Items>
              <p:Trailer><p:Count>3</p:Count></p:Trailer>
            </p:Batch>
            """;
        List<SourceRecordEnvelope> records = await ReadAll(xml, XmlTestProfiles.Simple());
        records.Should().HaveCount(3);
        records.Select(r => r.SourceOrdinal).Should().Equal(1, 2, 3);
        records[0].Fragment.Element(XName.Get("Id", Ns))!.Value.Should().Be("A1");
        records[2].Fragment.IsEmpty.Should().BeTrue();
        records.Select(r => r.ApproximateBytePosition).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Default_namespace_serialization_is_equivalent()
    {
        string xml = $"""
            <Batch xmlns="{Ns}"><Items><Item><Id>X</Id><Amount>3</Amount></Item><Item><Id>Y</Id><Amount>4</Amount></Item></Items></Batch>
            """;
        List<SourceRecordEnvelope> records = await ReadAll(xml, XmlTestProfiles.Simple());
        records.Should().HaveCount(2);
    }

    [Fact]
    public async Task Wrong_namespace_yields_no_records()
    {
        const string xml = "<Batch xmlns=\"urn:other\"><Items><Item><Id>X</Id></Item></Items></Batch>";
        ProcessingFatalException ex = await Assert.ThrowsAsync<ProcessingFatalException>(() => ReadAll(xml, XmlTestProfiles.Simple()));
        ex.Code.Should().Be(IssueCodes.XmlUnexpectedRoot);
        ex.Quarantine.Should().BeTrue();
    }

    [Fact]
    public async Task Record_elements_at_the_wrong_depth_are_ignored()
    {
        string xml = $"""
            <p:Batch xmlns:p="{Ns}"><p:Item><p:Id>top</p:Id></p:Item><p:Items><p:Wrapper><p:Item><p:Id>deep</p:Id></p:Item></p:Wrapper><p:Item><p:Id>ok</p:Id></p:Item></p:Items></p:Batch>
            """;
        List<SourceRecordEnvelope> records = await ReadAll(xml, XmlTestProfiles.Simple());
        records.Should().ContainSingle().Which.Fragment.Value.Should().Be("ok");
    }

    [Fact]
    public async Task Malformed_xml_fails_with_sanitized_message()
    {
        string xml = $"""
            <p:Batch xmlns:p="{Ns}"><p:Items><p:Item><p:Id>SECRET-123</p:Id><p:Amount>1</p:Amount></p:Item><p:Item><p:Id>trunc
            """;
        ProcessingFatalException ex = await Assert.ThrowsAsync<ProcessingFatalException>(() => ReadAll(xml, XmlTestProfiles.Simple()));
        ex.Code.Should().Be(IssueCodes.XmlMalformed);
        ex.Quarantine.Should().BeTrue();
        ex.Message.Should().MatchRegex(@"Malformed XML at line \d+, position \d+\.");
        ex.Message.Should().NotContain("SECRET");
    }

    [Fact]
    public async Task Records_before_the_error_are_still_streamed()
    {
        string xml = $"""
            <p:Batch xmlns:p="{Ns}"><p:Items><p:Item><p:Id>1</p:Id></p:Item><p:Item><p:Id>2</p:Id></p:Item><p:Item><p:Id>3</p:Id></p:It
            """;
        var seen = new List<long>();
        await using IRecordReader reader = new StreamingXmlRecordReader(XmlTestProfiles.WriteTemp(xml), XmlTestProfiles.Simple(), 1024);
        Func<Task> act = async () =>
        {
            await foreach (SourceRecordEnvelope r in reader.ReadRecordsAsync(CancellationToken.None))
            {
                seen.Add(r.SourceOrdinal);
            }
        };
        await act.Should().ThrowAsync<ProcessingFatalException>();
        seen.Should().Equal(1, 2);
    }

    [Fact]
    public async Task Dtd_is_prohibited()
    {
        string xml = $"""
            <?xml version="1.0"?>
            <!DOCTYPE Batch [<!ENTITY x "expanded">]>
            <p:Batch xmlns:p="{Ns}"><p:Items><p:Item><p:Id>&x;</p:Id></p:Item></p:Items></p:Batch>
            """;
        ProcessingFatalException ex = await Assert.ThrowsAsync<ProcessingFatalException>(() => ReadAll(xml, XmlTestProfiles.Simple()));
        ex.Code.Should().Be(IssueCodes.XmlDtdProhibited);
    }

    [Fact]
    public async Task External_entities_cannot_be_resolved()
    {
        string xml = $"""
            <?xml version="1.0"?>
            <!DOCTYPE Batch [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <p:Batch xmlns:p="{Ns}"><p:Items><p:Item><p:Id>&xxe;</p:Id></p:Item></p:Items></p:Batch>
            """;
        ProcessingFatalException ex = await Assert.ThrowsAsync<ProcessingFatalException>(() => ReadAll(xml, XmlTestProfiles.Simple()));
        ex.Code.Should().BeOneOf(IssueCodes.XmlDtdProhibited, IssueCodes.XmlMalformed);
    }

    [Fact]
    public async Task Oversized_record_is_rejected()
    {
        string big = new string('x', 20_000);
        string xml = $"""
            <p:Batch xmlns:p="{Ns}"><p:Items><p:Item><p:Id>1</p:Id><p:Amount>{big}</p:Amount></p:Item></p:Items></p:Batch>
            """;
        await using IRecordReader reader = new StreamingXmlRecordReader(XmlTestProfiles.WriteTemp(xml), XmlTestProfiles.Simple(), maxRecordFragmentChars: 1000);
        Func<Task> act = async () =>
        {
            await foreach (SourceRecordEnvelope _ in reader.ReadRecordsAsync(CancellationToken.None))
            {
            }
        };
        (await act.Should().ThrowAsync<ProcessingFatalException>()).Which.Code.Should().Be(IssueCodes.XmlRecordTooLarge);
    }

    [Fact]
    public async Task Progress_counters_advance_and_reach_total()
    {
        (_, long total, long read) = await ReadAllWithProgress(File.ReadAllText(XmlTestProfiles.DemoInputPath), XmlTestProfiles.Demo());
        total.Should().BeGreaterThan(0);
        read.Should().Be(total);
    }

    [Fact]
    public async Task Demo_sample_streams_all_records()
    {
        await using IRecordReader reader = new StreamingXmlRecordReader(XmlTestProfiles.DemoInputPath, XmlTestProfiles.Demo(), 4 * 1024 * 1024);
        long count = 0;
        await foreach (SourceRecordEnvelope r in reader.ReadRecordsAsync(CancellationToken.None))
        {
            count++;
            r.Fragment.Name.LocalName.Should().Be("Transaction");
        }

        count.Should().Be(250);
    }

    [Fact]
    public async Task Cancellation_is_cooperative()
    {
        using var cts = new CancellationTokenSource();
        await using IRecordReader reader = new StreamingXmlRecordReader(XmlTestProfiles.DemoInputPath, XmlTestProfiles.Demo(), 4 * 1024 * 1024);
        int seen = 0;
        Func<Task> act = async () =>
        {
            await foreach (SourceRecordEnvelope _ in reader.ReadRecordsAsync(cts.Token))
            {
                if (++seen == 5)
                {
                    cts.Cancel();
                }
            }
        };
        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().Be(5);
    }

    [Fact]
    public async Task Xsd_validation_rejects_schema_violations_without_loading_the_document()
    {
        string xsd = $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="{Ns}" xmlns="{Ns}" elementFormDefault="qualified">
              <xs:element name="Batch"><xs:complexType><xs:sequence>
                <xs:element name="Items"><xs:complexType><xs:sequence>
                  <xs:element name="Item" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                    <xs:element name="Id" type="xs:string"/><xs:element name="Amount" type="xs:decimal"/>
                  </xs:sequence></xs:complexType></xs:element>
                </xs:sequence></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        string xsdPath = XmlTestProfiles.WriteTemp(xsd, ".xsd");
        CompiledProfile profile = XmlTestProfiles.Simple(xsdPath: xsdPath);
        string good = $"<p:Batch xmlns:p=\"{Ns}\"><p:Items><p:Item><p:Id>1</p:Id><p:Amount>2.5</p:Amount></p:Item></p:Items></p:Batch>";
        (await ReadAll(good, profile)).Should().ContainSingle();
        string bad = $"<p:Batch xmlns:p=\"{Ns}\"><p:Items><p:Item><p:Id>1</p:Id><p:Amount>not-a-number</p:Amount></p:Item></p:Items></p:Batch>";
        (await Assert.ThrowsAsync<ProcessingFatalException>(() => ReadAll(bad, profile))).Code.Should().Be(IssueCodes.XmlSchemaViolation);
    }

    [Fact]
    public void Secure_settings_are_applied()
    {
        var settings = StreamingXmlRecordReader.CreateSecureSettings(null);
        settings.DtdProcessing.Should().Be(System.Xml.DtdProcessing.Prohibit);
        settings.CheckCharacters.Should().BeTrue();
        settings.IgnoreComments.Should().BeTrue();
        settings.IgnoreProcessingInstructions.Should().BeTrue();
    }

    private static async Task<List<SourceRecordEnvelope>> ReadAll(string xml, CompiledProfile profile)
    {
        (List<SourceRecordEnvelope> records, _, _) = await ReadAllWithProgress(xml, profile);
        return records;
    }

    private static async Task<(List<SourceRecordEnvelope> Records, long Total, long Read)> ReadAllWithProgress(string xml, CompiledProfile profile)
    {
        string path = XmlTestProfiles.WriteTemp(xml);
        await using var reader = new StreamingXmlRecordReader(path, profile, 4 * 1024 * 1024);
        var records = new List<SourceRecordEnvelope>();
        await foreach (SourceRecordEnvelope record in reader.ReadRecordsAsync(CancellationToken.None))
        {
            records.Add(record);
        }

        return (records, reader.TotalBytes ?? 0, reader.BytesRead);
    }
}
