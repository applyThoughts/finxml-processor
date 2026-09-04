using FinXmlProcessor.Application.Reports;
using FinXmlProcessor.Domain.Jobs;

namespace FinXmlProcessor.Application.Processing;

/// <summary>What the caller wants processed. Profile resolution happens inside the pipeline.</summary>
public sealed record ProcessingRequest(
    string InputPath,
    string? ProfileId = null,
    string? OutputDirectory = null,
    bool Force = false,
    string Trigger = "manual",
    Guid? RerunOfJobId = null,
    DateOnly? BusinessDate = null,
    string SourceProvider = "local",
    string? RemoteReference = null);

public enum ProcessingOutcome
{
    Completed,
    CompletedWithWarnings,
    Failed,
    Cancelled,
    DuplicateBlocked,
    Quarantined,
    LockUnavailable,
    ConfigurationInvalid,
}

public sealed record ProcessingResult(ProcessingOutcome Outcome, ProcessingJob? Job, ProcessingReport? Report, string? OutputPath, string? ReportPath, string SanitizedMessage)
{
    public bool IsSuccess => Outcome is ProcessingOutcome.Completed or ProcessingOutcome.CompletedWithWarnings;
}

/// <summary>Throttled progress snapshot. Emitted at most every <see cref="ProcessingOptions.ProgressIntervalMilliseconds"/>.</summary>
public sealed record ProcessingProgress(
    JobStatus Phase,
    long BytesRead,
    long? TotalBytes,
    long RecordsSeen,
    long RecordsAccepted,
    long RecordsRejected,
    long RecordDuplicates,
    long RowsWritten,
    TimeSpan Elapsed)
{
    public double? PercentComplete => TotalBytes is > 0 ? Math.Min(100d, BytesRead * 100d / TotalBytes.Value) : null;
}
