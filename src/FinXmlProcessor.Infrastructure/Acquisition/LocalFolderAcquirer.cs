using System.Security.Cryptography;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Infrastructure.Acquisition;

/// <summary>
/// Picks input files from the configured local input folder: newest stable file first, skipping files whose
/// content hash was already processed successfully.
/// </summary>
public sealed class LocalFolderAcquirer : IInputAcquirer
{
    public const string Id = "local";
    private readonly IAppPaths _paths;
    private readonly IOptionsMonitor<ProcessingOptions> _options;
    private readonly IFileDuplicateDetector _duplicates;
    private readonly ILogger<LocalFolderAcquirer> _logger;

    public LocalFolderAcquirer(IAppPaths paths, IOptionsMonitor<ProcessingOptions> options, IFileDuplicateDetector duplicates, ILogger<LocalFolderAcquirer> logger)
    {
        _paths = paths;
        _options = options;
        _duplicates = duplicates;
        _logger = logger;
    }

    public string ProviderId => Id;

    public bool IsConfigured => true;

    public string InputDirectory => _options.CurrentValue.InputDirectory ?? _paths.DefaultInput;

    public async Task<AcquisitionResult> AcquireAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        string directory = InputDirectory;
        if (!Directory.Exists(directory))
        {
            diagnostics.Add($"Input folder does not exist: {directory}");
            return new AcquisitionResult([], diagnostics);
        }

        var candidates = new DirectoryInfo(directory)
            .EnumerateFiles(_options.CurrentValue.InputPattern, SearchOption.TopDirectoryOnly)
            .Where(f => !f.Name.StartsWith('.') && !f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        diagnostics.Add($"{candidates.Count} file(s) match '{_options.CurrentValue.InputPattern}' in the input folder.");

        var inputs = new List<AcquiredInput>();
        foreach (FileInfo file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string hash;
            try
            {
                await using FileStream stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }
            catch (IOException ex)
            {
                diagnostics.Add($"Skipped {file.Name}: cannot read ({ex.GetType().Name}).");
                continue;
            }

            FileDuplicateMatch? processed = await _duplicates.FindBySha256Async(hash, cancellationToken).ConfigureAwait(false);
            if (processed is not null)
            {
                diagnostics.Add($"Skipped {file.Name}: already processed as job {processed.JobId:D}.");
                continue;
            }

            inputs.Add(new AcquiredInput(file.FullName, file.Name, file.Length, hash, Id, null));
        }

        _logger.LogInformation("Local acquisition found {Count} unprocessed file(s)", inputs.Count);
        return new AcquisitionResult(inputs, diagnostics);
    }

    public Task<IReadOnlyList<string>> TestAsync(CancellationToken cancellationToken)
    {
        string directory = InputDirectory;
        var lines = new List<string> { $"Input folder: {directory}" };
        lines.Add(Directory.Exists(directory) ? "Folder exists and is readable." : "Folder does not exist.");
        return Task.FromResult<IReadOnlyList<string>>(lines);
    }
}
