using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinXmlProcessor.Application.Abstractions;

namespace FinXmlProcessor.Infrastructure.Secrets;

/// <summary>Test double. Never used in production wiring.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public string ProviderName => "in-memory (tests only)";

    public Task StoreAsync(string service, string account, string secret, CancellationToken cancellationToken)
    {
        _values[Key(service, account)] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> RetrieveAsync(string service, string account, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryGetValue(Key(service, account), out string? v) ? v : null);

    public Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken) =>
        Task.FromResult(_values.TryRemove(Key(service, account), out _));

    private static string Key(string service, string account) => service + "" + account;
}

/// <summary>
/// Windows development store: each secret is encrypted with DPAPI (current user scope) and the ciphertext is kept
/// in a JSON file in the settings folder. No plaintext ever touches disk.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FinXmlProcessor.DpapiSecretStore.v1");
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiSecretStore(IAppPaths paths)
    {
        _path = Path.Combine(paths.Settings, "secrets.dpapi.json");
    }

    public string ProviderName => "Windows DPAPI (development)";

    public async Task StoreAsync(string service, string account, string secret, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> map = await LoadAsync(cancellationToken).ConfigureAwait(false);
            byte[] protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
            map[Key(service, account)] = Convert.ToBase64String(protectedBytes);
            await SaveAsync(map, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> RetrieveAsync(string service, string account, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> map = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!map.TryGetValue(Key(service, account), out string? encoded))
            {
                return null;
            }

            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> map = await LoadAsync(cancellationToken).ConfigureAwait(false);
            bool removed = map.Remove(Key(service, account));
            if (removed)
            {
                await SaveAsync(map, cancellationToken).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await using FileStream stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task SaveAsync(Dictionary<string, string> map, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        await using (FileStream stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, map, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static string Key(string service, string account) => service + "/" + account;
}

/// <summary>
/// macOS Keychain Services through a deliberately narrow interop surface (generic passwords only). Items are
/// created in the user's login keychain, scoped by service and account. Only exercised on macOS CI runners.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class KeychainSecretStore : ISecretStore
{
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;

    public string ProviderName => "macOS Keychain";

    public Task StoreAsync(string service, string account, string secret, CancellationToken cancellationToken)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            int status = NativeMethods.SecKeychainAddGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, (uint)accountBytes.Length, accountBytes, (uint)secretBytes.Length, secretBytes, out IntPtr item);
            if (status == ErrSecDuplicateItem)
            {
                DeleteItem(serviceBytes, accountBytes);
                status = NativeMethods.SecKeychainAddGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, (uint)accountBytes.Length, accountBytes, (uint)secretBytes.Length, secretBytes, out item);
            }

            if (item != IntPtr.Zero)
            {
                NativeMethods.CFRelease(item);
            }

            if (status != ErrSecSuccess)
            {
                throw new InvalidOperationException($"Keychain refused to store the item (OSStatus {status}).");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }

        return Task.CompletedTask;
    }

    public Task<string?> RetrieveAsync(string service, string account, CancellationToken cancellationToken)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        int status = NativeMethods.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, (uint)accountBytes.Length, accountBytes, out uint length, out IntPtr data, out IntPtr item);
        if (status == ErrSecItemNotFound)
        {
            return Task.FromResult<string?>(null);
        }

        if (status != ErrSecSuccess)
        {
            throw new InvalidOperationException($"Keychain lookup failed (OSStatus {status}).");
        }

        try
        {
            var buffer = new byte[length];
            Marshal.Copy(data, buffer, 0, (int)length);
            string value = Encoding.UTF8.GetString(buffer);
            CryptographicOperations.ZeroMemory(buffer);
            return Task.FromResult<string?>(value);
        }
        finally
        {
            _ = NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero)
            {
                NativeMethods.CFRelease(item);
            }
        }
    }

    public Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken) =>
        Task.FromResult(DeleteItem(Encoding.UTF8.GetBytes(service), Encoding.UTF8.GetBytes(account)));

    private static bool DeleteItem(byte[] serviceBytes, byte[] accountBytes)
    {
        int status = NativeMethods.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, (uint)accountBytes.Length, accountBytes, out _, out IntPtr data, out IntPtr item);
        if (status == ErrSecItemNotFound)
        {
            return false;
        }

        if (status != ErrSecSuccess)
        {
            throw new InvalidOperationException($"Keychain lookup failed (OSStatus {status}).");
        }

        try
        {
            int deleteStatus = NativeMethods.SecKeychainItemDelete(item);
            if (deleteStatus != ErrSecSuccess)
            {
                throw new InvalidOperationException($"Keychain refused to delete the item (OSStatus {deleteStatus}).");
            }

            return true;
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                _ = NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, data);
            }

            if (item != IntPtr.Zero)
            {
                NativeMethods.CFRelease(item);
            }
        }
    }

    private static partial class NativeMethods
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [LibraryImport(Security)]
        internal static partial int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

        [LibraryImport(Security)]
        internal static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

        [LibraryImport(Security)]
        internal static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [LibraryImport(Security)]
        internal static partial int SecKeychainItemDelete(IntPtr itemRef);

        [LibraryImport(CoreFoundation)]
        internal static partial void CFRelease(IntPtr cf);
    }
}

/// <summary>Used on platforms without a supported secure store (e.g. Linux CI). Every operation fails clearly.</summary>
public sealed class UnsupportedSecretStore : ISecretStore
{
    public string ProviderName => "unsupported on this platform";

    public Task StoreAsync(string service, string account, string secret, CancellationToken cancellationToken) => throw Unsupported();

    public Task<string?> RetrieveAsync(string service, string account, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken) => Task.FromResult(false);

    private static PlatformNotSupportedException Unsupported() => new("No secure secret store is available on this platform. Use macOS (Keychain) or Windows (DPAPI).");
}
