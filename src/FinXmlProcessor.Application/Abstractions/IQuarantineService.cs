namespace FinXmlProcessor.Application.Abstractions;

/// <summary>
/// A quarantined input. <see cref="QuarantinedPath"/> is inside the application quarantine folder when the file
/// was app-managed (staging/download); for user-selected external files the original is never moved and
/// <see cref="OriginalPath"/> is only recorded.
/// </summary>
public sealed record QuarantineEntry(
    Guid Id,
    Guid? JobId,
    string OriginalPath,
    string? QuarantinedPath,
    string ReasonCode,
    string SanitizedReason,
    DateTimeOffset QuarantinedAt,
    string Status);

public interface IQuarantineRepository
{
    Task SaveQuarantineEntryAsync(QuarantineEntry entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantineEntry>> ListQuarantineAsync(CancellationToken cancellationToken);

    Task<QuarantineEntry?> GetQuarantineEntryAsync(Guid id, CancellationToken cancellationToken);
}

public interface IQuarantineService
{
    Task<QuarantineEntry> QuarantineAsync(Guid? jobId, string sourcePath, string reasonCode, string sanitizedReason, bool moveFile, CancellationToken cancellationToken);

    Task<IReadOnlyList<QuarantineEntry>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Moves a quarantined copy back to the configured input folder for reprocessing.</summary>
    Task<QuarantineEntry> RestoreAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Deletes the quarantined copy (never an external original). Requires explicit user confirmation upstream.</summary>
    Task<QuarantineEntry> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
