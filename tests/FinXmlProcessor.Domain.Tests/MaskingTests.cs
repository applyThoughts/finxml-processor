using FinXmlProcessor.Domain.Security;
using FinXmlProcessor.Domain.Tables;

namespace FinXmlProcessor.Domain.Tests;

public class MaskingTests
{
    [Theory]
    [InlineData("1234567890", "******7890")]
    [InlineData("12345", "*2345")]
    [InlineData("1234", "****")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ", "********WXYZ")]
    public void MaskTail_hides_everything_but_the_last_four(string? input, string expected)
    {
        Masking.MaskTail(input).Should().Be(expected);
    }

    [Fact]
    public void Classification_controls_rendering()
    {
        Masking.ForClassification("secret-value", SensitivityClassification.None).Should().Be("secret-value");
        Masking.ForClassification("secret-value", SensitivityClassification.Sensitive).Should().Be("********alue");
        Masking.ForClassification("secret-value", SensitivityClassification.Restricted).Should().Be(Masking.RestrictedPlaceholder);
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData("", "empty")]
    [InlineData("12345", "length 5, digits")]
    [InlineData("12,34.5", "length 7, digits+symbols")]
    [InlineData("abc 123", "length 7, digits+letters+whitespace")]
    public void DescribeShape_never_echoes_the_value(string? input, string expected)
    {
        string description = Masking.DescribeShape(input);
        description.Should().Be(expected);
        if (!string.IsNullOrEmpty(input))
        {
            description.Should().NotContain(input);
        }
    }
}
