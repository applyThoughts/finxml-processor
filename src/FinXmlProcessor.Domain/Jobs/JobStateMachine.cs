namespace FinXmlProcessor.Domain.Jobs;

/// <summary>
/// The only place that knows which job transitions are legal:
/// <code>
/// Discovered -> Ready -> Validating -> Processing -> GeneratingOutput
/// GeneratingOutput -> CompletedWithWarnings | Completed
/// Completed | CompletedWithWarnings -> Delivering -> Delivered
/// Any active state -> Failed | Cancelled | Quarantined
/// </code>
/// </summary>
public static class JobStateMachine
{
    private static readonly Dictionary<JobStatus, JobStatus[]> Forward = new()
    {
        [JobStatus.Discovered] = [JobStatus.Ready],
        [JobStatus.Ready] = [JobStatus.Validating],
        [JobStatus.Validating] = [JobStatus.Processing],
        [JobStatus.Processing] = [JobStatus.GeneratingOutput],
        [JobStatus.GeneratingOutput] = [JobStatus.Completed, JobStatus.CompletedWithWarnings],
        [JobStatus.Completed] = [JobStatus.Delivering],
        [JobStatus.CompletedWithWarnings] = [JobStatus.Delivering],
        [JobStatus.Delivering] = [JobStatus.Delivered],
        [JobStatus.Delivered] = [],
        [JobStatus.Failed] = [],
        [JobStatus.Cancelled] = [],
        [JobStatus.Quarantined] = [],
    };

    public static bool CanTransition(JobStatus from, JobStatus to)
    {
        if (from == to)
        {
            return false;
        }

        if (to is JobStatus.Failed or JobStatus.Cancelled or JobStatus.Quarantined)
        {
            return from.IsActive();
        }

        return Forward.TryGetValue(from, out JobStatus[]? targets) && Array.IndexOf(targets, to) >= 0;
    }

    public static void EnsureCanTransition(JobStatus from, JobStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidJobTransitionException(from, to);
        }
    }
}
