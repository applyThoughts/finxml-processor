using System.Globalization;
using FinXmlProcessor.Application.Mapping;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Domain.Cells;
using FinXmlProcessor.Domain.Issues;

namespace FinXmlProcessor.Application.Tests;

public class CellConverterTests
{
    private static readonly CompiledParseOptions Iso = new(["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ssK"], CultureInfo.InvariantCulture, [], [], false);

    [Fact]
    public void Text_is_never_coerced()
    {
        CellConverter.TryConvert("000123", CellType.Text, CompiledParseOptions.Default, out CellValue v, out _, out _).Should().BeTrue();
        v.TextValue.Should().Be("000123");
    }

    [Theory]
    [InlineData("42", 42L)]
    [InlineData("-7", -7L)]
    [InlineData(" 9223372036854775807 ", long.MaxValue)]
    public void Integers(string text, long expected)
    {
        CellConverter.TryConvert(text, CellType.Integer, CompiledParseOptions.Default, out CellValue v, out _, out _).Should().BeTrue();
        v.IntegerValue.Should().Be(expected);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1,000")]
    [InlineData("abc")]
    [InlineData("99999999999999999999")]
    public void Invalid_integers_report_shape_not_value(string text)
    {
        CellConverter.TryConvert(text, CellType.Integer, CompiledParseOptions.Default, out _, out string? code, out string? message).Should().BeFalse();
        code.Should().Be(IssueCodes.MapInvalidInteger);
        message.Should().NotContain(text);
    }

    [Fact]
    public void Decimals_keep_precision_and_reject_thousands_unless_allowed()
    {
        CellConverter.TryConvert("1234567.891234", CellType.Decimal, CompiledParseOptions.Default, out CellValue v, out _, out _).Should().BeTrue();
        v.DecimalValue.Should().Be(1234567.891234m);
        CellConverter.TryConvert("1,234.50", CellType.Decimal, CompiledParseOptions.Default, out _, out string? code, out _).Should().BeFalse();
        code.Should().Be(IssueCodes.MapInvalidDecimal);
        var thousands = new CompiledParseOptions([], CultureInfo.InvariantCulture, [], [], true);
        CellConverter.TryConvert("1,234.50", CellType.Decimal, thousands, out v, out _, out _).Should().BeTrue();
        v.DecimalValue.Should().Be(1234.50m);
    }

    [Fact]
    public void Decimal_culture_is_respected()
    {
        var german = new CompiledParseOptions([], CultureInfo.GetCultureInfo("de-DE"), [], [], false);
        CellConverter.TryConvert("1234,56", CellType.Decimal, german, out CellValue v, out _, out _).Should().BeTrue();
        v.DecimalValue.Should().Be(1234.56m);
    }

    [Fact]
    public void Dates_use_declared_formats_only()
    {
        CellConverter.TryConvert("2026-09-03", CellType.Date, Iso, out CellValue v, out _, out _).Should().BeTrue();
        v.DateValue.Should().Be(new DateOnly(2026, 9, 3));
        CellConverter.TryConvert("09/03/2026", CellType.Date, Iso, out _, out string? code, out string? message).Should().BeFalse();
        code.Should().Be(IssueCodes.MapInvalidDate);
        message.Should().NotContain("09/03/2026");
        CellConverter.TryConvert("2026-02-30", CellType.Date, Iso, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void DateTimes_are_normalised_to_utc()
    {
        CellConverter.TryConvert("2026-09-03T14:00:00-04:00", CellType.DateTime, Iso, out CellValue v, out _, out _).Should().BeTrue();
        v.DateTimeValue.Should().Be(new DateTime(2026, 9, 3, 18, 0, 0));
        CellConverter.TryConvert("2026-09-03T14:00:00Z", CellType.DateTime, Iso, out v, out _, out _).Should().BeTrue();
        v.DateTimeValue.Should().Be(new DateTime(2026, 9, 3, 14, 0, 0));
        CellConverter.TryConvert("2026-13-45T25:61:00Z", CellType.DateTime, Iso, out _, out string? code, out _).Should().BeFalse();
        code.Should().Be(IssueCodes.MapInvalidDateTime);
    }

    [Fact]
    public void Booleans_default_and_declared_values()
    {
        CellConverter.TryConvert("Y", CellType.Boolean, CompiledParseOptions.Default, out CellValue v, out _, out _).Should().BeTrue();
        v.BooleanValue.Should().BeTrue();
        CellConverter.TryConvert("0", CellType.Boolean, CompiledParseOptions.Default, out v, out _, out _).Should().BeTrue();
        v.BooleanValue.Should().BeFalse();
        var custom = new CompiledParseOptions([], CultureInfo.InvariantCulture, ["ja"], ["nein"], false);
        CellConverter.TryConvert("JA", CellType.Boolean, custom, out v, out _, out _).Should().BeTrue();
        v.BooleanValue.Should().BeTrue();
        CellConverter.TryConvert("true", CellType.Boolean, custom, out _, out string? code, out _).Should().BeFalse();
        code.Should().Be(IssueCodes.MapInvalidBoolean);
    }
}
