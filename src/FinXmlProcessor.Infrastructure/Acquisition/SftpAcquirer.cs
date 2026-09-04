using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Naming;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace FinXmlProcessor.Infrastructure.Acquisition;

/// <summary>Non-secret SFTP settings. Passwords and passphrases live in <see cref="ISecretStore"/> only.</summary>
public sealed class SftpOptions
{
    public const string SectionName = "Sftp";

    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string Username { get; set; } = string.Empty;

    /// <summary>"key" (preferred) or "password".</summary>
    public string AuthMethod { get; set; } = "key";

    public string? PrivateKeyPath { get; set; }

    public string RemoteDirectory { get; set; } = "/";

    /// <summary>Glob-style pattern (* and ?) matched against remote file names.</summary>
    public string FilePattern { get; set; } = "*.xml";

    /// <summary>Expected host key algorithm, e.g. "ssh-ed25519" or "rsa-sha2-512". Required.</summary>
    public string HostKeyAlgorithm { get; set; } = string.Empty;

    /// <summary>Expected host key SHA-256 fingerprint in OpenSSH form ("SHA256:...") or plain base64. Required.</summary>
    public string HostKeyFingerprintSha256 { get; set; } = string.Empty;

    /// <summary>"newest" (default): the newest stable file whose hash has not been processed.</summary>
    public string SelectionPolicy { get; set; } = "newest";

    /// <summary>Remote archival after download. Disabled by default; the remote file is never deleted or moved otherwise.</summary>
    public bool ArchiveRemoteAfterDownload { get; set; }

    public string? RemoteArchiveDirectory { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 30;

    public int MaxTransientRetries { get; set; } = 3;

    /// <summary>When true, remote paths are treated as confidential and only file counts appear in logs.</summary>
    public bool RemotePathsAreConfidential { get; set; }
}

/// <summary>
/// SSH.NET-based acquisition with mandatory host-key pinning. Downloads to a ".part" file in staging, verifies
/// the size, hashes locally, then renames atomically. Never modifies the remote file unless archival is enabled.
/// </summary>
public sealed class SftpAcquirer : IInputAcquirer
{
    public const string Id = "sftp";
    private readonly IOptionsMonitor<SftpOptions> _options;
    private readonly ISecretStore _secrets;
    private readonly IAppPaths _paths;
    private readonly IFileDuplicateDetector _duplicates;
    private readonly ILogger<SftpAcquirer> _logger;

    public SftpAcquirer(IOptionsMonitor<SftpOptions> options, ISecretStore secrets, IAppPaths paths, IFileDuplicateDetector duplicates, ILogger<SftpAcquirer> logger)
    {
        _options = options;
        _secrets = secrets;
        _paths = paths;
        _duplicates = duplicates;
        _logger = logger;
    }

    public string ProviderId => Id;

    public bool IsConfigured
    {
        get
        {
            SftpOptions o = _options.CurrentValue;
            return o.Enabled && !string.IsNullOrWhiteSpace(o.Host) && !string.IsNullOrWhiteSpace(o.Username)
                && !string.IsNullOrWhiteSpace(o.HostKeyAlgorithm) && !string.IsNullOrWhiteSpace(o.HostKeyFingerprintSha256);
        }
    }

    public static IReadOnlyList<string> ValidateConfiguration(SftpOptions o)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(o.Host))
        {
            problems.Add("Host is required.");
        }

        if (o.Port is < 1 or > 65535)
        {
            problems.Add("Port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(o.Username))
        {
            problems.Add("Username is required.");
        }

        if (string.IsNullOrWhiteSpace(o.HostKeyAlgorithm))
        {
            problems.Add("Expected host key algorithm is required (no trust-on-first-use).");
        }

        if (string.IsNullOrWhiteSpace(o.HostKeyFingerprintSha256))
        {
            problems.Add("Expected host key SHA-256 fingerprint is required (no trust-on-first-use).");
        }

        if (string.Equals(o.AuthMethod, "key", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(o.PrivateKeyPath))
            {
                problems.Add("Private key path is required for key authentication.");
            }
            else if (!File.Exists(o.PrivateKeyPath))
            {
                problems.Add("Private key file does not exist.");
            }
        }
        else if (!string.Equals(o.AuthMethod, "password", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("AuthMethod must be 'key' or 'password'.");
        }

        return problems;
    }

    public async Task<IReadOnlyList<string>> TestAsync(CancellationToken cancellationToken)
    {
        SftpOptions o = _options.CurrentValue;
        var lines = new List<string>(ValidateConfiguration(o));
        if (lines.Count > 0)
        {
            return lines;
        }

        try
        {
            using SftpClient client = await ConnectAsync(o, cancellationToken).ConfigureAwait(false);
            lines.Add($"Connected to {o.Host}:{o.Port} as {o.Username}; host key verified ({o.HostKeyAlgorithm}).");
            int count = 0;
            await foreach (ISftpFile _ in ListMatchingAsync(client, o, cancellationToken).ConfigureAwait(false))
            {
                count++;
            }

            lines.Add($"{count} file(s) match the pattern in the remote directory.");
        }
        catch (Exception ex)
        {
            lines.Add("Connection test failed: " + Sanitize(ex));
        }

        return lines;
    }

    public async Task<AcquisitionResult> AcquireAsync(CancellationToken cancellationToken)
    {
        SftpOptions o = _options.CurrentValue;
        var diagnostics = new List<string>(ValidateConfiguration(o));
        if (diagnostics.Count > 0)
        {
            return new AcquisitionResult([], diagnostics);
        }

        Directory.CreateDirectory(_paths.Staging);
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await AcquireOnceAsync(o, diagnostics, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt <= o.MaxTransientRetries && !cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay = Backoff(attempt);
                _logger.LogWarning("SFTP attempt {Attempt} failed transiently ({Type}); retrying in {Delay}s", attempt, ex.GetType().Name, delay.TotalSeconds);
                diagnostics.Add($"Attempt {attempt} failed transiently ({ex.GetType().Name}); retrying.");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string message = Sanitize(ex);
                _logger.LogError(ex, "SFTP acquisition failed: {Message}", message);
                diagnostics.Add("SFTP acquisition failed: " + message);
                return new AcquisitionResult([], diagnostics);
            }
        }
    }

    private async Task<AcquisitionResult> AcquireOnceAsync(SftpOptions o, List<string> diagnostics, CancellationToken cancellationToken)
    {
        using SftpClient client = await ConnectAsync(o, cancellationToken).ConfigureAwait(false);
        var candidates = new List<ISftpFile>();
        await foreach (ISftpFile file in ListMatchingAsync(client, o, cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(file);
        }

        diagnostics.Add($"{candidates.Count} remote file(s) match the pattern.");
        var inputs = new List<AcquiredInput>();
        foreach (ISftpFile file in candidates.OrderByDescending(f => f.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string localName = OutputNaming.SafeFileNameFromPath(file.Name);
            string finalPath = Path.Combine(_paths.Staging, localName);
            string partPath = finalPath + ".part";
            long expected = file.Length;

            // Stability: the remote size must not change during a short window (still uploading otherwise).
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            ISftpFile again = await client.GetAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            if (again.Length != expected)
            {
                diagnostics.Add($"Skipped {Describe(o, file)}: remote size is still changing.");
                continue;
            }

            string hash;
            await using (FileStream target = new(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 17, FileOptions.SequentialScan))
            {
                client.DownloadFile(file.FullName, target);
            }

            long actual = new FileInfo(partPath).Length;
            if (actual != expected)
            {
                File.Delete(partPath);
                diagnostics.Add($"Download of {Describe(o, file)} was incomplete ({actual} of {expected} bytes); discarded.");
                continue;
            }

            await using (FileStream verify = new(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(verify, cancellationToken).ConfigureAwait(false));
            }

            FileDuplicateMatch? processed = await _duplicates.FindBySha256Async(hash, cancellationToken).ConfigureAwait(false);
            if (processed is not null)
            {
                File.Delete(partPath);
                diagnostics.Add($"Skipped {Describe(o, file)}: already processed as job {processed.JobId:D}.");
                continue;
            }

            if (File.Exists(finalPath))
            {
                finalPath = Path.Combine(_paths.Staging, $"{Path.GetFileNameWithoutExtension(localName)}_{DateTime.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(localName)}");
            }

            File.Move(partPath, finalPath, overwrite: false);
            inputs.Add(new AcquiredInput(finalPath, localName, expected, hash, Id, o.RemotePathsAreConfidential ? null : file.FullName));
            _logger.LogInformation("Downloaded {File} ({Bytes} bytes) to staging", Describe(o, file), expected);

            if (o.ArchiveRemoteAfterDownload && !string.IsNullOrWhiteSpace(o.RemoteArchiveDirectory))
            {
                string archived = o.RemoteArchiveDirectory.TrimEnd('/') + "/" + file.Name;
                file.MoveTo(archived);
                diagnostics.Add($"Archived remote file {Describe(o, file)}.");
            }

            if (string.Equals(o.SelectionPolicy, "newest", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return new AcquisitionResult(inputs, diagnostics);
    }

    private async Task<SftpClient> ConnectAsync(SftpOptions o, CancellationToken cancellationToken)
    {
        AuthenticationMethod auth;
        if (string.Equals(o.AuthMethod, "password", StringComparison.OrdinalIgnoreCase))
        {
            string password = await _secrets.RetrieveAsync(SecretNames.Service, SecretNames.SftpPassword, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("SFTP password is not stored in the secret store.");
            auth = new PasswordAuthenticationMethod(o.Username, password);
        }
        else
        {
            string? passphrase = await _secrets.RetrieveAsync(SecretNames.Service, SecretNames.SftpKeyPassphrase, cancellationToken).ConfigureAwait(false);
            PrivateKeyFile key = string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(o.PrivateKeyPath!) : new PrivateKeyFile(o.PrivateKeyPath!, passphrase);
            auth = new PrivateKeyAuthenticationMethod(o.Username, key);
        }

        var connectionInfo = new ConnectionInfo(o.Host, o.Port, o.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(o.ConnectTimeoutSeconds),
        };
        var client = new SftpClient(connectionInfo);
        string expectedFingerprint = NormalizeFingerprint(o.HostKeyFingerprintSha256);
        client.HostKeyReceived += (_, e) =>
        {
            bool algorithmOk = string.Equals(e.HostKeyName, o.HostKeyAlgorithm, StringComparison.OrdinalIgnoreCase);
            bool fingerprintOk = string.Equals(NormalizeFingerprint(e.FingerPrintSHA256), expectedFingerprint, StringComparison.Ordinal);
            e.CanTrust = algorithmOk && fingerprintOk;
            if (!e.CanTrust)
            {
                _logger.LogError("SFTP host key rejected: algorithm {Algorithm} (expected {ExpectedAlgorithm}), fingerprint mismatch={Mismatch}", e.HostKeyName, o.HostKeyAlgorithm, !fingerprintOk);
            }
        };

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return client;
    }

    private static async IAsyncEnumerable<ISftpFile> ListMatchingAsync(SftpClient client, SftpOptions o, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pattern = new Regex("^" + Regex.Escape(o.FilePattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        await foreach (ISftpFile file in client.ListDirectoryAsync(o.RemoteDirectory, cancellationToken).ConfigureAwait(false))
        {
            if (file.IsRegularFile && pattern.IsMatch(file.Name) && !file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !file.Name.EndsWith(".filepart", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static string Describe(SftpOptions o, ISftpFile file) => o.RemotePathsAreConfidential ? "[confidential remote file]" : file.Name;

    private static string NormalizeFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return string.Empty;
        }

        string f = fingerprint.Trim();
        if (f.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            f = f[7..];
        }

        return f.TrimEnd('=');
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        SshAuthenticationException => false,
        SftpPathNotFoundException => false,
        SftpPermissionDeniedException => false,
        SshConnectionException e when e.Message.Contains("host key", StringComparison.OrdinalIgnoreCase) => false,
        SshConnectionException => true,
        SshOperationTimeoutException => true,
        System.Net.Sockets.SocketException => true,
        IOException => true,
        TimeoutException => true,
        _ => false,
    };

    private static TimeSpan Backoff(int attempt)
    {
        double seconds = Math.Min(60, Math.Pow(2, attempt)) + Random.Shared.NextDouble() * 2;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>Error text without host names, user names, paths or credentials.</summary>
    public static string Sanitize(Exception ex) => ex switch
    {
        SshAuthenticationException => "authentication was rejected by the server (check the user name, key or password).",
        SshConnectionException e when e.Message.Contains("host key", StringComparison.OrdinalIgnoreCase) => "the server host key did not match the expected algorithm and fingerprint. Connection refused.",
        SshConnectionException => "the SSH connection could not be established or was dropped.",
        SftpPathNotFoundException => "the remote directory or file was not found.",
        SftpPermissionDeniedException => "the server denied access to the remote path.",
        SshOperationTimeoutException => "the operation timed out.",
        InvalidOperationException e => e.Message,
        _ => $"{ex.GetType().Name}.",
    };
}
