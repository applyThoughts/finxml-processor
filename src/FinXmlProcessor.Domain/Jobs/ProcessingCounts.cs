namespace FinXmlProcessor.Domain.Jobs;

/// <summary>Counts are the only per-record state retained after processing.</summary>
public sealed record ProcessingCounts(
    long RecordsSeen,
    long RecordsAccepted,
    long RecordsRejected,
    long RecordDuplicates,
    long RowsWritten,
    long WarningCount)
{
    public static ProcessingCounts Empty { get; } = new(0, 0, 0, 0, 0, 0);
}
