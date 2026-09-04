using NodaTime;

namespace FinXmlProcessor.Application.Abstractions;

/// <summary>Describes a scheduled occurrence in the business zone and the equivalent instant.</summary>
public sealed record ScheduledOccurrence(LocalDate BusinessDate, ZonedDateTime BusinessTime, Instant Instant);

/// <summary>Outcome of asking whether a run is due right now.</summary>
public sealed record DueRunDecision(bool IsDue, ScheduledOccurrence? Occurrence, bool IsCatchUp, string Reason);

/// <summary>
/// All schedule arithmetic in the business zone (America/New_York), independent of the host machine zone.
/// Implementations must be deterministic given a clock so DST behaviour can be tested.
/// </summary>
public interface IScheduleService
{
    string ScheduleId { get; }

    DateTimeZone BusinessZone { get; }

    /// <summary>The next occurrence strictly after the given instant, resolving skipped/ambiguous local times deterministically.</summary>
    ScheduledOccurrence NextOccurrence(Instant after);

    /// <summary>The most recent occurrence at or before the given instant.</summary>
    ScheduledOccurrence PreviousOccurrence(Instant atOrBefore);

    /// <summary>Decides whether a run is due, consulting the ledger for the candidate business date.</summary>
    Task<DueRunDecision> EvaluateAsync(Instant now, CancellationToken cancellationToken);
}
