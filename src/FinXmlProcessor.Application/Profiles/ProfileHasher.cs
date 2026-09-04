using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FinXmlProcessor.Application.Profiles;

/// <summary>Canonical SHA-256 of a profile so every job records exactly which mapping rules produced its output.</summary>
public static class ProfileHasher
{
    private static readonly JsonSerializerOptions Canonical = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static string Compute(MappingProfile profile)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(profile, Canonical);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    public static string ComputeForText(string json)
    {
        // Normalise line endings so the same profile hashes identically on Windows and macOS checkouts.
        string normalized = json.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
