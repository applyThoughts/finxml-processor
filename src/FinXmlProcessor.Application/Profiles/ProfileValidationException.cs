namespace FinXmlProcessor.Application.Profiles;

public sealed class ProfileValidationException : Exception
{
    public ProfileValidationException(string message)
        : base(message)
    {
        Errors = [message];
    }

    public ProfileValidationException(IReadOnlyList<string> errors)
        : base(errors.Count == 1 ? errors[0] : $"Profile has {errors.Count} validation errors: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }

    public ProfileValidationException()
    {
        Errors = [];
    }

    public ProfileValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [message];
    }

    public IReadOnlyList<string> Errors { get; }
}
