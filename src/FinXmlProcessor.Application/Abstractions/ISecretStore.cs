namespace FinXmlProcessor.Application.Abstractions;

/// <summary>
/// Stores secrets scoped by service and account. Production uses the macOS Keychain; Windows development uses DPAPI.
/// Secrets are never returned as loggable objects, never written to plaintext files and never passed on command lines.
/// </summary>
public interface ISecretStore
{
    /// <summary>Human-readable backing store name for diagnostics, e.g. "macOS Keychain".</summary>
    string ProviderName { get; }

    Task StoreAsync(string service, string account, string secret, CancellationToken cancellationToken);

    Task<string?> RetrieveAsync(string service, string account, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken);
}

/// <summary>Well-known secret identifiers so callers never invent ad-hoc keys.</summary>
public static class SecretNames
{
    public const string Service = "com.example.finxmlprocessor";
    public const string SftpPassword = "sftp.password";
    public const string SftpKeyPassphrase = "sftp.key-passphrase";
}
