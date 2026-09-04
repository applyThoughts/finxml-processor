using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Application.Reports;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Jobs;
using FinXmlProcessor.TestDataGenerator;

namespace FinXmlProcessor.IntegrationTests;

public class PipelineIntegrationTests
{
    [Fact]
    public async Task Demo_file_produces_verified_workbook_report_and_history()
    {
        await using var host = new TestHost();
        await host.InitializeAsync();
        string output = Path.Combine(host.Root, "out");
        var progress = new List<ProcessingProgress>();
        ProcessingResult result = await host.Get<ProcessingPipeline>().RunAsync(new ProcessingRequest(TestHost.DemoInputPath, OutputDirectory: output, Trigger: "test"), new SyncProgress<ProcessingProgress>(progress.Add), CancellationToken.None);

        result.Outcome.Should().Be(ProcessingOutcome.CompletedWithWarnings, result.SanitizedMessage);
        ProcessingJob job = result.Job!;
        job.Status.Should().Be(JobStatus.CompletedWithWarnings);
        job.Counts.RecordsSeen.Should().Be(250);
        (job.Counts.RecordsAccepted + job.Counts.RecordsRejected).Should().Be(250);
        job.Counts.RecordsRejected.Should().BeGreaterThan(0);
        job.Counts.RecordDuplicates.Should().BeGreaterThan(0);
        job.Counts.RowsWritten.Should().Be(job.Counts.RecordsAccepted);
        job.SourceSha256.Should().HaveLength(64);
        job.OutputSha256.Should().HaveLength(64);
        job.Transitions.Select(t => t.To).Should().Equal(JobStatus.Ready, JobStatus.Validating, JobStatus.Processing, JobStatus.GeneratingOutput, JobStatus.CompletedWithWarnings);
        Path.GetFileName(result.OutputPath!).Should().MatchRegex(@"^demo-fintech-v1_\d{4}-\d{2}-\d{2}_[0-9a-f]{8}\.xlsx$");
        File.Exists(result.OutputPath).Should().BeTrue();
        File.Exists(result.OutputPath + ".part").Should().BeFalse();
        Directory.Exists(result.OutputPath + ".spool").Should().BeFalse();
        Directory.EnumerateFiles(host.Paths.Staging).Should().BeEmpty("duplicate-key scratch files are removed");

        // Workbook content
        using (SpreadsheetDocument doc = SpreadsheetDocument.Open(result.OutputPath!, false))
        {
            var sheets = doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Select(s => s.Name!.Value).ToList();
            sheets.Should().Equal("Summary", "Transactions", "Rejected Records");
            WorksheetPart data = (WorksheetPart)doc.WorkbookPart.GetPartById(doc.WorkbookPart.Workbook.Sheets!.Elements<Sheet>().Single(s => s.Name == "Transactions").Id!.Value!);
            var rows = data.Worksheet.Descendants<Row>().ToList();
            rows.Count.Should().Be((int)job.Counts.RowsWritten + 1);
            var header = rows[0].Elements<Cell>().Select(c => c.InlineString!.Text!.Text).ToList();
            header.Should().StartWith(["Transaction ID", "Account Reference", "Posted (UTC)"]);
            var firstRow = rows[1].Elements<Cell>().ToList();
            firstRow[0].InlineString!.Text!.Text.Should().StartWith("TXN-");
            firstRow[13].InlineString!.Text!.Text.Should().Be("DEMO");
            data.Worksheet.Descendants<CellFormula>().Should().BeEmpty();
            WorksheetPart rejected = (WorksheetPart)doc.WorkbookPart.GetPartById(doc.WorkbookPart.Workbook.Sheets!.Elements<Sheet>().Single(s => s.Name == "Rejected Records").Id!.Value!);
            string rejectedText = string.Join("|", rejected.Worksheet.Descendants<Text>().Select(t => t.Text));
            rejectedText.Should().Contain("MAP-").And.NotContain("Counterparty=", "counterparty is not allowed in rejection output");
            rejectedText.Should().NotContain("Amount=", "amount is restricted");
            WorksheetPart summary = (WorksheetPart)doc.WorkbookPart.GetPartById(doc.WorkbookPart.Workbook.Sheets!.Elements<Sheet>().First().Id!.Value!);
            string summaryText = string.Join("|", summary.Worksheet.Descendants<Text>().Select(t => t.Text));
            summaryText.Should().Contain(job.SourceSha256!).And.Contain("demo-fintech-v1 1.0.0").And.Contain("Synthetic demo profile");
        }

        // Report
        ProcessingReport? report = await host.Get<IReportWriter>().ReadAsync(result.ReportPath!, CancellationToken.None);
        report.Should().NotBeNull();
        report!.Counts.Should().Be(job.Counts);
        report.IssueCodeCounts.Should().ContainKey(IssueCodes.ValRecordDuplicate);
        report.TimingsMs.Total.Should().BeGreaterThan(0);
        report.Output!.Sha256.Should().Be(job.OutputSha256);
        report.Issues.Should().OnlyContain(i => !i.Message.Contains("ACC-", StringComparison.Ordinal), "no account references leak into the report");
        host.Get<IReportWriter>().RenderText(report).Should().Contain("Records:");

        // History
        ProcessingJob? stored = await host.Get<IProcessingRepository>().GetJobAsync(job.Id, CancellationToken.None);
        stored!.Status.Should().Be(JobStatus.CompletedWithWarnings);
        stored.ReportPath.Should().Be(result.ReportPath);
        progress.Should().NotBeEmpty();
        progress.Last().RecordsSeen.Should().Be(250);
    }

    [Fact]
    public async Task Duplicate_content_is_blocked_unless_forced()
    {
        await using var host = new TestHost();
        await host.InitializeAsync();
        ProcessingPipeline pipeline = host.Get<ProcessingPipeline>();
        ProcessingResult first = await pipeline.RunAsync(new ProcessingRequest(TestHost.DemoInputPath), null, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        string copy = host.StageDemoInput("same-content-other-name.xml");
        ProcessingResult blocked = await pipeline.RunAsync(new ProcessingRequest(copy), null, CancellationToken.None);
        blocked.Outcome.Should().Be(ProcessingOutcome.DuplicateBlocked);
        blocked.Job!.Status.Should().Be(JobStatus.Failed);
        string shortId = first.Job!.Id.ToString("N")[..8];
        blocked.Job.Issues.Should().Contain(i => i.Code == IssueCodes.FileDuplicate && i.Message.Contains(shortId, StringComparison.Ordinal));
        blocked.OutputPath.Should().BeNull();

        ProcessingResult forced = await pipeline.RunAsync(new ProcessingRequest(copy, Force: true, RerunOfJobId: first.Job!.Id), null, CancellationToken.None);
        forced.IsSuccess.Should().BeTrue();
        forced.Job!.RerunOfJobId.Should().Be(first.Job.Id);
        forced.Job.Issues.Should().Contain(i => i.Code == IssueCodes.FileDuplicate && i.Severity == IssueSeverity.Warning);
        forced.OutputPath.Should().NotBe(first.OutputPath);
    }

    [Fact]
    public async Task Malformed_managed_input_is_quarantined_and_no_output_is_published()
    {
        await using var host = new TestHost();
        await host.InitializeAsync();
        string input = Path.Combine(host.Paths.DefaultInput, "truncated.xml");
        SyntheticDataGenerator.GenerateFile(input, new GeneratorOptions { Records = 40, Truncate = true });
        string output = Path.Combine(host.Root, "out");

        ProcessingResult result = await host.Get<ProcessingPipeline>().RunAsync(new ProcessingRequest(input, OutputDirectory: output), null, CancellationToken.None);
        result.Outcome.Should().Be(ProcessingOutcome.Quarantined);
        result.Job!.Status.Should().Be(JobStatus.Quarantined);
        result.Job.Issues.Should().Contain(i => i.Code == IssueCodes.XmlMalformed && i.Severity == IssueSeverity.Fatal);
        File.Exists(input).Should().BeFalse("managed inputs are moved to quarantine");
        IReadOnlyList<QuarantineEntry> entries = await host.Get<IQuarantineService>().ListAsync(CancellationToken.None);
        entries.Should().ContainSingle().Which.QuarantinedPath.Should().StartWith(host.Paths.Quarantine);
        Directory.Exists(output).Should().BeTrue();
        Directory.EnumerateFiles(output).Should().BeEmpty("no workbook, staging or spool remains");
        Directory.EnumerateDirectories(output).Should().BeEmpty();
    }

    [Fact]
    public async Task External_unsupported_input_is_recorded_not_moved()
    {
        await using var host = new TestHost();
        await host.InitializeAsync();
        string external = Path.Combine(Path.GetTempPath(), "finxml-tests", $"gz-{Guid.NewGuid():N}.xml");
        await File.WriteAllBytesAsync(external, [0x1F, 0x8B, 0x08, 0, 1, 2, 3]);
        ProcessingResult result = await host.Get<ProcessingPipeline>().RunAsync(new ProcessingRequest(external), null, CancellationToken.None);
        result.Outcome.Should().Be(ProcessingOutcome.Quarantined);
        File.Exists(external).Should().BeTrue();
        (await host.Get<IQuarantineService>().ListAsync(CancellationToken.None)).Single().QuarantinedPath.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_leaves_no_output_and_marks_job_cancelled()
    {
        await using var host = new TestHost();
        await host.InitializeAsync();
        string input = Path.Combine(host.Root, "big.xml");
        SyntheticDataGenerator.GenerateFile(input, new GeneratorOptions { Records = 20_000, Indent = false });
        string output = Path.Combine(host.Root, "out");
        using var cts = new CancellationTokenSource();
        // Synchronous so the cancel fires on the processing thread, before the test disposes the token source.
        var progress = new SyncProgress<ProcessingProgress>(p =>
        {
            if (p.RecordsSeen > 500)
            {
                cts.Cancel();
            }
        });

        ProcessingResult result = await host.Get<ProcessingPipeline>().RunAsync(new ProcessingRequest(input, OutputDirectory: output), progress, cts.Token);
        result.Outcome.Should().Be(ProcessingOutcome.Cancelled);
        result.Job!.Status.Should().Be(JobStatus.Cancelled);
        result.Job.Counts.RecordsSeen.Should().BeGreaterThan(0).And.BeLessThan(20_000);
        File.Exists(input).Should().BeTrue("the original input is never touched");
        Directory.EnumerateFileSystemEntries(output).Should().BeEmpty();
        Directory.EnumerateFiles(host.Paths.Staging).Should().BeEmpty();
    }

    [Fact]
    public async Task Concurrent_runs_are_serialized_by_the_lock()
    {
        await using var host = new TestHost();
        await host.InitializeAsync();
        IAsyncDisposable? lease = await host.Get<IProcessingLock>().TryAcquireAsync("simulated worker", CancellationToken.None);
        lease.Should().NotBeNull();
        ProcessingResult result = await host.Get<ProcessingPipeline>().RunAsync(new ProcessingRequest(TestHost.DemoInputPath), null, CancellationToken.None);
        result.Outcome.Should().Be(ProcessingOutcome.LockUnavailable);
        result.Job.Should().BeNull();
        result.SanitizedMessage.Should().Contain("simulated worker");
        await lease!.DisposeAsync();
        (await host.Get<ProcessingPipeline>().RunAsync(new ProcessingRequest(TestHost.DemoInputPath), null, CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Local_folder_delivery_is_recorded_with_hash()
    {
        string delivered = Path.Combine(Path.GetTempPath(), "finxml-tests", "delivered", Guid.NewGuid().ToString("N"));
        await using var host = new TestHost("Delivery:Provider=local-folder", $"Delivery:LocalFolder={delivered}");
        await host.InitializeAsync();
        ProcessingResult result = await host.Get<ProcessingPipeline>().RunAsync(new ProcessingRequest(TestHost.DemoInputPath), null, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.SanitizedMessage);
        result.Job!.Status.Should().Be(JobStatus.Delivered);
        result.Report!.Delivery!.Succeeded.Should().BeTrue();
        result.Report.Delivery.DeliveredSha256.Should().Be(result.Job.OutputSha256);
        File.Exists(result.Report.Delivery.DeliveredPath).Should().BeTrue();
        IReadOnlyList<DeliveryAttempt> attempts = await host.Get<IProcessingRepository>().GetDeliveryAttemptsAsync(result.Job.Id, CancellationToken.None);
        attempts.Should().ContainSingle().Which.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Sheet_splitting_is_driven_by_the_configured_row_limit()
    {
        await using var host = new TestHost("Processing:MaxRowsPerSheet=101");
        await host.InitializeAsync();
        ProcessingResult result = await host.Get<ProcessingPipeline>().RunAsync(new ProcessingRequest(TestHost.DemoInputPath), null, CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        using SpreadsheetDocument doc = SpreadsheetDocument.Open(result.OutputPath!, false);
        var names = doc.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Select(s => s.Name!.Value!).ToList();
        names.Should().Contain("Transactions").And.Contain("Transactions (2)").And.Contain("Transactions (3)");
        result.Job!.Issues.Should().NotContain(i => i.Severity == IssueSeverity.Fatal);
    }

    [Fact]
    public async Task Unknown_profile_is_a_configuration_error_without_a_job()
    {
        await using var host = new TestHost();
        await host.InitializeAsync();
        ProcessingResult result = await host.Get<ProcessingPipeline>().RunAsync(new ProcessingRequest(TestHost.DemoInputPath, ProfileId: "does-not-exist"), null, CancellationToken.None);
        result.Outcome.Should().Be(ProcessingOutcome.ConfigurationInvalid);
        result.Job.Should().BeNull();
        (await host.Get<IProcessingRepository>().QueryJobsAsync(new JobQuery(), CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Default_namespace_variant_of_the_same_data_maps_identically()
    {
        await using var host = new TestHost();
        await host.InitializeAsync();
        string prefixed = Path.Combine(host.Root, "prefixed.xml");
        string defaultNs = Path.Combine(host.Root, "default-ns.xml");
        SyntheticDataGenerator.GenerateFile(prefixed, new GeneratorOptions { Records = 300, Seed = 5, DuplicateRate = 0.02, MissingRequiredRate = 0.02 });
        SyntheticDataGenerator.GenerateFile(defaultNs, new GeneratorOptions { Records = 300, Seed = 5, DuplicateRate = 0.02, MissingRequiredRate = 0.02, DefaultNamespace = true });
        ProcessingPipeline pipeline = host.Get<ProcessingPipeline>();
        ProcessingResult a = await pipeline.RunAsync(new ProcessingRequest(prefixed), null, CancellationToken.None);
        ProcessingResult b = await pipeline.RunAsync(new ProcessingRequest(defaultNs), null, CancellationToken.None);
        a.IsSuccess.Should().BeTrue();
        b.IsSuccess.Should().BeTrue();
        b.Job!.Counts.Should().Be(a.Job!.Counts);
    }
}

/// <summary>
/// Unlike <see cref="Progress{T}"/>, which posts callbacks to the thread pool when no synchronization context
/// exists, this reporter runs the handler inline so tests observe every report in order and before the run returns.
/// </summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    private readonly object _gate = new();

    public SyncProgress(Action<T> handler)
    {
        _handler = handler;
    }

    public void Report(T value)
    {
        lock (_gate)
        {
            _handler(value);
        }
    }
}
