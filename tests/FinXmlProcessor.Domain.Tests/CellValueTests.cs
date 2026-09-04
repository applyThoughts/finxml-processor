using FinXmlProcessor.Domain.Cells;

namespace FinXmlProcessor.Domain.Tests;

public class CellValueTests
{
    [Fact]
    public void Text_round_trips_and_preserves_long_identifiers()
    {
        string id = "00012345678901234567890123456789";
        CellValue cell = CellValue.FromText(id);
        cell.Type.Should().Be(CellType.Text);
        cell.IsBlank.Should().BeFalse();
        cell.TextValue.Should().Be(id);
        cell.ToInvariantString().Should().Be(id);
    }

    [Fact]
    public void Null_text_is_blank()
    {
        CellValue cell = CellValue.FromText(null);
        cell.IsBlank.Should().BeTrue();
        cell.ToInvariantString().Should().BeEmpty();
        FluentActions.Invoking(() => cell.TextValue).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Decimal_preserves_precision_and_uses_invariant_serialization()
    {
        CellValue cell = CellValue.FromDecimal(1234567.8901234567m);
        cell.DecimalValue.Should().Be(1234567.8901234567m);
        cell.ToInvariantString().Should().Be("1234567.8901234567");
    }

    [Fact]
    public void Dates_serialize_iso()
    {
        CellValue.FromDate(new DateOnly(2026, 3, 8)).ToInvariantString().Should().Be("2026-03-08");
        CellValue.FromDateTime(new DateTime(2026, 3, 8, 14, 5, 9, DateTimeKind.Unspecified)).ToInvariantString().Should().Be("2026-03-08T14:05:09");
    }

    [Fact]
    public void Accessing_the_wrong_type_throws()
    {
        CellValue cell = CellValue.FromInteger(5);
        FluentActions.Invoking(() => cell.DecimalValue).Should().Throw<InvalidOperationException>().WithMessage("*Integer*not Decimal*");
        cell.IntegerValue.Should().Be(5);
    }

    [Fact]
    public void Booleans_serialize_lowercase()
    {
        CellValue.FromBoolean(true).ToInvariantString().Should().Be("true");
        CellValue.FromBoolean(false).BooleanValue.Should().BeFalse();
    }
}
