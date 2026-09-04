using FinXmlProcessor.Domain.Issues;

namespace FinXmlProcessor.Domain.Jobs;

/// <summary>Aggregate root for one processing run of one input file. Transitions are persisted, never derived from logs.</summary>
public sealed class ProcessingJob
{
    private readonly List<JobStateTransition> _transitions = [];
    private readonly List<RecordIssue> _issues = [];

    public ProcessingJob(
        Guid id,
        string sourceFileName,
        string? sourceSha256,
        string profileId,
        string profileVersion,
        string profileHash,
        DateTimeOffset createdAt,
        Guid? rerunOfJobId = null,
        string trigger = "manual")
    {
        Id = id;
        SourceFileName = sourceFileName;
        SourceSha256 = sourceSha256;
        ProfileId = profileId;
        ProfileVersion = profileVersion;
        ProfileHash = profileHash;
        CreatedAt = createdAt;
        RerunOfJobId = rerunOfJobId;
        Trigger = trigger;
        Status = JobStatus.Discovered;
        Counts = ProcessingCounts.Empty;
    }

    public Guid Id { get; }

    public string SourceFileName { get; }

    public string? SourceSha256 { get; private set; }

    public long SourceSizeBytes { get; set; }

    public string ProfileId { get; }

    public string ProfileVersion { get; }

    public string ProfileHash { get; }

    public DateTimeOffset CreatedAt { get; }

    public Guid? RerunOfJobId { get; }

    /// <summary>"manual", "scheduled" or "cli". Informational only.</summary>
    public string Trigger { get; }

    public JobStatus Status { get; private set; }

    public ProcessingCounts Counts { get; set; }

    public string? OutputPath { get; set; }

    public string? OutputSha256 { get; set; }

    public string? ReportPath { get; set; }

    public DateOnly? BusinessDate { get; set; }

    public IReadOnlyList<JobStateTransition> Transitions => _transitions;

    /// <summary>Sanitized issues only. Fatal and warning issues are kept; rejection issues may be capped by policy.</summary>
    public IReadOnlyList<RecordIssue> Issues => _issues;

    public DateTimeOffset? StartedAt => _transitions.FirstOrDefault(t => t.To == JobStatus.Validating)?.At;

    public DateTimeOffset? FinishedAt => _transitions
        .LastOrDefault(t => t.To.IsTerminal() || t.To is JobStatus.Completed or JobStatus.CompletedWithWarnings)?.At;

    public void SetSourceHash(string sha256) => SourceSha256 = sha256;

    public void TransitionTo(JobStatus target, DateTimeOffset at, string? reason = null)
    {
        JobStateMachine.EnsureCanTransition(Status, target);
        _transitions.Add(new JobStateTransition(Status, target, at, reason));
        Status = target;
    }

    public void AddIssue(RecordIssue issue) => _issues.Add(issue);

    /// <summary>Rehydrates a job from persistence without re-validating history (it was validated when written).</summary>
    public static ProcessingJob Rehydrate(
        Guid id,
        string sourceFileName,
        string? sourceSha256,
        long sourceSizeBytes,
        string profileId,
        string profileVersion,
        string profileHash,
        DateTimeOffset createdAt,
        Guid? rerunOfJobId,
        string trigger,
        JobStatus status,
        ProcessingCounts counts,
        string? outputPath,
        string? outputSha256,
        string? reportPath,
        DateOnly? businessDate,
        IEnumerable<JobStateTransition> transitions,
        IEnumerable<RecordIssue> issues)
    {
        var job = new ProcessingJob(id, sourceFileName, sourceSha256, profileId, profileVersion, profileHash, createdAt, rerunOfJobId, trigger)
        {
            SourceSizeBytes = sourceSizeBytes,
            Status = status,
            Counts = counts,
            OutputPath = outputPath,
            OutputSha256 = outputSha256,
            ReportPath = reportPath,
            BusinessDate = businessDate,
        };
        job._transitions.AddRange(transitions);
        job._issues.AddRange(issues);
        return job;
    }
}
