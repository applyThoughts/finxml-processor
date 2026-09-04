using FinXmlProcessor.Domain.Sources;

namespace FinXmlProcessor.Application.Abstractions;

/// <summary>
/// Streams records from a source. Implementations must never buffer the whole document; each yielded
/// envelope is valid only until the next iteration.
/// </summary>
public interface IRecordReader : IAsyncDisposable
{
    /// <summary>Total bytes of the underlying source, or null when unknown.</summary>
    long? TotalBytes { get; }

    /// <summary>Bytes consumed so far. Safe to read from another thread for progress reporting.</summary>
    long BytesRead { get; }

    IAsyncEnumerable<SourceRecordEnvelope> ReadRecordsAsync(CancellationToken cancellationToken);
}

/// <summary>Creates readers for a given source path and compiled profile.</summary>
public interface IRecordReaderFactory
{
    IRecordReader Create(string sourcePath, Profiles.CompiledProfile profile);
}
