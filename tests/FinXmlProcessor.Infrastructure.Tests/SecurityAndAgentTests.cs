using FinXmlProcessor.Infrastructure.Acquisition;
using FinXmlProcessor.Infrastructure.Agent;
using FinXmlProcessor.Infrastructure.Diagnostics;
using FinXmlProcessor.Infrastructure.Logging;
using FinXmlProcessor.Infrastructure.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet.Common;
using Serilog;
using Serilog.Events;

namespace FinXmlProcessor.Infrastructure.Tests;

public class SecurityAndAgentTests : IDisposable
{
    private readonly TempRoot _root = new();

    [Theory]
    [InlineData("password=hunter2 rest", "password=[redacted] rest")]
    [InlineData("Passphrase: abc123;x", "Passphrase: [redacted];x")]
    [InlineData("sftp://user:p%40ss@host/dir", "sftp://[redacted]@host/dir")]
    [InlineData("Authorization: Bearer eyJhbGciOi.abc", "Authorization: Bearer [redacted]")]
    [InlineData("nothing sensitive here", "nothing sensitive here")]
    public void RedactText_masks_common_secret_shapes(string input, string expected)
    {
        LogRedaction.RedactText(input).Should().Be(expected);
    }

    [Fact]
    public void RedactText_masks_private_key_blocks()
    {
        const string key = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----";
        string redacted = LogRedaction.RedactText("key: " + key + " tail");
        redacted.Should().NotContain("b3BlbnNzaC");
        redacted.Should().Contain("[redacted] PRIVATE KEY");
        redacted.Should().EndWith(" tail");
    }

    [Fact]
    public void Enricher_redacts_sensitive_property_names_and_values()
    {
        var events = new List<LogEvent>();
        using Serilog.Core.Logger logger = new LoggerConfiguration().Enrich.With<RedactingEnricher>().WriteTo.Sink(new ListSink(events)).CreateLogger();
        logger.Information("Connecting with {Password} to {Host} using {Note}", "hunter2", "sftp.example", "password=abc");
        events.Should().ContainSingle();
        string rendered = events[0].RenderMessage();
        rendered.Should().NotContain("hunter2").And.NotContain("password=abc");
        rendered.Should().Contain("sftp.example");
        events[0].Properties["Password"].ToString().Should().Contain("[redacted]");
    }

    [Fact]
    public void Settings_json_redaction_keeps_structure()
    {
        const string json = """{ "Sftp": { "Host": "h", "Password": "p", "PrivateKeyPassphrase": "x", "Port": 22 }, "List": ["a", "token=b"] }""";
        string redacted = DiagnosticsService.RedactSettingsJson(json);
        redacted.Should().Contain("\"Host\": \"h\"").And.Contain("\"Port\": 22");
        redacted.Should().Contain("\"Password\": \"[redacted]\"").And.Contain("\"PrivateKeyPassphrase\": \"[redacted]\"");
        redacted.Should().Contain("token=[redacted]");
        redacted.Should().NotContain("\"p\"");
    }

    [Fact]
    public void LaunchAgent_plist_renders_worker_invocation_and_escapes()
    {
        string plist = LaunchAgentManager.RenderPlist("/Applications/FinXml Processor.app/Contents/MacOS/finxml", "/Users/x/Library/Logs & More", 300);
        plist.Should().Contain("<string>com.example.finxmlprocessor.worker</string>");
        plist.Should().Contain("<string>/Applications/FinXml Processor.app/Contents/MacOS/finxml</string>");
        plist.Should().Contain("<string>schedule</string>").And.Contain("<string>run-due</string>");
        plist.Should().Contain("<key>RunAtLoad</key>").And.Contain("<integer>300</integer>");
        plist.Should().Contain("Logs &amp; More");
        LaunchAgentManager.RenderPlist("/x", "/l", 5).Should().Contain("<integer>60</integer>", "interval is clamped");
    }

    [Fact]
    public async Task NoOp_agent_reports_unsupported()
    {
        var manager = new NoOpBackgroundAgentManager(_root.Paths, Options.Monitor(new ScheduleOptions()));
        var status = await manager.GetStatusAsync(CancellationToken.None);
        status.IsSupported.Should().BeFalse();
        status.Diagnostics.Should().ContainSingle().Which.Should().Contain("macOS");
        manager.RenderDefinition().Should().Contain("run-due");
    }

    [Fact]
    public void Sftp_configuration_requires_pinned_host_key()
    {
        var o = new SftpOptions { Host = "h", Username = "u", AuthMethod = "password" };
        IReadOnlyList<string> problems = SftpAcquirer.ValidateConfiguration(o);
        problems.Should().Contain(p => p.Contains("algorithm", StringComparison.OrdinalIgnoreCase));
        problems.Should().Contain(p => p.Contains("fingerprint", StringComparison.OrdinalIgnoreCase));
        o.HostKeyAlgorithm = "ssh-ed25519";
        o.HostKeyFingerprintSha256 = "SHA256:abc";
        SftpAcquirer.ValidateConfiguration(o).Should().BeEmpty();
        o.AuthMethod = "key";
        SftpAcquirer.ValidateConfiguration(o).Should().ContainSingle().Which.Should().Contain("Private key path");
        o.PrivateKeyPath = Path.Combine(_root.Paths.Root, "missing.key");
        SftpAcquirer.ValidateConfiguration(o).Should().ContainSingle().Which.Should().Contain("does not exist");
        o.Port = 70000;
        SftpAcquirer.ValidateConfiguration(o).Should().Contain(p => p.Contains("Port", StringComparison.Ordinal));
    }

    [Fact]
    public void Sftp_errors_are_sanitized()
    {
        SftpAcquirer.Sanitize(new SshAuthenticationException("Permission denied for user bob with key /home/bob/.ssh/id")).Should().NotContain("bob");
        SftpAcquirer.Sanitize(new SshConnectionException("host key rejected")).Should().Contain("host key");
        SftpAcquirer.Sanitize(new IOException("boom at /secret/path")).Should().Be("IOException.");
    }

    [Fact]
    public void Sftp_acquirer_is_not_configured_by_default()
    {
        var acquirer = new SftpAcquirer(Options.Monitor(new SftpOptions()), new Secrets.InMemorySecretStore(), _root.Paths, Substitute.For<Application.Abstractions.IFileDuplicateDetector>(), NullLogger<SftpAcquirer>.Instance);
        acquirer.IsConfigured.Should().BeFalse();
        acquirer.ProviderId.Should().Be("sftp");
    }

    public void Dispose() => _root.Dispose();

    private sealed class ListSink : Serilog.Core.ILogEventSink
    {
        private readonly List<LogEvent> _events;

        public ListSink(List<LogEvent> events)
        {
            _events = events;
        }

        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }
}
