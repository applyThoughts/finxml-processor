using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Jobs;
using FinXmlProcessor.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinXmlProcessor.Infrastructure.Tests;

public class SqliteProcessingRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 23, 0, 0, TimeSpan.Zero);
    private readonly TempRoot _root = new();
    private readonly SqliteProcessingRepository _repository;

    public SqliteProcessingRepositoryTests()
    {
        _repository = new SqliteProcessingRepository(_root.Paths.DatabaseFile, NullLogger<SqliteProcessingRepository>.Instance);
    }

    [Fact]
    public async Task Migration_is_idempotent_and_enables_wal()
    {
        await _repository.InitializeAsync(CancellationToken.None);
        var second = new SqliteProcessingRepository(_root.Paths.DatabaseFile, NullLogger<SqliteProcessingRepository>.Instance);
        await second.InitializeAsync(CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={_root.Paths.DatabaseFile}");
        await connection.OpenAsync();
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version;";
        Convert.ToInt64(await cmd.ExecuteScalarAsync()).Should().Be(1);
        cmd.CommandText = "PRAGMA journal_mode;";
        ((string)(await cmd.ExecuteScalarAsync())!).Should().Be("wal");
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='ix_jobs_source_sha256';";
        (await cmd.ExecuteScalarAsync()).Should().Be("ix_jobs_source_sha256");
    }

    [Fact]
    public async Task Job_round_trips_with_transitions_and_issues()
    {
        var job = new ProcessingJob(Guid.NewGuid(), "in.xml", null, "demo", "1.0.0", "hash", T0, null, "cli") { BusinessDate = new DateOnly(2026, 9, 3) };
        await _repository.SaveJobAsync(job, CancellationToken.None);
        job.TransitionTo(JobStatus.Ready, T0.AddSeconds(1));
        job.TransitionTo(JobStatus.Validating, T0.AddSeconds(2));
        job.SetSourceHash("abc");
        job.SourceSizeBytes = 123;
        job.AddIssue(RecordIssue.Rejection("MAP-001", "f", "missing", 7));
        job.AddIssue(RecordIssue.Warning("W-1", null, "warn"));
        job.Counts = new ProcessingCounts(10, 8, 2, 1, 8, 1);
        job.OutputPath = "out.xlsx";
        job.OutputSha256 = "def";
        job.ReportPath = "r.json";
        await _repository.SaveJobAsync(job, CancellationToken.None);

        ProcessingJob? loaded = await _repository.GetJobAsync(job.Id, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(JobStatus.Validating);
        loaded.Transitions.Should().HaveCount(2);
        loaded.Transitions[1].At.Should().Be(T0.AddSeconds(2));
        loaded.Issues.Should().HaveCount(2);
        loaded.Issues[0].Should().Be(new RecordIssue("MAP-001", IssueSeverity.RecordRejected, "f", "missing", 7));
        loaded.Counts.Should().Be(new ProcessingCounts(10, 8, 2, 1, 8, 1));
        loaded.SourceSha256.Should().Be("abc");
        loaded.SourceSizeBytes.Should().Be(123);
        loaded.OutputPath.Should().Be("out.xlsx");
        loaded.BusinessDate.Should().Be(new DateOnly(2026, 9, 3));
        loaded.Trigger.Should().Be("cli");
        loaded.CreatedAt.Should().Be(T0);
    }

    [Fact]
    public async Task Duplicate_lookup_only_matches_successful_jobs()
    {
        var failed = new ProcessingJob(Guid.NewGuid(), "a.xml", "same", "demo", "1.0.0", "h", T0);
        failed.TransitionTo(JobStatus.Failed, T0.AddSeconds(1), "boom");
        await _repository.SaveJobAsync(failed, CancellationToken.None);
        (await _repository.FindBySha256Async("same", CancellationToken.None)).Should().BeNull();

        var ok = new ProcessingJob(Guid.NewGuid(), "b.xml", "same", "demo", "1.0.0", "h", T0.AddMinutes(1));
        foreach (JobStatus s in new[] { JobStatus.Ready, JobStatus.Validating, JobStatus.Processing, JobStatus.GeneratingOutput, JobStatus.Completed })
        {
            ok.TransitionTo(s, T0.AddMinutes(2));
        }

        await _repository.SaveJobAsync(ok, CancellationToken.None);
        FileDuplicateMatch? match = await _repository.FindBySha256Async("same", CancellationToken.None);
        match.Should().NotBeNull();
        match!.JobId.Should().Be(ok.Id);
        match.SourceFileName.Should().Be("b.xml");
    }

    [Fact]
    public async Task Query_filters_and_orders_newest_first()
    {
        for (int i = 0; i < 5; i++)
        {
            var job = new ProcessingJob(Guid.NewGuid(), $"file{i}.xml", null, "demo", "1.0.0", "h", T0.AddMinutes(i));
            if (i % 2 == 0)
            {
                job.TransitionTo(JobStatus.Failed, T0.AddMinutes(i));
            }

            await _repository.SaveJobAsync(job, CancellationToken.None);
        }

        IReadOnlyList<ProcessingJob> all = await _repository.QueryJobsAsync(new JobQuery(), CancellationToken.None);
        all.Should().HaveCount(5);
        all[0].SourceFileName.Should().Be("file4.xml");
        (await _repository.QueryJobsAsync(new JobQuery(Status: JobStatus.Failed), CancellationToken.None)).Should().HaveCount(3);
        (await _repository.QueryJobsAsync(new JobQuery(SourceNameContains: "file3"), CancellationToken.None)).Should().ContainSingle();
        (await _repository.QueryJobsAsync(new JobQuery(Limit: 2), CancellationToken.None)).Should().HaveCount(2);
        (await _repository.QueryJobsAsync(new JobQuery(CreatedAfter: T0.AddMinutes(2)), CancellationToken.None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Scheduled_run_ledger_is_insert_once()
    {
        var entry = new ScheduledRunEntry("s", new DateOnly(2026, 9, 3), T0, null, "claimed", null);
        (await _repository.TryRecordScheduledRunAsync(entry, CancellationToken.None)).Should().BeTrue();
        (await _repository.TryRecordScheduledRunAsync(entry with { Outcome = "other" }, CancellationToken.None)).Should().BeFalse();
        (await _repository.GetScheduledRunAsync("s", new DateOnly(2026, 9, 3), CancellationToken.None))!.Outcome.Should().Be("claimed");
        Guid jobId = Guid.NewGuid();
        await _repository.UpdateScheduledRunAsync(entry with { Outcome = "completed", JobId = jobId }, CancellationToken.None);
        ScheduledRunEntry? updated = await _repository.GetScheduledRunAsync("s", new DateOnly(2026, 9, 3), CancellationToken.None);
        updated!.Outcome.Should().Be("completed");
        updated.JobId.Should().Be(jobId);
        (await _repository.GetScheduledRunAsync("s", new DateOnly(2026, 9, 4), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Delivery_attempts_settings_and_quarantine_persist()
    {
        Guid jobId = Guid.NewGuid();
        await _repository.RecordDeliveryAttemptAsync(new DeliveryAttempt(jobId, "local-folder", T0, false, null, null, "nope"), CancellationToken.None);
        await _repository.RecordDeliveryAttemptAsync(new DeliveryAttempt(jobId, "local-folder", T0.AddSeconds(5), true, "/x/y.xlsx", "sha", null), CancellationToken.None);
        IReadOnlyList<DeliveryAttempt> attempts = await _repository.GetDeliveryAttemptsAsync(jobId, CancellationToken.None);
        attempts.Should().HaveCount(2);
        attempts[1].Succeeded.Should().BeTrue();
        attempts[1].DeliveredPath.Should().Be("/x/y.xlsx");

        await _repository.SetSettingAsync("k", "v", CancellationToken.None);
        (await _repository.GetSettingAsync("k", CancellationToken.None)).Should().Be("v");
        await _repository.SetSettingAsync("k", null, CancellationToken.None);
        (await _repository.GetSettingAsync("k", CancellationToken.None)).Should().BeNull();

        var q = new QuarantineEntry(Guid.NewGuid(), jobId, "/in/bad.xml", "/q/bad.xml", "XML-001", "malformed", T0, "quarantined");
        await _repository.SaveQuarantineEntryAsync(q, CancellationToken.None);
        (await _repository.ListQuarantineAsync(CancellationToken.None)).Should().ContainSingle().Which.Should().Be(q);
        await _repository.SaveQuarantineEntryAsync(q with { Status = "deleted", QuarantinedPath = null }, CancellationToken.None);
        (await _repository.GetQuarantineEntryAsync(q.Id, CancellationToken.None))!.Status.Should().Be("deleted");
    }

    [Fact]
    public async Task History_retention_deletes_old_rows_only()
    {
        var old = new ProcessingJob(Guid.NewGuid(), "old.xml", null, "demo", "1.0.0", "h", T0.AddDays(-100));
        old.TransitionTo(JobStatus.Ready, T0.AddDays(-100));
        var recent = new ProcessingJob(Guid.NewGuid(), "new.xml", null, "demo", "1.0.0", "h", T0);
        await _repository.SaveJobAsync(old, CancellationToken.None);
        await _repository.SaveJobAsync(recent, CancellationToken.None);
        (await _repository.DeleteJobsOlderThanAsync(T0.AddDays(-30), CancellationToken.None)).Should().Be(1);
        (await _repository.GetJobAsync(old.Id, CancellationToken.None)).Should().BeNull();
        (await _repository.GetJobAsync(recent.Id, CancellationToken.None)).Should().NotBeNull();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _root.Dispose();
    }
}
