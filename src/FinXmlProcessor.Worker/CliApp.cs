using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using FinXmlProcessor.Application;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Infrastructure.Acquisition;
using FinXmlProcessor.Infrastructure.Diagnostics;
using FinXmlProcessor.Infrastructure.Hosting;
using FinXmlProcessor.Infrastructure.Retention;
using FinXmlProcessor.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FinXmlProcessor.Worker;

/// <summary>Documented exit codes. Automation should treat anything other than 0 as needing attention.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Unexpected = 1;
    public const int ConfigurationInvalid = 2;
    public const int InputInvalid = 3;
    public const int DuplicateBlocked = 4;
    public const int ProcessingFailed = 5;
    public const int OutputFailed = 6;
    public const int DeliveryFailed = 7;
    public const int Cancelled = 8;
    public const int LockUnavailable = 9;

    public static int FromOutcome(ProcessingResult result) => result.Outcome switch
    {
        ProcessingOutcome.Completed or ProcessingOutcome.CompletedWithWarnings => Success,
        ProcessingOutcome.ConfigurationInvalid => ConfigurationInvalid,
        ProcessingOutcome.Quarantined => InputInvalid,
        ProcessingOutcome.DuplicateBlocked => DuplicateBlocked,
        ProcessingOutcome.Cancelled => Cancelled,
        ProcessingOutcome.LockUnavailable => LockUnavailable,
        ProcessingOutcome.Failed when result.Job?.Issues.Any(i => i.Code == Domain.Issues.IssueCodes.JobDeliveryFailed) == true => DeliveryFailed,
        ProcessingOutcome.Failed when result.Job?.Issues.Any(i => i.Code.StartsWith("OUT-", StringComparison.Ordinal)) == true => OutputFailed,
        ProcessingOutcome.Failed when result.Job?.Issues.Any(i => i.Code.StartsWith("FILE-", StringComparison.Ordinal)) == true => InputInvalid,
        _ => ProcessingFailed,
    };
}

/// <summary>
/// The headless worker. Every command composes the same host as the desktop app, so behaviour is identical.
/// Secrets are never accepted as command-line arguments; <c>sftp set-secret</c> reads them from standard input.
/// </summary>
public static class CliApp
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, string? rootOverride = null, CancellationToken cancellationToken = default)
    {
        var jsonOption = new Option<bool>("--json") { Description = "Emit machine-readable JSON instead of text." };
        var quietOption = new Option<bool>("--quiet") { Description = "Only print errors and the final summary line." };
        var root = new RootCommand($"{AppInfo.ProductName} worker/CLI {AppInfo.Version}. Exit codes: 0 ok, 1 unexpected, 2 configuration, 3 invalid input, 4 duplicate, 5 processing, 6 output, 7 delivery, 8 cancelled, 9 lock busy.");
        root.Options.Add(jsonOption);
        root.Options.Add(quietOption);

        // process
        var input = new Option<string>("--input", "-i") { Description = "Path to the input XML file.", Required = true };
        var profile = new Option<string?>("--profile", "-p") { Description = "Profile id or path (default: the active profile)." };
        var output = new Option<string?>("--output", "-o") { Description = "Output folder (default: configured output folder)." };
        var force = new Option<bool>("--force") { Description = "Rerun even if identical content was processed before." };
        var process = new Command("process", "Process one XML file into an Excel workbook.") { input, profile, output, force };
        process.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            var pipeline = host.Services.GetRequiredService<ProcessingPipeline>();
            var request = new ProcessingRequest(pr.GetValue(input)!, pr.GetValue(profile), pr.GetValue(output), pr.GetValue(force), Trigger: "cli");
            ProcessingResult result = await pipeline.RunAsync(request, ctx.Quiet ? null : new ConsoleProgress(stdout), ct).ConfigureAwait(false);
            EmitProcessingResult(host, result, ctx);
            return ExitCodes.FromOutcome(result);
        }));

        // schedule
        var schedule = new Command("schedule", "Scheduled processing.");
        var runDue = new Command("run-due", "Run the daily job if it is due (idempotent; invoked by the LaunchAgent).");
        runDue.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            var coordinator = host.Services.GetRequiredService<ScheduledRunCoordinator>();
            ScheduledRunResult result = await coordinator.RunDueAsync(ctx.Quiet ? null : new ConsoleProgress(stdout), ct).ConfigureAwait(false);
            if (result.Processing is not null)
            {
                EmitProcessingResult(host, result.Processing, ctx);
                return ExitCodes.FromOutcome(result.Processing);
            }

            ctx.Emit(new { ran = false, message = result.Message }, result.Message);
            return ExitCodes.Success;
        }));
        var runNow = new Command("run-now", "Acquire and process the newest unprocessed input regardless of the schedule.");
        runNow.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            var coordinator = host.Services.GetRequiredService<ScheduledRunCoordinator>();
            ScheduledRunResult result = await coordinator.RunNowAsync(ctx.Quiet ? null : new ConsoleProgress(stdout), ct).ConfigureAwait(false);
            if (result.Processing is not null)
            {
                EmitProcessingResult(host, result.Processing, ctx);
                return ExitCodes.FromOutcome(result.Processing);
            }

            ctx.Emit(new { ran = false, message = result.Message }, result.Message);
            return ExitCodes.Success;
        }));
        var status = new Command("status", "Show the next scheduled occurrence and whether a run is due.");
        status.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            var service = host.Services.GetRequiredService<IScheduleService>();
            var clock = host.Services.GetRequiredService<IProcessingClock>();
            NodaTime.Instant now = clock.GetCurrentInstant();
            ScheduledOccurrence next = service.NextOccurrence(now);
            DueRunDecision due = await service.EvaluateAsync(now, ct).ConfigureAwait(false);
            var payload = new { nextEastern = next.BusinessTime.ToString("yyyy-MM-dd HH:mm o<g>", CultureInfo.InvariantCulture), nextUtc = next.Instant.ToString(), due = due.IsDue, catchUp = due.IsCatchUp, reason = due.Reason };
            ctx.Emit(payload, $"Next: {payload.nextEastern} (UTC {payload.nextUtc}). Due now: {due.IsDue}. {due.Reason}");
            return ExitCodes.Success;
        }));
        var agent = new Command("agent", "Manage the macOS LaunchAgent.");
        foreach ((string name, string description) in new[] { ("status", "Show agent status."), ("install", "Install or update the agent."), ("uninstall", "Remove the agent."), ("render", "Print the agent definition without installing.") })
        {
            var sub = new Command(name, description);
            sub.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
            {
                var manager = host.Services.GetRequiredService<IBackgroundAgentManager>();
                if (name == "render")
                {
                    stdout.WriteLine(manager.RenderDefinition());
                    return ExitCodes.Success;
                }

                AgentStatus agentStatus = name switch
                {
                    "install" => await manager.InstallOrUpdateAsync(ct).ConfigureAwait(false),
                    "uninstall" => await manager.UninstallAsync(ct).ConfigureAwait(false),
                    _ => await manager.GetStatusAsync(ct).ConfigureAwait(false),
                };
                ctx.Emit(agentStatus, $"Supported: {agentStatus.IsSupported}, installed: {agentStatus.IsInstalled}, loaded: {agentStatus.IsLoaded}\n" + string.Join('\n', agentStatus.Diagnostics));
                return agentStatus.IsSupported ? ExitCodes.Success : ExitCodes.ConfigurationInvalid;
            }));
            agent.Subcommands.Add(sub);
        }

        schedule.Subcommands.Add(runDue);
        schedule.Subcommands.Add(runNow);
        schedule.Subcommands.Add(status);
        schedule.Subcommands.Add(agent);

        // profile
        var profileCmd = new Command("profile", "Mapping profiles.");
        var profilePath = new Argument<string>("path") { Description = "Profile JSON file." };
        var validate = new Command("validate", "Validate a mapping profile against the schema and semantic rules.") { profilePath };
        validate.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            var loader = host.Services.GetRequiredService<ProfileLoader>();
            ProfileValidationResult result = await loader.LoadFileAsync(pr.GetValue(profilePath)!, ct).ConfigureAwait(false);
            ctx.Emit(new { valid = result.IsValid, id = result.Profile?.Id, version = result.Profile?.Version, hash = result.Profile?.Hash, errors = result.Errors },
                result.IsValid ? $"Valid: {result.Profile!.Id} {result.Profile.Version} (sha256 {result.Profile.Hash})" : "Invalid:\n  " + string.Join("\n  ", result.Errors));
            return result.IsValid ? ExitCodes.Success : ExitCodes.ConfigurationInvalid;
        }));
        var list = new Command("list", "List installed profiles.");
        list.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            var registry = host.Services.GetRequiredService<IProfileRegistry>();
            IReadOnlyList<InstalledProfile> profiles = await registry.ListAsync(ct).ConfigureAwait(false);
            ctx.Emit(profiles.Select(p => new { p.FileName, p.Id, valid = p.IsValid, errors = p.Validation.Errors }), string.Join('\n', profiles.Select(p => $"{p.FileName}: {(p.IsValid ? p.Id + " " + p.Validation.Profile!.Version : "INVALID: " + (p.Validation.Errors.Count > 0 ? p.Validation.Errors[0] : string.Empty))}")));
            return ExitCodes.Success;
        }));
        var importPath = new Argument<string>("path") { Description = "Profile JSON file to import." };
        var import = new Command("import", "Validate and install a profile into the profiles folder.") { importPath };
        import.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            var registry = host.Services.GetRequiredService<IProfileRegistry>();
            ProfileValidationResult result = await registry.ImportAsync(pr.GetValue(importPath)!, ct).ConfigureAwait(false);
            ctx.Emit(new { valid = result.IsValid, id = result.Profile?.Id, errors = result.Errors }, result.IsValid ? $"Imported {result.Profile!.Id}." : "Not imported:\n  " + string.Join("\n  ", result.Errors));
            return result.IsValid ? ExitCodes.Success : ExitCodes.ConfigurationInvalid;
        }));
        var schemaCmd = new Command("schema", "Print the mapping profile JSON Schema.");
        schemaCmd.SetAction(_ =>
        {
            stdout.WriteLine(ProfileLoader.SchemaJson);
            return ExitCodes.Success;
        });
        profileCmd.Subcommands.Add(validate);
        profileCmd.Subcommands.Add(list);
        profileCmd.Subcommands.Add(import);
        profileCmd.Subcommands.Add(schemaCmd);

        // sftp
        var sftp = new Command("sftp", "SFTP acquisition.");
        var sftpTest = new Command("test", "Test the SFTP connection and host key (sanitized output).");
        sftpTest.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            IInputAcquirer acquirer = host.Services.GetServices<IInputAcquirer>().First(a => a.ProviderId == SftpAcquirer.Id);
            IReadOnlyList<string> lines = await acquirer.TestAsync(ct).ConfigureAwait(false);
            bool ok = lines.Any(l => l.StartsWith("Connected", StringComparison.Ordinal));
            ctx.Emit(new { ok, lines }, string.Join('\n', lines));
            return ok ? ExitCodes.Success : ExitCodes.ConfigurationInvalid;
        }));
        var secretName = new Argument<string>("name") { Description = "Secret name: sftp.password or sftp.key-passphrase." };
        var setSecret = new Command("set-secret", "Store a secret read from standard input (never pass secrets as arguments).") { secretName };
        setSecret.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            string name = pr.GetValue(secretName)!;
            if (name is not (SecretNames.SftpPassword or SecretNames.SftpKeyPassphrase))
            {
                stderr.WriteLine($"Unknown secret name '{name}'.");
                return ExitCodes.ConfigurationInvalid;
            }

            string? value = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value))
            {
                stderr.WriteLine("No secret was read from standard input.");
                return ExitCodes.ConfigurationInvalid;
            }

            var store = host.Services.GetRequiredService<ISecretStore>();
            await store.StoreAsync(SecretNames.Service, name, value, ct).ConfigureAwait(false);
            ctx.Emit(new { stored = name, provider = store.ProviderName }, $"Stored {name} in {store.ProviderName}.");
            return ExitCodes.Success;
        }));
        var deleteSecret = new Command("delete-secret", "Delete a stored secret.") { new Argument<string>("name") { Description = "Secret name." } };
        deleteSecret.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            string name = pr.GetValue((Argument<string>)deleteSecret.Arguments[0])!;
            var store = host.Services.GetRequiredService<ISecretStore>();
            bool deleted = await store.DeleteAsync(SecretNames.Service, name, ct).ConfigureAwait(false);
            ctx.Emit(new { deleted }, deleted ? "Deleted." : "Nothing to delete.");
            return ExitCodes.Success;
        }));
        sftp.Subcommands.Add(sftpTest);
        sftp.Subcommands.Add(setSecret);
        sftp.Subcommands.Add(deleteSecret);

        // diagnostics
        var bundle = new Option<string?>("--bundle") { Description = "Write a sanitized diagnostic bundle ZIP to this path." };
        var diagnostics = new Command("diagnostics", "Print sanitized environment and configuration facts.") { bundle };
        diagnostics.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            var service = host.Services.GetRequiredService<DiagnosticsService>();
            IReadOnlyList<KeyValuePair<string, string>> facts = await service.CollectAsync(ct).ConfigureAwait(false);
            string? bundlePath = pr.GetValue(bundle);
            if (bundlePath is not null)
            {
                bundlePath = await service.ExportBundleAsync(bundlePath, ct).ConfigureAwait(false);
            }

            ctx.Emit(new { facts = facts.ToDictionary(f => f.Key.Trim(), f => f.Value, StringComparer.Ordinal), bundle = bundlePath }, string.Join('\n', facts.Select(f => $"{f.Key,-28} {f.Value}")) + (bundlePath is null ? string.Empty : $"\nBundle written to {bundlePath}"));
            return ExitCodes.Success;
        }));

        // self-test
        var selfTest = new Command("self-test", "Process the bundled demo file end-to-end into a temporary folder and verify the workbook.");
        selfTest.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            string demo = Path.Combine(AppContext.BaseDirectory, "samples", "input", "demo-transactions.xml");
            if (!File.Exists(demo))
            {
                stderr.WriteLine($"Demo input not found at {demo}.");
                return ExitCodes.ConfigurationInvalid;
            }

            string tempOut = Path.Combine(Path.GetTempPath(), "finxml-selftest", Guid.NewGuid().ToString("N"));
            var pipeline = host.Services.GetRequiredService<ProcessingPipeline>();
            ProcessingResult result = await pipeline.RunAsync(new ProcessingRequest(demo, "demo-fintech-v1", tempOut, Force: true, Trigger: "self-test"), null, ct).ConfigureAwait(false);
            EmitProcessingResult(host, result, ctx);
            return result.IsSuccess ? ExitCodes.Success : ExitCodes.ProcessingFailed;
        }));

        // retention
        var retention = new Command("retention", "Apply the configured retention policies (disabled categories are skipped).");
        retention.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            var service = host.Services.GetRequiredService<RetentionService>();
            IReadOnlyList<RetentionOutcome> outcomes = await service.ApplyAsync(ct).ConfigureAwait(false);
            ctx.Emit(outcomes, string.Join('\n', outcomes.Select(o => $"{o.Category,-12} deleted {o.Deleted}, skipped {o.Skipped} {string.Join("; ", o.Notes)}")));
            return ExitCodes.Success;
        }));

        // benchmark
        var benchInput = new Option<string>("--input", "-i") { Description = "Input XML file.", Required = true };
        var benchProfile = new Option<string?>("--profile", "-p") { Description = "Profile id or path." };
        var benchOutput = new Option<string?>("--output", "-o") { Description = "Output folder." };
        var benchResult = new Option<string?>("--result") { Description = "Write the measurements as JSON to this path." };
        var benchmark = new Command("benchmark", "Process a file while sampling memory and record throughput. Uses --force.") { benchInput, benchProfile, benchOutput, benchResult };
        benchmark.SetAction((pr, ct) => WithHost(args, pr, stdout, stderr, rootOverride, async (host, ctx) =>
        {
            var pipeline = host.Services.GetRequiredService<ProcessingPipeline>();
            using var sampler = new PeakMemorySampler();
            ProcessingResult result = await pipeline.RunAsync(new ProcessingRequest(pr.GetValue(benchInput)!, pr.GetValue(benchProfile), pr.GetValue(benchOutput), Force: true, Trigger: "benchmark"), ctx.Quiet ? null : new ConsoleProgress(stdout), ct).ConfigureAwait(false);
            MemoryMeasurement m = sampler.Measure();
            long size = result.Job?.SourceSizeBytes ?? 0;
            double seconds = m.Elapsed.TotalSeconds;
            var measurement = new
            {
                outcome = result.Outcome.ToString(),
                inputBytes = size,
                elapsedSeconds = Math.Round(seconds, 2),
                throughputMBps = seconds > 0 ? Math.Round(size / 1_048_576d / seconds, 2) : 0,
                recordsPerSecond = seconds > 0 ? Math.Round((result.Job?.Counts.RecordsSeen ?? 0) / seconds) : 0,
                peakWorkingSetMB = Math.Round(m.PeakWorkingSetBytes / 1_048_576d, 1),
                peakManagedHeapMB = Math.Round(m.PeakManagedHeapBytes / 1_048_576d, 1),
                totalAllocatedMB = Math.Round(m.TotalAllocatedBytes / 1_048_576d, 1),
                gen0 = m.Gen0Collections,
                gen1 = m.Gen1Collections,
                gen2 = m.Gen2Collections,
                counts = result.Job?.Counts,
                output = result.OutputPath,
                outputBytes = result.OutputPath is not null && File.Exists(result.OutputPath) ? new FileInfo(result.OutputPath).Length : 0,
                platform = AppInfo.Platform,
                version = AppInfo.Version,
                timestampUtc = DateTimeOffset.UtcNow,
            };
            string json = JsonSerializer.Serialize(measurement, Json);
            string? resultPath = pr.GetValue(benchResult);
            if (resultPath is not null)
            {
                await File.WriteAllTextAsync(resultPath, json, ct).ConfigureAwait(false);
            }

            ctx.Emit(measurement, $"{result.SanitizedMessage}\nInput {size:N0} bytes in {seconds:F1}s ({measurement.throughputMBps} MB/s, {measurement.recordsPerSecond:N0} records/s)\nPeak working set {measurement.peakWorkingSetMB} MB, peak managed heap {measurement.peakManagedHeapMB} MB, allocated {measurement.totalAllocatedMB} MB, GC gen0/1/2 {m.Gen0Collections}/{m.Gen1Collections}/{m.Gen2Collections}");
            return ExitCodes.FromOutcome(result);
        }));

        root.Subcommands.Add(process);
        root.Subcommands.Add(schedule);
        root.Subcommands.Add(profileCmd);
        root.Subcommands.Add(sftp);
        root.Subcommands.Add(diagnostics);
        root.Subcommands.Add(selfTest);
        root.Subcommands.Add(retention);
        root.Subcommands.Add(benchmark);

        ParseResult parsed = root.Parse(args);
        return await parsed.InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> WithHost(string[] args, ParseResult parseResult, TextWriter stdout, TextWriter stderr, string? rootOverride, Func<IHost, OutputContext, Task<int>> action)
    {
        bool json = args.Contains("--json", StringComparer.Ordinal);
        bool quiet = args.Contains("--quiet", StringComparer.Ordinal);
        var ctx = new OutputContext(stdout, json, quiet);
        string[] hostArgs = args.Where(a => a is not ("--json" or "--quiet")).ToArray();
        try
        {
            HostApplicationBuilder builder = FinXmlHost.CreateBuilder([], console: !json, rootOverride);
            using IHost host = builder.Build();
            await host.Services.GetRequiredService<IProcessingRepository>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await InstallBuiltInProfilesAsync(host).ConfigureAwait(false);
            _ = hostArgs;
            return await action(host, ctx).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("Cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Error: {ex.GetType().Name}: {ex.Message}");
            return ExitCodes.Unexpected;
        }
    }

    private static async Task InstallBuiltInProfilesAsync(IHost host)
    {
        string samples = Path.Combine(AppContext.BaseDirectory, "samples", "profiles");
        if (!Directory.Exists(samples))
        {
            return;
        }

        var builtIn = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(samples, "*.json"))
        {
            builtIn[Path.GetFileName(file)] = await File.ReadAllTextAsync(file).ConfigureAwait(false);
        }

        await host.Services.GetRequiredService<IProfileRegistry>().EnsureBuiltInProfilesAsync(builtIn, CancellationToken.None).ConfigureAwait(false);
    }

    private static void EmitProcessingResult(IHost host, ProcessingResult result, OutputContext ctx)
    {
        if (ctx.Json)
        {
            ctx.Emit(new { outcome = result.Outcome.ToString(), jobId = result.Job?.Id, message = result.SanitizedMessage, output = result.OutputPath, report = result.ReportPath, counts = result.Job?.Counts }, string.Empty);
            return;
        }

        if (result.Report is not null && !ctx.Quiet)
        {
            ctx.Out.WriteLine(host.Services.GetRequiredService<IReportWriter>().RenderText(result.Report));
        }

        ctx.Out.WriteLine(result.SanitizedMessage);
        if (result.OutputPath is not null)
        {
            ctx.Out.WriteLine($"Workbook: {result.OutputPath}");
        }

        if (result.ReportPath is not null)
        {
            ctx.Out.WriteLine($"Report:   {result.ReportPath}");
        }
    }

    private sealed class OutputContext
    {
        public OutputContext(TextWriter output, bool json, bool quiet)
        {
            Out = output;
            Json = json;
            Quiet = quiet;
        }

        public TextWriter Out { get; }

        public bool Json { get; }

        public bool Quiet { get; }

        public void Emit(object payload, string text)
        {
            if (Json)
            {
                Out.WriteLine(JsonSerializer.Serialize(payload, CliApp.Json));
            }
            else if (text.Length > 0)
            {
                Out.WriteLine(text);
            }
        }
    }

    private sealed class ConsoleProgress : IProgress<ProcessingProgress>
    {
        private readonly TextWriter _writer;
        private long _lastReportTicks;

        public ConsoleProgress(TextWriter writer)
        {
            _writer = writer;
        }

        public void Report(ProcessingProgress value)
        {
            long now = Environment.TickCount64;
            if (now - _lastReportTicks < 1000 && value.PercentComplete < 100)
            {
                return;
            }

            _lastReportTicks = now;
            string percent = value.PercentComplete is double p ? $"{p,5:F1}%" : "  n/a";
            _writer.WriteLine($"[{value.Phase}] {percent}  seen {value.RecordsSeen:N0}  accepted {value.RecordsAccepted:N0}  rejected {value.RecordsRejected:N0}  duplicates {value.RecordDuplicates:N0}  rows {value.RowsWritten:N0}  {value.Elapsed:mm\\:ss}");
        }
    }
}
