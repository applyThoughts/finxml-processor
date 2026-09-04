using System.Globalization;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Naming;
using FinXmlProcessor.Application.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Infrastructure.Quarantine;

public sealed class QuarantineService : IQuarantineService
{
    private readonly IAppPaths _paths;
    private readonly IQuarantineRepository _repository;
    private readonly IOptionsMonitor<ProcessingOptions> _options;
    private readonly IProcessingClock _clock;
    private readonly ILogger<QuarantineService> _logger;

    public QuarantineService(IAppPaths paths, IQuarantineRepository repository, IOptionsMonitor<ProcessingOptions> options, IProcessingClock clock, ILogger<QuarantineService> logger)
    {
        _paths = paths;
        _repository = repository;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public async Task<QuarantineEntry> QuarantineAsync(Guid? jobId, string sourcePath, string reasonCode, string sanitizedReason, bool moveFile, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.Quarantine);
        string? quarantinedPath = null;
        if (moveFile && File.Exists(sourcePath))
        {
            string stamp = _clock.UtcNowOffset.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string name = $"{stamp}_{(jobId is Guid j ? OutputNaming.ShortJobId(j) : "nojob")}_{OutputNaming.SafeFileNameFromPath(sourcePath)}";
            quarantinedPath = _paths.ResolveInside(_paths.Quarantine, name);
            File.Move(sourcePath, quarantinedPath, overwrite: false);
            _logger.LogWarning("Moved input to quarantine as {Name} ({Code})", name, reasonCode);
        }
        else
        {
            _logger.LogWarning("Recorded quarantine for external input {Name} without moving it ({Code})", OutputNaming.SafeFileNameFromPath(sourcePath), reasonCode);
        }

        var entry = new QuarantineEntry(Guid.NewGuid(), jobId, sourcePath, quarantinedPath, reasonCode, sanitizedReason, _clock.UtcNowOffset, quarantinedPath is null ? "recorded" : "quarantined");
        await _repository.SaveQuarantineEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public Task<IReadOnlyList<QuarantineEntry>> ListAsync(CancellationToken cancellationToken) => _repository.ListQuarantineAsync(cancellationToken);

    public async Task<QuarantineEntry> RestoreAsync(Guid id, CancellationToken cancellationToken)
    {
        QuarantineEntry entry = await _repository.GetQuarantineEntryAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Quarantine entry {id} not found.");
        if (entry.QuarantinedPath is null || !File.Exists(entry.QuarantinedPath))
        {
            throw new InvalidOperationException("This entry has no quarantined copy to restore (external originals are never moved).");
        }

        string inputDirectory = _options.CurrentValue.InputDirectory ?? _paths.DefaultInput;
        Directory.CreateDirectory(inputDirectory);
        string source = _paths.ResolveInside(_paths.Quarantine, entry.QuarantinedPath);
        string target = Path.Combine(inputDirectory, OutputNaming.SafeFileNameFromPath(entry.OriginalPath));
        if (File.Exists(target))
        {
            target = Path.Combine(inputDirectory, $"{Path.GetFileNameWithoutExtension(target)}_restored-{OutputNaming.ShortJobId(id)}{Path.GetExtension(target)}");
        }

        File.Move(source, target, overwrite: false);
        QuarantineEntry updated = entry with { Status = "restored", QuarantinedPath = null };
        await _repository.SaveQuarantineEntryAsync(updated, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Restored quarantine entry {Id} to the input folder", id);
        return updated;
    }

    public async Task<QuarantineEntry> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        QuarantineEntry entry = await _repository.GetQuarantineEntryAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Quarantine entry {id} not found.");
        if (entry.QuarantinedPath is not null)
        {
            // ResolveInside guarantees we never delete outside the quarantine folder, whatever the row contains.
            string path = _paths.ResolveInside(_paths.Quarantine, entry.QuarantinedPath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        QuarantineEntry updated = entry with { Status = "deleted", QuarantinedPath = null };
        await _repository.SaveQuarantineEntryAsync(updated, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Deleted quarantined copy for entry {Id}", id);
        return updated;
    }
}
