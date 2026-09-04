using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Infrastructure.Paths;
using FinXmlProcessor.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Infrastructure.Retention;

/// <summary>Per-category retention. Everything is disabled (retain forever) until the user enables it.</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public RetentionPolicy Logs { get; set; } = new();

    public RetentionPolicy Reports { get; set; } = new();

    public RetentionPolicy Staging { get; set; } = new() { Enabled = true, MaxAgeDays = 2 };

    public RetentionPolicy Quarantine { get; set; } = new();

    public RetentionPolicy History { get; set; } = new();
}

public sealed class RetentionPolicy
{
    public bool Enabled { get; set; }

    public int MaxAgeDays { get; set; } = 90;
}

public sealed record RetentionOutcome(string Category, int Deleted, int Skipped, IReadOnlyList<string> Notes);

/// <summary>
/// Deletes only files inside application-owned roots matching known name patterns, resolved through
/// <see cref="IAppPaths.ResolveInside"/> so a misconfigured path can never reach outside the root.
/// </summary>
public sealed class RetentionService
{
    private readonly AppPaths _paths;
    private readonly IOptionsMonitor<RetentionOptions> _options;
    private readonly SqliteProcessingRepository _repository;
    private readonly IQuarantineRepository _quarantine;
    private readonly IProcessingClock _clock;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(AppPaths paths, IOptionsMonitor<RetentionOptions> options, SqliteProcessingRepository repository, IQuarantineRepository quarantine, IProcessingClock clock, ILogger<RetentionService> logger)
    {
        _paths = paths;
        _options = options;
        _repository = repository;
        _quarantine = quarantine;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RetentionOutcome>> ApplyAsync(CancellationToken cancellationToken)
    {
        RetentionOptions o = _options.CurrentValue;
        var outcomes = new List<RetentionOutcome>
        {
            ApplyToFolder("logs", o.Logs, _paths.Logs, ["*.log", "*.json"]),
            ApplyToFolder("reports", o.Reports, _paths.Reports, ["report_*.json"]),
            ApplyToFolder("staging", o.Staging, _paths.Staging, ["*.part", "dupkeys-*.sqlite", "*.xml"]),
        };

        outcomes.Add(await ApplyQuarantineAsync(o.Quarantine, cancellationToken).ConfigureAwait(false));
        outcomes.Add(await ApplyHistoryAsync(o.History, cancellationToken).ConfigureAwait(false));
        return outcomes;
    }

    private RetentionOutcome ApplyToFolder(string category, RetentionPolicy policy, string root, string[] patterns)
    {
        if (!policy.Enabled)
        {
            return new RetentionOutcome(category, 0, 0, ["disabled"]);
        }

        if (!Directory.Exists(root))
        {
            return new RetentionOutcome(category, 0, 0, ["folder missing"]);
        }

        DateTimeOffset cutoff = _clock.UtcNowOffset.AddDays(-Math.Max(0, policy.MaxAgeDays));
        int deleted = 0, skipped = 0;
        var notes = new List<string>();
        foreach (string pattern in patterns)
        {
            foreach (FileInfo file in new DirectoryInfo(root).EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
            {
                if (file.LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }

                try
                {
                    string resolved = _paths.ResolveInside(root, file.FullName);
                    File.Delete(resolved);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    notes.Add($"{file.Name}: {ex.GetType().Name}");
                }
            }
        }

        _logger.LogInformation("Retention {Category}: deleted {Deleted}, skipped {Skipped}", category, deleted, skipped);
        return new RetentionOutcome(category, deleted, skipped, notes);
    }

    private async Task<RetentionOutcome> ApplyQuarantineAsync(RetentionPolicy policy, CancellationToken cancellationToken)
    {
        if (!policy.Enabled)
        {
            return new RetentionOutcome("quarantine", 0, 0, ["disabled"]);
        }

        DateTimeOffset cutoff = _clock.UtcNowOffset.AddDays(-Math.Max(0, policy.MaxAgeDays));
        int deleted = 0, skipped = 0;
        var notes = new List<string>();
        foreach (QuarantineEntry entry in await _quarantine.ListQuarantineAsync(cancellationToken).ConfigureAwait(false))
        {
            if (entry.QuarantinedPath is null || entry.QuarantinedAt >= cutoff)
            {
                continue;
            }

            try
            {
                string resolved = _paths.ResolveInside(_paths.Quarantine, entry.QuarantinedPath);
                if (File.Exists(resolved))
                {
                    File.Delete(resolved);
                }

                await _quarantine.SaveQuarantineEntryAsync(entry with { Status = "expired", QuarantinedPath = null }, cancellationToken).ConfigureAwait(false);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                notes.Add($"{entry.Id}: {ex.GetType().Name}");
            }
        }

        return new RetentionOutcome("quarantine", deleted, skipped, notes);
    }

    private async Task<RetentionOutcome> ApplyHistoryAsync(RetentionPolicy policy, CancellationToken cancellationToken)
    {
        if (!policy.Enabled)
        {
            return new RetentionOutcome("history", 0, 0, ["disabled"]);
        }

        DateTimeOffset cutoff = _clock.UtcNowOffset.AddDays(-Math.Max(0, policy.MaxAgeDays));
        int deleted = await _repository.DeleteJobsOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
        return new RetentionOutcome("history", deleted, 0, []);
    }
}
