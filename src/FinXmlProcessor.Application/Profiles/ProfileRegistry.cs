using FinXmlProcessor.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Application.Profiles;

public sealed record InstalledProfile(string Path, string FileName, ProfileValidationResult Validation)
{
    public string? Id => Validation.Profile?.Id;

    public bool IsValid => Validation.IsValid;
}

/// <summary>Installed profiles live in the per-user profiles folder. Built-in demo profiles are copied there on first run.</summary>
public interface IProfileRegistry
{
    Task<IReadOnlyList<InstalledProfile>> ListAsync(CancellationToken cancellationToken);

    Task<ProfileValidationResult> GetByIdAsync(string profileId, CancellationToken cancellationToken);

    /// <summary>Validates and copies a profile file into the profiles folder. Returns the validation result; invalid profiles are not installed.</summary>
    Task<ProfileValidationResult> ImportAsync(string sourcePath, CancellationToken cancellationToken);

    /// <summary>Installs embedded/sample profiles that are not present yet. Never overwrites user changes.</summary>
    Task EnsureBuiltInProfilesAsync(IReadOnlyDictionary<string, string> builtInProfilesJsonByFileName, CancellationToken cancellationToken);
}

public sealed class FileProfileRegistry : IProfileRegistry
{
    private readonly IAppPaths _paths;
    private readonly ProfileLoader _loader;
    private readonly ILogger<FileProfileRegistry> _logger;

    public FileProfileRegistry(IAppPaths paths, ProfileLoader loader, ILogger<FileProfileRegistry> logger)
    {
        _paths = paths;
        _loader = loader;
        _logger = logger;
    }

    public async Task<IReadOnlyList<InstalledProfile>> ListAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.Profiles);
        var results = new List<InstalledProfile>();
        foreach (string file in Directory.EnumerateFiles(_paths.Profiles, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            ProfileValidationResult validation = await _loader.LoadFileAsync(file, cancellationToken).ConfigureAwait(false);
            results.Add(new InstalledProfile(file, Path.GetFileName(file), validation));
        }

        return results;
    }

    public async Task<ProfileValidationResult> GetByIdAsync(string profileId, CancellationToken cancellationToken)
    {
        // A profile may also be supplied as a path (CLI convenience).
        if (File.Exists(profileId))
        {
            return await _loader.LoadFileAsync(profileId, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<InstalledProfile> installed = await ListAsync(cancellationToken).ConfigureAwait(false);
        var matches = installed.Where(p => string.Equals(p.Id, profileId, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
        {
            return ProfileValidationResult.Failure([$"No installed profile has id '{profileId}'."]);
        }

        if (matches.Count > 1)
        {
            return ProfileValidationResult.Failure([$"More than one installed profile has id '{profileId}': {string.Join(", ", matches.Select(m => m.FileName))}."]);
        }

        return matches[0].Validation;
    }

    public async Task<ProfileValidationResult> ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ProfileValidationResult result = await _loader.LoadFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!result.IsValid || result.Profile is null)
        {
            return result;
        }

        Directory.CreateDirectory(_paths.Profiles);
        string target = Path.Combine(_paths.Profiles, Naming.OutputNaming.SanitizeFileNameComponent(result.Profile.Id) + ".json");
        string temp = target + ".tmp";
        File.Copy(sourcePath, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);
        _logger.LogInformation("Imported profile {ProfileId} version {ProfileVersion}", result.Profile.Id, result.Profile.Version);
        return result;
    }

    public async Task EnsureBuiltInProfilesAsync(IReadOnlyDictionary<string, string> builtInProfilesJsonByFileName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.Profiles);
        foreach ((string fileName, string json) in builtInProfilesJsonByFileName)
        {
            string target = Path.Combine(_paths.Profiles, Naming.OutputNaming.SafeFileNameFromPath(fileName));
            if (File.Exists(target))
            {
                continue;
            }

            string temp = target + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
            File.Move(temp, target, overwrite: false);
            _logger.LogInformation("Installed built-in profile {FileName}", fileName);
        }
    }
}
