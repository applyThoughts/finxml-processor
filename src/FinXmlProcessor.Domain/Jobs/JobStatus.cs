namespace FinXmlProcessor.Domain.Jobs;

public enum JobStatus
{
    Discovered = 0,
    Ready = 1,
    Validating = 2,
    Processing = 3,
    GeneratingOutput = 4,
    Completed = 5,
    CompletedWithWarnings = 6,
    Delivering = 7,
    Delivered = 8,
    Failed = 9,
    Cancelled = 10,
    Quarantined = 11,
}

public static class JobStatusExtensions
{
    public static bool IsTerminal(this JobStatus status) =>
        status is JobStatus.Delivered or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Quarantined;

    public static bool IsActive(this JobStatus status) =>
        status is JobStatus.Discovered or JobStatus.Ready or JobStatus.Validating or JobStatus.Processing
            or JobStatus.GeneratingOutput or JobStatus.Completed or JobStatus.CompletedWithWarnings or JobStatus.Delivering;

    public static bool IsSuccessful(this JobStatus status) =>
        status is JobStatus.Completed or JobStatus.CompletedWithWarnings or JobStatus.Delivering or JobStatus.Delivered;
}
