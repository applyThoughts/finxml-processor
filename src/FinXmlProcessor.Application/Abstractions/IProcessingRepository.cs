using FinXmlProcessor.Domain.Jobs;

namespace FinXmlProcessor.Application.Abstractions;

public sealed record JobQuery(int Limit = 200, JobStatus? Status = null, string? SourceNameContains = null, DateTimeOffset? CreatedAfter = null);

public sealed record DeliveryAttempt(Guid JobId, string Provider, DateTimeOffset AttemptedAt, bool Succeeded, string? DeliveredPath, string? DeliveredSha256, string? SanitizedError);

public sealed record ScheduledRunEntry(string ScheduleId, DateOnly EasternDate, DateTimeOffset RecordedAt, Guid? JobId, string Outcome, string? Note);

/// <summary>Persistence for jobs, transitions, issues, artifacts, delivery attempts and the scheduled-run ledger.</summary>
public interface IProcessingRepository
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task SaveJobAsync(ProcessingJob job, CancellationToken cancellationToken);

    Task<ProcessingJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProcessingJob>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken);

    Task<ProcessingJob?> FindLatestJobBySourceHashAsync(string sha256, CancellationToken cancellationToken);

    Task RecordDeliveryAttemptAsync(DeliveryAttempt attempt, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeliveryAttempt>> GetDeliveryAttemptsAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ScheduledRunEntry?> GetScheduledRunAsync(string scheduleId, DateOnly easternDate, CancellationToken cancellationToken);

    /// <summary>Inserts the ledger entry; returns false without modifying anything if one already exists for that date.</summary>
    Task<bool> TryRecordScheduledRunAsync(ScheduledRunEntry entry, CancellationToken cancellationToken);

    Task UpdateScheduledRunAsync(ScheduledRunEntry entry, CancellationToken cancellationToken);

    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken);

    Task SetSettingAsync(string key, string? value, CancellationToken cancellationToken);
}
