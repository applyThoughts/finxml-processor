using FinXmlProcessor.Domain.Issues;

namespace FinXmlProcessor.Application.Abstractions;

public sealed record InputValidationResult(bool IsValid, IReadOnlyList<RecordIssue> Issues, long SizeBytes, string? Sha256)
{
    public static InputValidationResult Fatal(RecordIssue issue, long sizeBytes = 0) => new(false, [issue], sizeBytes, null);
}

/// <summary>
/// File-level checks (existence, extension, stability, size, format sniffing) and SHA-256 computation.
/// Well-formedness is enforced during the single streaming pass so a 200 MB file is not read twice.
/// </summary>
public interface IInputValidator
{
    Task<InputValidationResult> ValidateFileAsync(string path, CancellationToken cancellationToken);
}
