namespace FinXmlProcessor.Domain.Jobs;

public sealed class InvalidJobTransitionException : InvalidOperationException
{
    public InvalidJobTransitionException(JobStatus from, JobStatus to)
        : base($"Job transition {from} -> {to} is not allowed.")
    {
        From = from;
        To = to;
    }

    public InvalidJobTransitionException()
    {
    }

    public InvalidJobTransitionException(string message)
        : base(message)
    {
    }

    public InvalidJobTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public JobStatus From { get; }

    public JobStatus To { get; }
}
