using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Tables;

namespace FinXmlProcessor.Application.Abstractions;

/// <summary>Key/value pairs rendered on the Summary sheet. Values must already be sanitized.</summary>
public sealed record SummaryEntry(string Label, string Value);

/// <summary>A rejected record line for the optional "Rejected Records" sheet. Field values are pre-masked by the caller.</summary>
public sealed record RejectedRecordLine(long SourceOrdinal, string? SafeIdentifier, string Codes, string Messages, IReadOnlyList<KeyValuePair<string, string>> SafeFields);

/// <summary>Options that affect workbook layout. <see cref="MaxRowsPerSheet"/> is injectable so sheet splitting can be tested cheaply.</summary>
public sealed record WorkbookWriterOptions(int MaxRowsPerSheet = 1_048_576, int MaxCellTextLength = 32_767, bool IncludeRejectedSheet = true)
{
    public static WorkbookWriterOptions Default { get; } = new();
}

/// <summary>
/// A forward-only workbook session. Rows are written as they arrive and never retained. The final file is
/// only materialized by <see cref="CompleteAsync"/>; disposing without completing discards the staging file.
/// </summary>
public interface IWorkbookSession : IAsyncDisposable
{
    long RowsWritten { get; }

    void WriteRow(OutputRow row);

    void WriteRejected(RejectedRecordLine line);

    /// <summary>Writes the summary sheet, closes the package, validates it and atomically moves it to the final path.</summary>
    Task<string> CompleteAsync(IReadOnlyList<SummaryEntry> summary, IReadOnlyList<RecordIssue> jobIssues, CancellationToken cancellationToken);
}

public interface IWorkbookWriter
{
    Task<IWorkbookSession> BeginAsync(string finalPath, IReadOnlyList<OutputTableDefinition> tables, WorkbookWriterOptions options, CancellationToken cancellationToken);

    /// <summary>Opens the package read-only and verifies structural validity. Used after writing and by tests.</summary>
    Task<IReadOnlyList<RecordIssue>> VerifyAsync(string path, CancellationToken cancellationToken);
}
