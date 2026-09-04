using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Application.Scheduling;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace FinXmlProcessor.Infrastructure.Scheduling;

public sealed record ScheduledRunResult(bool Ran, string Message, ProcessingResult? Processing, ScheduledOccurrence? Occurrence);

/// <summary>
/// The idempotent "run-due" operation invoked by the LaunchAgent (and by Run Now). Claims the business date in
/// the ledger before doing any work so two invocations can never process the same date twice.
/// </summary>
public sealed class ScheduledRunCoordinator
{
    private readonly IScheduleService _schedule;
    private readonly IProcessingRepository _repository;
    private readonly IReadOnlyList<IInputAcquirer> _acquirers;
    private readonly ProcessingPipeline _pipeline;
    private readonly IProcessingClock _clock;
    private readonly ILogger<ScheduledRunCoordinator> _logger;

    public ScheduledRunCoordinator(IScheduleService schedule, IProcessingRepository repository, IEnumerable<IInputAcquirer> acquirers, ProcessingPipeline pipeline, IProcessingClock clock, ILogger<ScheduledRunCoordinator> logger)
    {
        _schedule = schedule;
        _repository = repository;
        _acquirers = acquirers.ToList();
        _pipeline = pipeline;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Returns the acquirer that will be used: SFTP when configured, otherwise the local input folder.</summary>
    public IInputAcquirer SelectAcquirer() =>
        _acquirers.FirstOrDefault(a => a.ProviderId == "sftp" && a.IsConfigured) ?? _acquirers.First(a => a.ProviderId == "local");

    public async Task<ScheduledRunResult> RunDueAsync(IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
    {
        Instant now = _clock.GetCurrentInstant();
        DueRunDecision decision = await _schedule.EvaluateAsync(now, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Schedule evaluation: due={IsDue} catchUp={IsCatchUp} {Reason}", decision.IsDue, decision.IsCatchUp, decision.Reason);
        if (!decision.IsDue || decision.Occurrence is null)
        {
            return new ScheduledRunResult(false, decision.Reason, null, decision.Occurrence);
        }

        DateOnly businessDate = BusinessCalendar.ToDateOnly(decision.Occurrence.BusinessDate);
        var claim = new ScheduledRunEntry(_schedule.ScheduleId, businessDate, _clock.UtcNowOffset, null, ScheduledRunOutcomes.Claimed, decision.IsCatchUp ? "catch-up" : "on-time");
        bool claimed = await _repository.TryRecordScheduledRunAsync(claim, cancellationToken).ConfigureAwait(false);
        if (!claimed)
        {
            // A previous "no-input" attempt exists; take it over.
            ScheduledRunEntry? existing = await _repository.GetScheduledRunAsync(_schedule.ScheduleId, businessDate, cancellationToken).ConfigureAwait(false);
            if (existing is null || !string.Equals(existing.Outcome, ScheduledRunOutcomes.NoInput, StringComparison.Ordinal))
            {
                return new ScheduledRunResult(false, "Another worker already claimed this business date.", null, decision.Occurrence);
            }

            await _repository.UpdateScheduledRunAsync(claim, cancellationToken).ConfigureAwait(false);
        }

        return await RunForDateAsync(businessDate, "scheduled", progress, decision.Occurrence, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Run Now: acquires and processes the newest unprocessed input regardless of the schedule, without touching the ledger.</summary>
    public Task<ScheduledRunResult> RunNowAsync(IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken) =>
        RunForDateAsync(BusinessCalendar.BusinessDateFor(_clock.GetCurrentInstant()), "manual", progress, null, cancellationToken);

    private async Task<ScheduledRunResult> RunForDateAsync(DateOnly businessDate, string trigger, IProgress<ProcessingProgress>? progress, ScheduledOccurrence? occurrence, CancellationToken cancellationToken)
    {
        IInputAcquirer acquirer = SelectAcquirer();
        AcquisitionResult acquisition;
        try
        {
            acquisition = await acquirer.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Acquisition via {Provider} failed", acquirer.ProviderId);
            if (occurrence is not null)
            {
                await _repository.UpdateScheduledRunAsync(new ScheduledRunEntry(_schedule.ScheduleId, businessDate, _clock.UtcNowOffset, null, ScheduledRunOutcomes.Failed, $"acquisition failed: {ex.GetType().Name}"), CancellationToken.None).ConfigureAwait(false);
            }

            return new ScheduledRunResult(false, $"Acquisition failed ({ex.GetType().Name}); see the log.", null, occurrence);
        }

        foreach (string line in acquisition.Diagnostics)
        {
            _logger.LogInformation("Acquisition ({Provider}): {Line}", acquirer.ProviderId, line);
        }

        if (acquisition.Inputs.Count == 0)
        {
            if (occurrence is not null)
            {
                await _repository.UpdateScheduledRunAsync(new ScheduledRunEntry(_schedule.ScheduleId, businessDate, _clock.UtcNowOffset, null, ScheduledRunOutcomes.NoInput, string.Join(" ", acquisition.Diagnostics.TakeLast(2))), CancellationToken.None).ConfigureAwait(false);
            }

            return new ScheduledRunResult(false, "No unprocessed input file was found. " + string.Join(" ", acquisition.Diagnostics.TakeLast(2)), null, occurrence);
        }

        AcquiredInput input = acquisition.Inputs[0];
        var request = new ProcessingRequest(input.LocalPath, Trigger: trigger, BusinessDate: businessDate, SourceProvider: input.Provider, RemoteReference: input.RemoteReference);
        ProcessingResult result = await _pipeline.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
        if (occurrence is not null)
        {
            string outcome = result.Outcome switch
            {
                ProcessingOutcome.Completed => ScheduledRunOutcomes.Completed,
                ProcessingOutcome.CompletedWithWarnings => ScheduledRunOutcomes.CompletedWithWarnings,
                _ => ScheduledRunOutcomes.Failed,
            };
            await _repository.UpdateScheduledRunAsync(new ScheduledRunEntry(_schedule.ScheduleId, businessDate, _clock.UtcNowOffset, result.Job?.Id, outcome, result.SanitizedMessage), CancellationToken.None).ConfigureAwait(false);
        }

        return new ScheduledRunResult(true, result.SanitizedMessage, result, occurrence);
    }
}
