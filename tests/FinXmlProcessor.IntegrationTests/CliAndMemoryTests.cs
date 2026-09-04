using System.Text.Json;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Infrastructure.Diagnostics;
using FinXmlProcessor.TestDataGenerator;
using FinXmlProcessor.Worker;

namespace FinXmlProcessor.IntegrationTests;

public class CliAndMemoryTests
{
    private static async Task<(int ExitCode, string Out, string Err)> RunCliAsync(string root, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CliApp.RunAsync(args, stdout, stderr, root);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "finxml-tests", "cli", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Process_command_returns_documented_exit_codes()
    {
        string root = NewRoot();
        string output = Path.Combine(root, "out");
        (int ok, string text, _) = await RunCliAsync(root, "process", "--input", TestHost.DemoInputPath, "--output", output, "--quiet", "--set", "Processing:StabilityWindowMilliseconds=0");
        ok.Should().Be(ExitCodes.Success, text);
        text.Should().Contain("Completed with warnings").And.Contain("Workbook:");

        (int dup, string dupText, _) = await RunCliAsync(root, "process", "--input", TestHost.DemoInputPath, "--output", output, "--quiet");
        dup.Should().Be(ExitCodes.DuplicateBlocked, dupText);

        (int forced, string forcedJson, _) = await RunCliAsync(root, "process", "--input", TestHost.DemoInputPath, "--output", output, "--force", "--json");
        forced.Should().Be(ExitCodes.Success, forcedJson);
        using JsonDocument doc = JsonDocument.Parse(forcedJson);
        doc.RootElement.GetProperty("outcome").GetString().Should().Be("CompletedWithWarnings");
        doc.RootElement.GetProperty("counts").GetProperty("RecordsSeen").GetInt64().Should().Be(250);

        (int missing, _, _) = await RunCliAsync(root, "process", "--input", Path.Combine(root, "nope.xml"), "--quiet");
        missing.Should().Be(ExitCodes.InputInvalid);

        (int badProfile, _, _) = await RunCliAsync(root, "process", "--input", TestHost.DemoInputPath, "--profile", "ghost", "--quiet");
        badProfile.Should().Be(ExitCodes.ConfigurationInvalid);
    }

    [Fact]
    public async Task Profile_schedule_diagnostics_and_self_test_commands()
    {
        string root = NewRoot();
        (int valid, string validText, _) = await RunCliAsync(root, "profile", "validate", TestHost.DemoProfilePath);
        valid.Should().Be(ExitCodes.Success, validText);
        validText.Should().Contain("Valid: demo-fintech-v1");

        string broken = Path.Combine(Path.GetTempPath(), "finxml-tests", $"broken-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(broken, "{ \"schemaVersion\": 1 }");
        (int invalid, string invalidText, _) = await RunCliAsync(root, "profile", "validate", broken);
        invalid.Should().Be(ExitCodes.ConfigurationInvalid);
        invalidText.Should().Contain("Invalid");

        (int listCode, string listText, _) = await RunCliAsync(root, "profile", "list");
        listCode.Should().Be(ExitCodes.Success);
        listText.Should().Contain("demo-fintech-v1");

        (int status, string statusText, _) = await RunCliAsync(root, "schedule", "status", "--json");
        status.Should().Be(ExitCodes.Success, statusText);
        using (JsonDocument doc = JsonDocument.Parse(statusText))
        {
            doc.RootElement.GetProperty("due").GetBoolean().Should().BeFalse("scheduling is disabled by default");
            doc.RootElement.GetProperty("nextEastern").GetString().Should().Contain("19:00");
        }

        (int notDue, string notDueText, _) = await RunCliAsync(root, "schedule", "run-due", "--quiet");
        notDue.Should().Be(ExitCodes.Success);
        notDueText.Should().Contain("disabled");

        string bundle = Path.Combine(root, "bundle.zip");
        (int diag, string diagText, _) = await RunCliAsync(root, "diagnostics", "--bundle", bundle);
        diag.Should().Be(ExitCodes.Success, diagText);
        diagText.Should().Contain("Secret store").And.Contain("Next occurrence (Eastern)");
        File.Exists(bundle).Should().BeTrue();
        using (var zip = System.IO.Compression.ZipFile.OpenRead(bundle))
        {
            zip.Entries.Select(e => e.FullName).Should().Contain("diagnostics.txt");
            zip.Entries.Should().NotContain(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase));
        }

        (int agent, _, _) = await RunCliAsync(root, "schedule", "agent", "render");
        agent.Should().Be(ExitCodes.Success);

        (int self, string selfText, _) = await RunCliAsync(root, "self-test", "--quiet", "--set", "Processing:StabilityWindowMilliseconds=0");
        self.Should().Be(ExitCodes.Success, selfText);
    }

    [Fact]
    public async Task Run_now_processes_the_newest_file_in_the_input_folder()
    {
        string root = NewRoot();
        await using var host = new TestHost();
        await host.InitializeAsync();
        host.StageDemoInput("older.xml");
        await Task.Delay(50);
        string newer = host.StageDemoInput("newer.xml");
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(1));
        (int code, string text, _) = await RunCliAsync(host.Root, "schedule", "run-now", "--quiet", "--set", "Processing:StabilityWindowMilliseconds=0");
        code.Should().Be(ExitCodes.Success, text);
        text.Should().Contain("Completed with warnings");
        (int second, string secondText, _) = await RunCliAsync(host.Root, "schedule", "run-now", "--quiet", "--set", "Processing:StabilityWindowMilliseconds=0");
        second.Should().Be(ExitCodes.Success, secondText);
        secondText.Should().Contain("No unprocessed input", "older.xml has identical content and is skipped as already processed");
        _ = root;
    }

    [Fact]
    public async Task Memory_stays_bounded_for_a_generated_dataset()
    {
        // ~30 MB in CI; set FINXML_LARGE_BENCH_BYTES to run bigger locally (e.g. 209715200).
        long bytes = long.TryParse(Environment.GetEnvironmentVariable("FINXML_LARGE_BENCH_BYTES"), out long env) && env > 0 ? env : 30L * 1024 * 1024;
        await using var host = new TestHost();
        await host.InitializeAsync();
        string input = Path.Combine(host.Root, "large.xml");
        GenerationSummary generated = SyntheticDataGenerator.GenerateFile(input, new GeneratorOptions { ApproximateBytes = bytes, Seed = 99, DuplicateRate = 0.01, MissingRequiredRate = 0.005, SpecialCharacterRate = 0.02, Indent = false });
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);

        using var sampler = new PeakMemorySampler(TimeSpan.FromMilliseconds(50));
        ProcessingResult result = await host.Get<ProcessingPipeline>().RunAsync(new ProcessingRequest(input, OutputDirectory: Path.Combine(host.Root, "out")), null, CancellationToken.None);
        MemoryMeasurement m = sampler.Measure();

        result.IsSuccess.Should().BeTrue(result.SanitizedMessage);
        result.Job!.Counts.RecordsSeen.Should().Be(generated.Records);
        (result.Job.Counts.RecordsAccepted + result.Job.Counts.RecordsRejected).Should().Be(generated.Records);
        long managedGrowth = m.PeakManagedHeapBytes - baseline;
        managedGrowth.Should().BeLessThan(96L * 1024 * 1024, $"managed heap growth must not scale with input size (input {bytes:N0} bytes, peak heap {m.PeakManagedHeapBytes:N0}, baseline {baseline:N0})");
        m.PeakWorkingSetBytes.Should().BeLessThan(512L * 1024 * 1024);
    }
}
