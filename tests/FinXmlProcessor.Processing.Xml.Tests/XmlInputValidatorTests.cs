using System.Text;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Domain.Issues;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Processing.Xml.Tests;

public class XmlInputValidatorTests
{
    private static XmlInputValidator Create(Action<ProcessingOptions>? configure = null)
    {
        var options = new ProcessingOptions { StabilityWindowMilliseconds = 0 };
        configure?.Invoke(options);
        var monitor = Substitute.For<IOptionsMonitor<ProcessingOptions>>();
        monitor.CurrentValue.Returns(options);
        return new XmlInputValidator(monitor);
    }

    [Fact]
    public async Task Valid_xml_file_is_hashed()
    {
        string path = XmlTestProfiles.WriteTemp("<?xml version=\"1.0\"?><a/>");
        InputValidationResult result = await Create().ValidateFileAsync(path, CancellationToken.None);
        result.IsValid.Should().BeTrue();
        result.Sha256.Should().HaveLength(64);
        result.SizeBytes.Should().Be(new FileInfo(path).Length);
    }

    [Fact]
    public async Task Missing_file()
    {
        InputValidationResult result = await Create().ValidateFileAsync(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".xml"), CancellationToken.None);
        result.IsValid.Should().BeFalse();
        result.Issues.Single().Code.Should().Be(IssueCodes.FileNotFound);
    }

    [Fact]
    public async Task Wrong_extension_empty_and_oversized_files()
    {
        (await Create().ValidateFileAsync(XmlTestProfiles.WriteTemp("<a/>", ".txt"), CancellationToken.None)).Issues.Single().Code.Should().Be(IssueCodes.FileUnsupportedExtension);
        (await Create().ValidateFileAsync(XmlTestProfiles.WriteTemp(string.Empty), CancellationToken.None)).Issues.Single().Code.Should().Be(IssueCodes.FileEmpty);
        (await Create(o => o.MaxInputBytes = 3).ValidateFileAsync(XmlTestProfiles.WriteTemp("<abc/>"), CancellationToken.None)).Issues.Single().Code.Should().Be(IssueCodes.FileTooLarge);
    }

    [Fact]
    public async Task Compressed_or_binary_content_is_rejected_as_unsupported_format()
    {
        string gz = Path.Combine(Path.GetTempPath(), "finxml-tests", Guid.NewGuid().ToString("N") + ".xml");
        Directory.CreateDirectory(Path.GetDirectoryName(gz)!);
        await File.WriteAllBytesAsync(gz, [0x1F, 0x8B, 0x08, 0x00, 1, 2, 3, 4]);
        InputValidationResult result = await Create().ValidateFileAsync(gz, CancellationToken.None);
        result.Issues.Single().Code.Should().Be(IssueCodes.FileUnsupportedFormat);
        result.Issues.Single().Message.Should().Contain("gzip");

        string zip = Path.ChangeExtension(gz, ".2.xml");
        await File.WriteAllBytesAsync(zip, [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]);
        (await Create().ValidateFileAsync(zip, CancellationToken.None)).Issues.Single().Message.Should().Contain("ZIP");

        string text = XmlTestProfiles.WriteTemp("this is not xml at all");
        (await Create().ValidateFileAsync(text, CancellationToken.None)).Issues.Single().Code.Should().Be(IssueCodes.FileUnsupportedFormat);
    }

    [Fact]
    public void Sniffer_accepts_bom_whitespace_and_utf16()
    {
        XmlInputValidator.SniffFormat(Encoding.UTF8.GetPreamble().Concat("  \n<x/>"u8.ToArray()).ToArray()).Should().BeNull();
        XmlInputValidator.SniffFormat(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<x/>")).ToArray()).Should().BeNull();
        XmlInputValidator.SniffFormat("-----BEGIN PGP MESSAGE-----"u8).Should().Contain("PGP");
        XmlInputValidator.SniffFormat([]).Should().BeNull();
    }

    [Fact]
    public async Task Unstable_file_is_detected()
    {
        string path = XmlTestProfiles.WriteTemp("<a/>");
        var options = new ProcessingOptions { StabilityWindowMilliseconds = 10 };
        var monitor = Substitute.For<IOptionsMonitor<ProcessingOptions>>();
        monitor.CurrentValue.Returns(options);
        var validator = new XmlInputValidator(monitor, (_, _) =>
        {
            File.AppendAllText(path, "<!-- growing -->");
            return Task.CompletedTask;
        });
        InputValidationResult result = await validator.ValidateFileAsync(path, CancellationToken.None);
        result.Issues.Single().Code.Should().Be(IssueCodes.FileUnstable);
    }
}
