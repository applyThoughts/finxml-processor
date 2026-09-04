namespace FinXmlProcessor.Application.Processing;

/// <summary>
/// A fatal, already-sanitized processing failure raised by readers, writers or validators. The message is safe
/// to persist and display. <see cref="Quarantine"/> indicates the input itself is unusable (bad format, malformed XML).
/// </summary>
public sealed class ProcessingFatalException : Exception
{
    public ProcessingFatalException(string code, string sanitizedMessage, bool quarantine = false, Exception? innerException = null)
        : base(sanitizedMessage, innerException)
    {
        Code = code;
        Quarantine = quarantine;
    }

    public ProcessingFatalException()
    {
        Code = Domain.Issues.IssueCodes.JobUnexpectedError;
    }

    public ProcessingFatalException(string message)
        : base(message)
    {
        Code = Domain.Issues.IssueCodes.JobUnexpectedError;
    }

    public ProcessingFatalException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = Domain.Issues.IssueCodes.JobUnexpectedError;
    }

    public string Code { get; }

    public bool Quarantine { get; }
}
