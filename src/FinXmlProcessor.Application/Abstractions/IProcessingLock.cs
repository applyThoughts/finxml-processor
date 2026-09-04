namespace FinXmlProcessor.Application.Abstractions;

/// <summary>
/// Guarantees that only one job runs at a time across the desktop app and the scheduled worker.
/// Implementations combine an in-process semaphore with an interprocess file lock and detect abandoned locks.
/// </summary>
public interface IProcessingLock
{
    /// <summary>Attempts to acquire the lock; returns null if another process holds it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string holderDescription, CancellationToken cancellationToken);

    /// <summary>Describes the current holder if the lock is taken, for diagnostics.</summary>
    Task<string?> DescribeHolderAsync(CancellationToken cancellationToken);
}
