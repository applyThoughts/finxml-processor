namespace FinXmlProcessor.Application.Abstractions;

/// <summary>Describes a previously processed file that matches by content hash.</summary>
public sealed record FileDuplicateMatch(Guid JobId, string SourceFileName, DateTimeOffset ProcessedAt, string Status);

/// <summary>File-level duplicate detection backed by processing history.</summary>
public interface IFileDuplicateDetector
{
    Task<FileDuplicateMatch?> FindBySha256Async(string sha256, CancellationToken cancellationToken);
}

/// <summary>
/// Record-level duplicate detection within one job. Implementations must not hold all keys in process memory;
/// a spillable or database-backed set is expected so memory stays bounded for very large inputs.
/// </summary>
public interface IRecordDuplicateSet : IAsyncDisposable
{
    /// <summary>Returns true if the key was already seen in this job; otherwise remembers it and returns false.</summary>
    ValueTask<bool> IsDuplicateAsync(string compositeKey, CancellationToken cancellationToken);
}

public interface IRecordDuplicateSetFactory
{
    Task<IRecordDuplicateSet> CreateAsync(Guid jobId, CancellationToken cancellationToken);
}
