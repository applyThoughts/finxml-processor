using FinXmlProcessor.Domain.Jobs;

namespace FinXmlProcessor.Application.Abstractions;

public sealed record DeliveryResult(bool Succeeded, string? DeliveredPath, string? DeliveredSha256, string? SanitizedError)
{
    public static DeliveryResult Success(string path, string sha256) => new(true, path, sha256, null);

    public static DeliveryResult Failure(string sanitizedError) => new(false, null, null, sanitizedError);
}

/// <summary>
/// Delivers a completed workbook to a destination. The initial implementation is a local folder; an
/// internal-system adapter implements the same contract and is selected by configuration.
/// </summary>
public interface IOutputDelivery
{
    /// <summary>Stable provider identifier used in configuration and history, e.g. "local-folder".</summary>
    string ProviderId { get; }

    /// <summary>True when the provider has everything it needs to deliver (folders configured, credentials present).</summary>
    bool IsConfigured { get; }

    Task<DeliveryResult> DeliverAsync(ProcessingJob job, string artifactPath, CancellationToken cancellationToken);
}
