namespace FinXmlProcessor.Application.Abstractions;

/// <summary>A candidate input that has been located (and, for remote providers, downloaded to local staging).</summary>
public sealed record AcquiredInput(string LocalPath, string OriginalName, long SizeBytes, string Sha256, string Provider, string? RemoteReference);

/// <summary>Result of acquisition. Non-fatal "nothing to do" is expressed as an empty list, not an error.</summary>
public sealed record AcquisitionResult(IReadOnlyList<AcquiredInput> Inputs, IReadOnlyList<string> Diagnostics);

/// <summary>Locates input files. Local folder now; SFTP when configured.</summary>
public interface IInputAcquirer
{
    string ProviderId { get; }

    bool IsConfigured { get; }

    Task<AcquisitionResult> AcquireAsync(CancellationToken cancellationToken);

    /// <summary>Sanitized connectivity test (for SFTP) or folder accessibility check (for local).</summary>
    Task<IReadOnlyList<string>> TestAsync(CancellationToken cancellationToken);
}
