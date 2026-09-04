using System.Security.Cryptography;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Domain.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Infrastructure.Delivery;

public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";

    /// <summary>"none" disables delivery; "local-folder" copies the workbook to <see cref="LocalFolder"/>.</summary>
    public string Provider { get; set; } = "none";

    public string? LocalFolder { get; set; }

    /// <summary>"version" (default) appends a counter on collision; "fail" reports an error; "overwrite" replaces. Never silent.</summary>
    public string CollisionPolicy { get; set; } = "version";
}

/// <summary>Copies the completed workbook to a local (or mounted network) folder through a temp file and atomic rename.</summary>
public sealed class LocalFolderDelivery : IOutputDelivery
{
    public const string Id = "local-folder";
    private readonly IOptionsMonitor<DeliveryOptions> _options;
    private readonly ILogger<LocalFolderDelivery> _logger;

    public LocalFolderDelivery(IOptionsMonitor<DeliveryOptions> options, ILogger<LocalFolderDelivery> logger)
    {
        _options = options;
        _logger = logger;
    }

    public string ProviderId => Id;

    public bool IsConfigured => string.Equals(_options.CurrentValue.Provider, Id, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_options.CurrentValue.LocalFolder);

    public async Task<DeliveryResult> DeliverAsync(ProcessingJob job, string artifactPath, CancellationToken cancellationToken)
    {
        DeliveryOptions options = _options.CurrentValue;
        if (string.IsNullOrWhiteSpace(options.LocalFolder))
        {
            return DeliveryResult.Failure("Delivery folder is not configured.");
        }

        string folder = Path.GetFullPath(options.LocalFolder);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DeliveryResult.Failure($"Delivery folder cannot be created or accessed ({ex.GetType().Name}).");
        }

        string fileName = Path.GetFileName(artifactPath);
        string target = Path.Combine(folder, fileName);
        if (File.Exists(target))
        {
            switch (options.CollisionPolicy.ToUpperInvariant())
            {
                case "FAIL":
                    return DeliveryResult.Failure($"A file named '{fileName}' already exists in the delivery folder and the collision policy is 'fail'.");
                case "OVERWRITE":
                    _logger.LogWarning("Delivery target {File} exists and will be overwritten (policy: overwrite)", fileName);
                    break;
                default:
                    target = NextVersion(folder, fileName);
                    _logger.LogInformation("Delivery target exists; delivering as {File} (policy: version)", Path.GetFileName(target));
                    break;
            }
        }

        string temp = Path.Combine(folder, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream source = new(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream destination = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, target, overwrite: string.Equals(options.CollisionPolicy, "overwrite", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return DeliveryResult.Failure($"Copy to delivery folder failed ({ex.GetType().Name}).");
        }

        string hash;
        await using (FileStream delivered = new(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(delivered, cancellationToken).ConfigureAwait(false));
        }

        if (job.OutputSha256 is not null && !string.Equals(hash, job.OutputSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(target);
            return DeliveryResult.Failure("Delivered file hash does not match the generated workbook; the copy was removed.");
        }

        return DeliveryResult.Success(target, hash);
    }

    private static string NextVersion(string folder, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int n = 2; n < 10_000; n++)
        {
            string candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Too many versions of the delivered file already exist.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Template for the future internal-system adapter. It is intentionally not registered: implement
/// <see cref="TransmitAsync"/>, register the class as <see cref="IOutputDelivery"/>, set Delivery:Provider to its id,
/// and run the shared delivery contract tests against it. See docs/architecture.md.
/// </summary>
public abstract class InternalSystemDeliveryBase : IOutputDelivery
{
    public abstract string ProviderId { get; }

    public abstract bool IsConfigured { get; }

    public async Task<DeliveryResult> DeliverAsync(ProcessingJob job, string artifactPath, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return DeliveryResult.Failure($"Delivery provider '{ProviderId}' is not configured.");
        }

        string hash;
        await using (FileStream stream = new(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        try
        {
            string reference = await TransmitAsync(job, artifactPath, hash, cancellationToken).ConfigureAwait(false);
            return DeliveryResult.Success(reference, hash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DeliveryResult.Failure(Sanitize(ex));
        }
    }

    /// <summary>Sends the artifact and returns a reference (path, id or URL) the internal system uses for it.</summary>
    protected abstract Task<string> TransmitAsync(ProcessingJob job, string artifactPath, string sha256, CancellationToken cancellationToken);

    /// <summary>Convert provider errors into messages that contain no credentials or connection strings.</summary>
    protected virtual string Sanitize(Exception exception) => $"Delivery provider '{ProviderId}' failed with {exception.GetType().Name}.";
}
