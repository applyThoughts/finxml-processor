using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using CellType = FinXmlProcessor.Domain.Cells.CellType;
using CellValue = FinXmlProcessor.Domain.Cells.CellValue;

namespace FinXmlProcessor.Output.Excel.Tests;

public class StreamingXlsxWorkbookWriterTests
{
    private static readonly OutputTableDefinition Table = new("t", "Data",
    [
        new OutputColumnDefinition("id", "ID", CellType.Text, Width: 20),
        new OutputColumnDefinition("amount", "Amount", CellType.Decimal, NumberFormat: "#,##0.00"),
        new OutputColumnDefinition("when", "When", CellType.Date),
        new OutputColumnDefinition("at", "At", CellType.DateTime),
        new OutputColumnDefinition("flag", "Flag", CellType.Boolean),
        new OutputColumnDefinition("n", "N", CellType.Integer),
    ]);

    private readonly StreamingXlsxWorkbookWriter _writer = new(NullLogger<StreamingXlsxWorkbookWriter>.Instance);

    [Fact]
    public async Task Writes_typed_cells_summary_and_verifies_package()
    {
        string path = TempPath();
        await using (IWorkbookSession session = await _writer.BeginAsync(path, [Table], WorkbookWriterOptions.Default, CancellationToken.None))
        {
            session.WriteRow(new OutputRow("t", 1, "A", [
                CellValue.FromText("000123"),
                CellValue.FromDecimal(1234567.891m),
                CellValue.FromDate(new DateOnly(2026, 9, 3)),
                CellValue.FromDateTime(new DateTime(2026, 9, 3, 14, 30, 0)),
                CellValue.FromBoolean(true),
                CellValue.FromInteger(42),
            ]));
            session.WriteRow(new OutputRow("t", 2, "B", [CellValue.FromText("x"), CellValue.Blank(CellType.Decimal), CellValue.Blank(CellType.Date), CellValue.Blank(CellType.DateTime), CellValue.FromBoolean(false), CellValue.Blank(CellType.Integer)]));
            string final = await session.CompleteAsync([new SummaryEntry("Source file", "in.xml")], [RecordIssue.Warning("W-1", null, "note")], CancellationToken.None);
            final.Should().Be(path);
            session.RowsWritten.Should().Be(2);
        }

        File.Exists(path).Should().BeTrue();
        File.Exists(path + ".part").Should().BeFalse();
        IReadOnlyList<RecordIssue> verification = await _writer.VerifyAsync(path, CancellationToken.None);
        verification.Should().NotContain(i => i.Severity == IssueSeverity.Fatal);

        using SpreadsheetDocument doc = SpreadsheetDocument.Open(path, false);
        var sheets = doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Select(s => s.Name!.Value).ToList();
        sheets.Should().Equal("Summary", "Data");
        WorksheetPart data = (WorksheetPart)doc.WorkbookPart.GetPartById(doc.WorkbookPart.Workbook.Sheets!.Elements<Sheet>().Single(s => s.Name == "Data").Id!.Value!);
        var rows = data.Worksheet.Descendants<Row>().ToList();
        rows.Should().HaveCount(3);
        var first = rows[1].Elements<Cell>().ToList();
        first[0].DataType!.Value.Should().Be(CellValues.InlineString);
        first[0].InlineString!.Text!.Text.Should().Be("000123");
        first[1].CellValue!.Text.Should().Be("1234567.891");
        first[1].DataType.Should().BeNull();
        double.Parse(first[2].CellValue!.Text, System.Globalization.CultureInfo.InvariantCulture).Should().Be(new DateTime(2026, 9, 3).ToOADate());
        first[4].DataType!.Value.Should().Be(CellValues.Boolean);
        first[5].CellValue!.Text.Should().Be("42");
        data.Worksheet.Descendants<AutoFilter>().Should().ContainSingle().Which.Reference!.Value.Should().Be("A1:F3");
        data.Worksheet.Descendants<Pane>().Single().State!.Value.Should().Be(PaneStateValues.Frozen);
        data.Worksheet.Descendants<CellFormula>().Should().BeEmpty();

        WorksheetPart summary = (WorksheetPart)doc.WorkbookPart.GetPartById(doc.WorkbookPart.Workbook.Sheets!.Elements<Sheet>().First().Id!.Value!);
        string summaryText = string.Join("|", summary.Worksheet.Descendants<Text>().Select(t => t.Text));
        summaryText.Should().Contain("Source file|in.xml").And.Contain("Warning W-1");
    }

    [Theory]
    [InlineData("=SUM(A1:A9)")]
    [InlineData("+1-555-0100")]
    [InlineData("-42")]
    [InlineData("@mention")]
    [InlineData("=cmd|' /C calc'!A0")]
    public async Task Formula_like_text_stays_literal(string text)
    {
        string path = TempPath();
        await using (IWorkbookSession session = await _writer.BeginAsync(path, [Table], WorkbookWriterOptions.Default, CancellationToken.None))
        {
            session.WriteRow(new OutputRow("t", 1, null, [CellValue.FromText(text), CellValue.Blank(CellType.Decimal), CellValue.Blank(CellType.Date), CellValue.Blank(CellType.DateTime), CellValue.Blank(CellType.Boolean), CellValue.Blank(CellType.Integer)]));
            await session.CompleteAsync([], [], CancellationToken.None);
        }

        using SpreadsheetDocument doc = SpreadsheetDocument.Open(path, false);
        WorksheetPart data = doc.WorkbookPart!.WorksheetParts.Single(p => p.Worksheet.Descendants<Text>().Any(t => t.Text == text));
        Cell cell = data.Worksheet.Descendants<Cell>().Single(c => c.InlineString?.Text?.Text == text);
        cell.DataType!.Value.Should().Be(CellValues.InlineString);
        cell.CellFormula.Should().BeNull();
        data.Worksheet.Descendants<CellFormula>().Should().BeEmpty();
    }

    [Fact]
    public async Task Splits_sheets_at_the_row_limit_and_repeats_headers()
    {
        string path = TempPath();
        var options = new WorkbookWriterOptions(MaxRowsPerSheet: 4); // header + 3 data rows per sheet
        await using (IWorkbookSession session = await _writer.BeginAsync(path, [Table], options, CancellationToken.None))
        {
            for (int i = 1; i <= 8; i++)
            {
                session.WriteRow(new OutputRow("t", i, null, [CellValue.FromText($"r{i}"), CellValue.FromDecimal(i), CellValue.Blank(CellType.Date), CellValue.Blank(CellType.DateTime), CellValue.Blank(CellType.Boolean), CellValue.FromInteger(i)]));
            }

            await session.CompleteAsync([], [], CancellationToken.None);
        }

        using SpreadsheetDocument doc = SpreadsheetDocument.Open(path, false);
        var sheets = doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Select(s => s.Name!.Value!).ToList();
        sheets.Should().Equal("Summary", "Data", "Data (2)", "Data (3)");
        foreach (string name in sheets.Skip(1))
        {
            WorksheetPart part = (WorksheetPart)doc.WorkbookPart.GetPartById(doc.WorkbookPart.Workbook.Sheets!.Elements<Sheet>().Single(s => s.Name == name).Id!.Value!);
            var rows = part.Worksheet.Descendants<Row>().ToList();
            rows.Count.Should().BeLessThanOrEqualTo(4);
            rows[0].Elements<Cell>().First().InlineString!.Text!.Text.Should().Be("ID");
        }

        doc.WorkbookPart.Workbook.DefinedNames!.Elements<DefinedName>().Should().HaveCount(3);
    }

    [Fact]
    public async Task Rejected_sheet_is_created_only_when_used_and_masks_are_respected()
    {
        string path = TempPath();
        await using (IWorkbookSession session = await _writer.BeginAsync(path, [Table], WorkbookWriterOptions.Default, CancellationToken.None))
        {
            session.WriteRejected(new RejectedRecordLine(9, "ID-9", "MAP-003", "amount: not a decimal", [new KeyValuePair<string, string>("ID", "ID-9"), new KeyValuePair<string, string>("Account", "******4567")]));
            await session.CompleteAsync([], [], CancellationToken.None);
        }

        using SpreadsheetDocument doc = SpreadsheetDocument.Open(path, false);
        doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Select(s => s.Name!.Value).Should().Equal("Summary", "Data", "Rejected Records");
        WorksheetPart rejected = (WorksheetPart)doc.WorkbookPart.GetPartById(doc.WorkbookPart.Workbook.Sheets!.Elements<Sheet>().Last().Id!.Value!);
        string text = string.Join("|", rejected.Worksheet.Descendants<Text>().Select(t => t.Text));
        text.Should().Contain("MAP-003").And.Contain("Account=******4567");

        string path2 = TempPath();
        await using (IWorkbookSession session = await _writer.BeginAsync(path2, [Table], WorkbookWriterOptions.Default, CancellationToken.None))
        {
            await session.CompleteAsync([], [], CancellationToken.None);
        }

        using SpreadsheetDocument doc2 = SpreadsheetDocument.Open(path2, false);
        doc2.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Should().HaveCount(2);
    }

    [Fact]
    public async Task Disposing_without_completing_discards_the_staging_file()
    {
        string path = TempPath();
        await using (IWorkbookSession session = await _writer.BeginAsync(path, [Table], WorkbookWriterOptions.Default, CancellationToken.None))
        {
            session.WriteRow(new OutputRow("t", 1, null, [CellValue.FromText("x"), CellValue.Blank(CellType.Decimal), CellValue.Blank(CellType.Date), CellValue.Blank(CellType.DateTime), CellValue.Blank(CellType.Boolean), CellValue.Blank(CellType.Integer)]));
            File.Exists(path + ".part").Should().BeTrue();
        }

        File.Exists(path).Should().BeFalse();
        File.Exists(path + ".part").Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_xml_characters_are_replaced_and_long_text_truncated()
    {
        string path = TempPath();
        string longText = new string('a', 40_000);
        await using (IWorkbookSession session = await _writer.BeginAsync(path, [Table], WorkbookWriterOptions.Default, CancellationToken.None))
        {
            session.WriteRow(new OutputRow("t", 1, null, [CellValue.FromText("badchar " + longText), CellValue.Blank(CellType.Decimal), CellValue.Blank(CellType.Date), CellValue.Blank(CellType.DateTime), CellValue.Blank(CellType.Boolean), CellValue.Blank(CellType.Integer)]));
            await session.CompleteAsync([], [], CancellationToken.None);
        }

        (await _writer.VerifyAsync(path, CancellationToken.None)).Should().NotContain(i => i.Severity == IssueSeverity.Fatal);
        using SpreadsheetDocument doc = SpreadsheetDocument.Open(path, false);
        Text t = doc.WorkbookPart!.WorksheetParts.SelectMany(p => p.Worksheet.Descendants<Text>()).Single(x => x.Text.StartsWith("bad", StringComparison.Ordinal));
        t.Text.Length.Should().Be(32_767);
        t.Text.Should().StartWith("bad�char");
    }

    [Fact]
    public async Task Verify_reports_corrupt_files()
    {
        string path = TempPath();
        await File.WriteAllTextAsync(path, "definitely not a zip");
        IReadOnlyList<RecordIssue> issues = await _writer.VerifyAsync(path, CancellationToken.None);
        issues.Should().ContainSingle().Which.Severity.Should().Be(IssueSeverity.Fatal);
    }

    private static string TempPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "finxml-tests", "xlsx");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".xlsx");
    }
}
