using System.Globalization;
using FinXmlProcessor.Application;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Scheduling;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;
using NodaTime.TimeZones;

namespace FinXmlProcessor.Infrastructure.Scheduling;

public sealed class ScheduleOptions
{
    public const string SectionName = "Schedule";

    public bool Enabled { get; set; }

    /// <summary>Local time of day in America/New_York, "HH:mm". Default 19:00.</summary>
    public string Time { get; set; } = "19:00";

    /// <summary>How long after the scheduled instant a missed run may still be caught up. Default 20 hours.</summary>
    public int CatchUpWindowHours { get; set; } = 20;

    /// <summary>Runs starting later than this after the scheduled instant are reported as catch-up runs.</summary>
    public int OnTimeToleranceMinutes { get; set; } = 15;

    /// <summary>How often the LaunchAgent invokes the worker to check for a due run (seconds).</summary>
    public int AgentIntervalSeconds { get; set; } = 300;

    public LocalTime ParseTime()
    {
        ParseResult<LocalTime> parsed = LocalTimePattern.CreateWithInvariantCulture("HH:mm").Parse(Time);
        return parsed.Success ? parsed.Value : BusinessCalendar.DefaultRunTime;
    }
}

/// <summary>
/// Daily schedule at a fixed Eastern local time. DST is handled by NodaTime: 19:00 never falls in a transition
/// gap, but the resolver is lenient (skipped times move forward, ambiguous times take the earlier offset).
/// </summary>
public sealed class DailyScheduleService : IScheduleService
{
    private readonly IOptionsMonitor<ScheduleOptions> _options;
    private readonly IProcessingRepository _repository;

    public DailyScheduleService(IOptionsMonitor<ScheduleOptions> options, IProcessingRepository repository)
    {
        _options = options;
        _repository = repository;
    }

    public string ScheduleId => AppInfo.ScheduleId;

    public DateTimeZone BusinessZone => BusinessCalendar.EasternZone;

    public ScheduledOccurrence NextOccurrence(Instant after)
    {
        LocalTime time = _options.CurrentValue.ParseTime();
        ZonedDateTime local = after.InZone(BusinessZone);
        LocalDate date = local.Date;
        ScheduledOccurrence candidate = OccurrenceOn(date, time);
        if (candidate.Instant <= after)
        {
            candidate = OccurrenceOn(date.PlusDays(1), time);
        }

        return candidate;
    }

    public ScheduledOccurrence PreviousOccurrence(Instant atOrBefore)
    {
        LocalTime time = _options.CurrentValue.ParseTime();
        LocalDate date = atOrBefore.InZone(BusinessZone).Date;
        ScheduledOccurrence candidate = OccurrenceOn(date, time);
        if (candidate.Instant > atOrBefore)
        {
            candidate = OccurrenceOn(date.PlusDays(-1), time);
        }

        return candidate;
    }

    public async Task<DueRunDecision> EvaluateAsync(Instant now, CancellationToken cancellationToken)
    {
        ScheduleOptions options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return new DueRunDecision(false, null, false, "Scheduled processing is disabled.");
        }

        ScheduledOccurrence previous = PreviousOccurrence(now);
        Duration sinceScheduled = now - previous.Instant;
        if (sinceScheduled > Duration.FromHours(options.CatchUpWindowHours))
        {
            return new DueRunDecision(false, previous, false, $"Last occurrence ({Describe(previous)}) is outside the {options.CatchUpWindowHours}h catch-up window.");
        }

        ScheduledRunEntry? ledger = await _repository.GetScheduledRunAsync(ScheduleId, BusinessCalendar.ToDateOnly(previous.BusinessDate), cancellationToken).ConfigureAwait(false);
        if (ledger is not null && !string.Equals(ledger.Outcome, ScheduledRunOutcomes.NoInput, StringComparison.Ordinal))
        {
            return new DueRunDecision(false, previous, false, $"Run for {previous.BusinessDate:yyyy-MM-dd} already recorded ({ledger.Outcome}).");
        }

        bool catchUp = sinceScheduled > Duration.FromMinutes(options.OnTimeToleranceMinutes);
        string reason = ledger is null
            ? (catchUp ? $"Missed run for {Describe(previous)} is eligible for catch-up." : $"Run for {Describe(previous)} is due.")
            : $"Previous attempt for {previous.BusinessDate:yyyy-MM-dd} found no input; checking again.";
        return new DueRunDecision(true, previous, catchUp, reason);
    }

    private ScheduledOccurrence OccurrenceOn(LocalDate date, LocalTime time)
    {
        ZonedDateTime zoned = BusinessZone.ResolveLocal(date + time, Resolvers.LenientResolver);
        return new ScheduledOccurrence(date, zoned, zoned.ToInstant());
    }

    private static string Describe(ScheduledOccurrence occurrence) =>
        occurrence.BusinessTime.ToString("yyyy-MM-dd HH:mm o<g>", CultureInfo.InvariantCulture);
}

public static class ScheduledRunOutcomes
{
    public const string Claimed = "claimed";
    public const string NoInput = "no-input";
    public const string Completed = "completed";
    public const string CompletedWithWarnings = "completed-with-warnings";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}
