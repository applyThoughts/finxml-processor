using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FinXmlProcessor.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Infrastructure.Locking;

/// <summary>
/// In-process semaphore plus an exclusively opened lock file. The OS releases the exclusive handle when a
/// process dies, so an abandoned lock never blocks the next run; the sidecar holder file is informational only.
/// </summary>
public sealed class FileProcessingLock : IProcessingLock
{
    private readonly string _lockPath;
    private readonly string _holderPath;
    private readonly SemaphoreSlim _inProcess = new(1, 1);
    private readonly ILogger<FileProcessingLock> _logger;

    public FileProcessingLock(IAppPaths paths, ILogger<FileProcessingLock> logger)
    {
        _lockPath = Path.Combine(paths.Root, "processing.lock");
        _holderPath = Path.Combine(paths.Root, "processing.lock.holder.json");
        _logger = logger;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string holderDescription, CancellationToken cancellationToken)
    {
        if (!await _inProcess.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        FileStream? handle = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
            handle = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            var holder = new HolderInfo(Environment.ProcessId, Environment.MachineName, holderDescription, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(_holderPath, JsonSerializer.Serialize(holder), cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Processing lock acquired by {Holder}", holderDescription);
            return new Lease(this, handle);
        }
        catch (IOException)
        {
            handle?.Dispose();
            _inProcess.Release();
            _logger.LogInformation("Processing lock is held by another process");
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            handle?.Dispose();
            _inProcess.Release();
            return null;
        }
        catch
        {
            handle?.Dispose();
            _inProcess.Release();
            throw;
        }
    }

    public async Task<string?> DescribeHolderAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_holderPath))
            {
                return null;
            }

            HolderInfo? holder = JsonSerializer.Deserialize<HolderInfo>(await File.ReadAllTextAsync(_holderPath, cancellationToken).ConfigureAwait(false));
            if (holder is null)
            {
                return null;
            }

            bool alive = IsProcessAlive(holder.ProcessId);
            return $"pid {holder.ProcessId}{(alive ? string.Empty : " (not running)")} on {holder.Machine}: {holder.Description}, since {holder.Since.ToString("u", CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void Release(FileStream handle)
    {
        handle.Dispose();
        try
        {
            File.Delete(_holderPath);
        }
        catch (IOException)
        {
        }

        _inProcess.Release();
    }

    private sealed record HolderInfo(int ProcessId, string Machine, string Description, DateTimeOffset Since);

    private sealed class Lease : IAsyncDisposable
    {
        private readonly FileProcessingLock _owner;
        private FileStream? _handle;

        public Lease(FileProcessingLock owner, FileStream handle)
        {
            _owner = owner;
            _handle = handle;
        }

        public ValueTask DisposeAsync()
        {
            FileStream? handle = Interlocked.Exchange(ref _handle, null);
            if (handle is not null)
            {
                _owner.Release(handle);
            }

            return ValueTask.CompletedTask;
        }
    }
}
