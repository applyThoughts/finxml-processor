namespace FinXmlProcessor.Output.Excel.Tests;

public class SheetNamingTests
{
    [Theory]
    [InlineData("Transactions", "Transactions")]
    [InlineData("A/B:C*D?E[F]G\\H", "A_B_C_D_E_F_G_H")]
    [InlineData("'quoted'", "quoted")]
    [InlineData("", "Sheet")]
    [InlineData("   ", "Sheet")]
    [InlineData("History", "History_")]
    [InlineData("This sheet name is far too long for Excel to accept", "This sheet name is far too long")]
    public void Sanitize(string input, string expected)
    {
        SheetNaming.Sanitize(input).Should().Be(expected);
        SheetNaming.Sanitize(input).Length.Should().BeLessThanOrEqualTo(31);
    }

    [Fact]
    public void Suffix_fits_within_limit()
    {
        string name = SheetNaming.WithSuffix(new string('x', 31), 12);
        name.Length.Should().Be(31);
        name.Should().EndWith(" (12)");
    }

    [Fact]
    public void Allocator_is_case_insensitive_and_unique()
    {
        var allocator = new SheetNaming.Allocator();
        allocator.Allocate("Data").Should().Be("Data");
        allocator.Allocate("data").Should().Be("data (2)");
        allocator.Allocate("Data").Should().Be("Data (3)");
        allocator.Allocate("Summary").Should().Be("Summary");
    }
}
