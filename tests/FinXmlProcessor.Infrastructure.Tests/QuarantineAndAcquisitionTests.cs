using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Infrastructure.Acquisition;
using FinXmlProcessor.Infrastructure.Persistence;
using FinXmlProcessor.Infrastructure.Quarantine;
using FinXmlProcessor.Infrastructure.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;

namespace FinXmlProcessor.Infrastructure.Tests;

public class QuarantineAndAcquisitionTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly SqliteProcessingRepository _repository;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 9, 3, 22, 0));
    private readonly ProcessingOptions _options = new() { StabilityWindowMilliseconds = 0 };

    public QuarantineAndAcquisitionTests()
    {
        _repository = new SqliteProcessingRepository(_root.Paths.DatabaseFile, NullLogger<SqliteProcessingRepository>.Instance);
    }

    private QuarantineService CreateQuarantine() => new(_root.Paths, _repository, Options.Monitor(_options), _clock, NullLogger<QuarantineService>.Instance);

    [Fact]
    public async Task Managed_file_is_moved_restored_and_deleted_inside_roots_only()
    {
        string input = Path.Combine(_root.Paths.DefaultInput, "bad.xml");
        await File.WriteAllTextAsync(input, "<broken");
        QuarantineService service = CreateQuarantine();
        Guid jobId = Guid.NewGuid();

        QuarantineEntry entry = await service.QuarantineAsync(jobId, input, "XML-001", "Malformed XML at line 1, position 8.", moveFile: true, CancellationToken.None);
        File.Exists(input).Should().BeFalse();
        entry.QuarantinedPath.Should().StartWith(_root.Paths.Quarantine);
        File.Exists(entry.QuarantinedPath).Should().BeTrue();
        Path.GetFileName(entry.QuarantinedPath!).Should().EndWith("_bad.xml");
        entry.Status.Should().Be("quarantined");
        (await service.ListAsync(CancellationToken.None)).Should().ContainSingle();

        QuarantineEntry restored = await service.RestoreAsync(entry.Id, CancellationToken.None);
        restored.Status.Should().Be("restored");
        File.Exists(input).Should().BeTrue();
        await FluentActions.Awaiting(() => service.RestoreAsync(entry.Id, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();

        QuarantineEntry again = await service.QuarantineAsync(jobId, input, "XML-001", "still bad", moveFile: true, CancellationToken.None);
        QuarantineEntry deleted = await service.DeleteAsync(again.Id, CancellationToken.None);
        deleted.Status.Should().Be("deleted");
        File.Exists(again.QuarantinedPath!).Should().BeFalse();
    }

    [Fact]
    public async Task External_file_is_recorded_but_never_moved()
    {
        string external = Path.Combine(Path.GetTempPath(), "finxml-tests", $"external-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(external, "<x/>");
        QuarantineEntry entry = await CreateQuarantine().QuarantineAsync(null, external, "FILE-008", "not xml", moveFile: false, CancellationToken.None);
        File.Exists(external).Should().BeTrue();
        entry.QuarantinedPath.Should().BeNull();
        entry.Status.Should().Be("recorded");
        await FluentActions.Awaiting(() => CreateQuarantine().DeleteAsync(entry.Id, CancellationToken.None)).Should().NotThrowAsync();
        File.Exists(external).Should().BeTrue("deleting an entry never touches an external original");
    }

    [Fact]
    public async Task Tampered_quarantine_row_cannot_delete_outside_the_folder()
    {
        string victim = Path.Combine(_root.Paths.Database, "victim.txt");
        await File.WriteAllTextAsync(victim, "x");
        var tampered = new QuarantineEntry(Guid.NewGuid(), null, "orig", victim, "X", "x", _clock.UtcNowOffset, "quarantined");
        await _repository.SaveQuarantineEntryAsync(tampered, CancellationToken.None);
        await FluentActions.Awaiting(() => CreateQuarantine().DeleteAsync(tampered.Id, CancellationToken.None)).Should().ThrowAsync<UnauthorizedAccessException>();
        File.Exists(victim).Should().BeTrue();
    }

    [Fact]
    public async Task Local_acquirer_picks_newest_unprocessed_files()
    {
        string a = Path.Combine(_root.Paths.DefaultInput, "a.xml");
        string b = Path.Combine(_root.Paths.DefaultInput, "b.xml");
        string ignored = Path.Combine(_root.Paths.DefaultInput, "c.txt");
        string partial = Path.Combine(_root.Paths.DefaultInput, "d.xml.part");
        await File.WriteAllTextAsync(a, "<a/>");
        await File.WriteAllTextAsync(b, "<b/>");
        await File.WriteAllTextAsync(ignored, "<c/>");
        await File.WriteAllTextAsync(partial, "<d/>");
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow);

        var duplicates = Substitute.For<IFileDuplicateDetector>();
        duplicates.FindBySha256Async(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((FileDuplicateMatch?)null);
        var acquirer = new LocalFolderAcquirer(_root.Paths, Options.Monitor(_options), duplicates, NullLogger<LocalFolderAcquirer>.Instance);
        AcquisitionResult result = await acquirer.AcquireAsync(CancellationToken.None);
        result.Inputs.Select(i => i.OriginalName).Should().Equal("b.xml", "a.xml");
        result.Inputs[0].Sha256.Should().HaveLength(64);

        duplicates.FindBySha256Async(result.Inputs[0].Sha256, Arg.Any<CancellationToken>()).Returns(new FileDuplicateMatch(Guid.NewGuid(), "b.xml", _clock.UtcNowOffset, "Completed"));
        AcquisitionResult second = await acquirer.AcquireAsync(CancellationToken.None);
        second.Inputs.Select(i => i.OriginalName).Should().Equal("a.xml");
        second.Diagnostics.Should().Contain(d => d.Contains("already processed", StringComparison.Ordinal));
        (await acquirer.TestAsync(CancellationToken.None)).Should().Contain(l => l.Contains("exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coordinator_claims_the_date_once_and_records_no_input()
    {
        var schedule = Substitute.For<IScheduleService>();
        schedule.ScheduleId.Returns("s");
        var occurrence = new ScheduledOccurrence(new LocalDate(2026, 9, 3), Instant.FromUtc(2026, 9, 3, 23, 0).InZone(Application.Scheduling.BusinessCalendar.EasternZone), Instant.FromUtc(2026, 9, 3, 23, 0));
        schedule.EvaluateAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>()).Returns(new DueRunDecision(true, occurrence, false, "due"));
        var acquirer = Substitute.For<IInputAcquirer>();
        acquirer.ProviderId.Returns("local");
        acquirer.IsConfigured.Returns(true);
        acquirer.AcquireAsync(Arg.Any<CancellationToken>()).Returns(new AcquisitionResult([], ["0 file(s) match"]));
        ProcessingPipeline pipeline = CreatePipelineStub();
        var coordinator = new ScheduledRunCoordinator(schedule, _repository, [acquirer], pipeline, _clock, NullLogger<ScheduledRunCoordinator>.Instance);

        ScheduledRunResult first = await coordinator.RunDueAsync(null, CancellationToken.None);
        first.Ran.Should().BeFalse();
        first.Message.Should().Contain("No unprocessed input");
        ScheduledRunEntry? ledger = await _repository.GetScheduledRunAsync("s", new DateOnly(2026, 9, 3), CancellationToken.None);
        ledger!.Outcome.Should().Be(ScheduledRunOutcomes.NoInput);

        // A second attempt for the same date may retry because the first found no input.
        ScheduledRunResult second = await coordinator.RunDueAsync(null, CancellationToken.None);
        second.Ran.Should().BeFalse();
        await acquirer.Received(2).AcquireAsync(Arg.Any<CancellationToken>());

        // Once the ledger holds a completed outcome the coordinator must not run again.
        await _repository.UpdateScheduledRunAsync(ledger with { Outcome = ScheduledRunOutcomes.Completed }, CancellationToken.None);
        schedule.EvaluateAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>()).Returns(new DueRunDecision(true, occurrence, false, "due"));
        ScheduledRunResult third = await coordinator.RunDueAsync(null, CancellationToken.None);
        third.Ran.Should().BeFalse();
        third.Message.Should().Contain("already claimed");
        await acquirer.Received(2).AcquireAsync(Arg.Any<CancellationToken>());
    }

    private ProcessingPipeline CreatePipelineStub()
    {
        // The pipeline is never reached in these tests (no input); a minimally wired instance satisfies the constructor.
        return new ProcessingPipeline(
            Substitute.For<Application.Profiles.IProfileRegistry>(),
            Substitute.For<IInputValidator>(),
            Substitute.For<IFileDuplicateDetector>(),
            Substitute.For<IRecordReaderFactory>(),
            [],
            Substitute.For<IRecordDuplicateSetFactory>(),
            Substitute.For<IWorkbookWriter>(),
            _repository,
            Substitute.For<IReportWriter>(),
            Substitute.For<IQuarantineService>(),
            Substitute.For<IProcessingLock>(),
            [],
            _root.Paths,
            _clock,
            Options.Monitor(_options),
            NullLogger<ProcessingPipeline>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _root.Dispose();
    }
}
