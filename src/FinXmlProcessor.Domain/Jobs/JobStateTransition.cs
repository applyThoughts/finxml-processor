namespace FinXmlProcessor.Domain.Jobs;

public sealed record JobStateTransition(JobStatus From, JobStatus To, DateTimeOffset At, string? Reason);
