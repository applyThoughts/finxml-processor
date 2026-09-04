using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Scheduling;
using FinXmlProcessor.Infrastructure.Scheduling;
using NodaTime;

namespace FinXmlProcessor.Infrastructure.Tests;

public class SchedulingTests
{
    private static DailyScheduleService Create(IProcessingRepository? repository = null, bool enabled = true, string time = "19:00", int catchUpHours = 20)
    {
        repository ??= Substitute.For<IProcessingRepository>();
        return new DailyScheduleService(Options.Monitor(new ScheduleOptions { Enabled = enabled, Time = time, CatchUpWindowHours = catchUpHours }), repository);
    }

    [Fact]
    public void Next_occurrence_is_19_00_eastern_regardless_of_offset()
    {
        DailyScheduleService service = Create();
        // Summer (EDT, UTC-4): 19:00 ET == 23:00 UTC.
        ScheduledOccurrence summer = service.NextOccurrence(Instant.FromUtc(2026, 7, 1, 12, 0));
        summer.BusinessDate.Should().Be(new LocalDate(2026, 7, 1));
        summer.Instant.Should().Be(Instant.FromUtc(2026, 7, 1, 23, 0));
        summer.BusinessTime.Offset.Should().Be(Offset.FromHours(-4));

        // Winter (EST, UTC-5): 19:00 ET == 00:00 UTC next day.
        ScheduledOccurrence winter = service.NextOccurrence(Instant.FromUtc(2026, 1, 15, 12, 0));
        winter.BusinessDate.Should().Be(new LocalDate(2026, 1, 15));
        winter.Instant.Should().Be(Instant.FromUtc(2026, 1, 16, 0, 0));
        winter.BusinessTime.Offset.Should().Be(Offset.FromHours(-5));
    }

    [Fact]
    public void Next_rolls_to_tomorrow_when_today_has_passed()
    {
        DailyScheduleService service = Create();
        ScheduledOccurrence next = service.NextOccurrence(Instant.FromUtc(2026, 7, 1, 23, 0)); // exactly 19:00 ET
        next.BusinessDate.Should().Be(new LocalDate(2026, 7, 2));
        service.PreviousOccurrence(Instant.FromUtc(2026, 7, 1, 23, 0)).BusinessDate.Should().Be(new LocalDate(2026, 7, 1));
        service.PreviousOccurrence(Instant.FromUtc(2026, 7, 1, 22, 59)).BusinessDate.Should().Be(new LocalDate(2026, 6, 30));
    }

    [Fact]
    public void Spring_forward_day_keeps_19_00_and_is_23_hours_after_previous()
    {
        // 2026-03-08: clocks jump 02:00 -> 03:00 EST->EDT.
        DailyScheduleService service = Create();
        ScheduledOccurrence before = service.NextOccurrence(Instant.FromUtc(2026, 3, 7, 12, 0));
        ScheduledOccurrence after = service.NextOccurrence(before.Instant);
        before.BusinessTime.LocalDateTime.Should().Be(new LocalDateTime(2026, 3, 7, 19, 0));
        after.BusinessTime.LocalDateTime.Should().Be(new LocalDateTime(2026, 3, 8, 19, 0));
        (after.Instant - before.Instant).Should().Be(Duration.FromHours(23));
        after.BusinessTime.Offset.Should().Be(Offset.FromHours(-4));
    }

    [Fact]
    public void Fall_back_day_keeps_19_00_and_is_25_hours_after_previous()
    {
        // 2026-11-01: clocks fall back 02:00 -> 01:00 EDT->EST.
        DailyScheduleService service = Create();
        ScheduledOccurrence before = service.NextOccurrence(Instant.FromUtc(2026, 10, 31, 12, 0));
        ScheduledOccurrence after = service.NextOccurrence(before.Instant);
        (after.Instant - before.Instant).Should().Be(Duration.FromHours(25));
        after.BusinessTime.Offset.Should().Be(Offset.FromHours(-5));
    }

    [Fact]
    public void Skipped_and_ambiguous_local_times_resolve_deterministically()
    {
        DailyScheduleService skipped = Create(time: "02:30");
        ScheduledOccurrence s = skipped.NextOccurrence(Instant.FromUtc(2026, 3, 8, 5, 0)); // 00:00 EST on transition day
        s.BusinessDate.Should().Be(new LocalDate(2026, 3, 8));
        s.BusinessTime.LocalDateTime.Should().Be(new LocalDateTime(2026, 3, 8, 3, 30), "lenient resolver shifts forward");

        DailyScheduleService ambiguous = Create(time: "01:30");
        ScheduledOccurrence a = ambiguous.NextOccurrence(Instant.FromUtc(2026, 11, 1, 4, 0)); // 00:00 EDT
        a.BusinessTime.LocalDateTime.Should().Be(new LocalDateTime(2026, 11, 1, 1, 30));
        a.BusinessTime.Offset.Should().Be(Offset.FromHours(-4), "earlier offset chosen");
        ambiguous.NextOccurrence(a.Instant).BusinessDate.Should().Be(new LocalDate(2026, 11, 2), "the second 01:30 must not fire again");
    }

    [Fact]
    public async Task Evaluate_due_catch_up_ledger_and_window()
    {
        var repository = Substitute.For<IProcessingRepository>();
        repository.GetScheduledRunAsync(Arg.Any<string>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns((ScheduledRunEntry?)null);
        DailyScheduleService service = Create(repository);
        Instant scheduled = Instant.FromUtc(2026, 7, 1, 23, 0);

        (await service.EvaluateAsync(scheduled + Duration.FromMinutes(1), CancellationToken.None)).Should().Match<DueRunDecision>(d => d.IsDue && !d.IsCatchUp);
        (await service.EvaluateAsync(scheduled + Duration.FromHours(3), CancellationToken.None)).Should().Match<DueRunDecision>(d => d.IsDue && d.IsCatchUp);
        (await service.EvaluateAsync(scheduled + Duration.FromHours(21), CancellationToken.None)).IsDue.Should().BeFalse("outside catch-up window");
        (await service.EvaluateAsync(scheduled - Duration.FromMinutes(1), CancellationToken.None)).Should().Match<DueRunDecision>(d => d.IsDue && d.IsCatchUp && d.Occurrence!.BusinessDate == new LocalDate(2026, 6, 30));

        repository.GetScheduledRunAsync(Arg.Any<string>(), new DateOnly(2026, 7, 1), Arg.Any<CancellationToken>())
            .Returns(new ScheduledRunEntry("s", new DateOnly(2026, 7, 1), scheduled.ToDateTimeOffset(), Guid.NewGuid(), ScheduledRunOutcomes.Completed, null));
        (await service.EvaluateAsync(scheduled + Duration.FromMinutes(5), CancellationToken.None)).IsDue.Should().BeFalse("already recorded");

        repository.GetScheduledRunAsync(Arg.Any<string>(), new DateOnly(2026, 7, 1), Arg.Any<CancellationToken>())
            .Returns(new ScheduledRunEntry("s", new DateOnly(2026, 7, 1), scheduled.ToDateTimeOffset(), null, ScheduledRunOutcomes.NoInput, null));
        (await service.EvaluateAsync(scheduled + Duration.FromMinutes(5), CancellationToken.None)).IsDue.Should().BeTrue("no-input attempts may be retried");

        (await Create(repository, enabled: false).EvaluateAsync(scheduled, CancellationToken.None)).IsDue.Should().BeFalse();
    }

    [Fact]
    public void Business_date_helpers_round_trip()
    {
        BusinessCalendar.ToDateOnly(new LocalDate(2026, 2, 28)).Should().Be(new DateOnly(2026, 2, 28));
        BusinessCalendar.ToLocalDate(new DateOnly(2026, 2, 28)).Should().Be(new LocalDate(2026, 2, 28));
        new ScheduleOptions { Time = "bad" }.ParseTime().Should().Be(new LocalTime(19, 0));
        new ScheduleOptions { Time = "07:15" }.ParseTime().Should().Be(new LocalTime(7, 15));
    }
}
