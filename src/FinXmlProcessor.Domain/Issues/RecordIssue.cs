namespace FinXmlProcessor.Domain.Issues;

/// <summary>A sanitized, machine-readable issue. <see cref="Message"/> must never contain raw sensitive values.</summary>
public sealed record RecordIssue(
    string Code,
    IssueSeverity Severity,
    string? FieldId,
    string Message,
    long? SourceOrdinal)
{
    public static RecordIssue Rejection(string code, string? fieldId, string message, long sourceOrdinal) =>
        new(code, IssueSeverity.RecordRejected, fieldId, message, sourceOrdinal);

    public static RecordIssue Warning(string code, string? fieldId, string message, long? sourceOrdinal = null) =>
        new(code, IssueSeverity.Warning, fieldId, message, sourceOrdinal);

    public static RecordIssue Fatal(string code, string message) => new(code, IssueSeverity.Fatal, null, message, null);

    public static RecordIssue Info(string code, string message, long? sourceOrdinal = null) =>
        new(code, IssueSeverity.Information, null, message, sourceOrdinal);
}
