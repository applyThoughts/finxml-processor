using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Desktop.Services;
using FinXmlProcessor.Infrastructure.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Desktop.ViewModels;

public sealed class ProfileRowViewModel
{
    public ProfileRowViewModel(InstalledProfile profile, bool isActive)
    {
        Installed = profile;
        FileName = profile.FileName;
        Id = profile.Id ?? "(invalid)";
        DisplayName = profile.Validation.Profile?.DisplayName ?? "-";
        Version = profile.Validation.Profile?.Version ?? "-";
        IsValid = profile.IsValid;
        IsSynthetic = profile.Validation.Profile?.IsSynthetic == true;
        IsActive = isActive;
        Status = !profile.IsValid ? "Invalid" : isActive ? "Active" : "Installed";
        Details = profile.IsValid
            ? $"Hash {profile.Validation.Profile!.Hash[..12]}…; record path {string.Join("/", profile.Validation.Profile.Source.RecordPath)}; {profile.Validation.Profile.Fields.Count} fields; duplicate key: {(profile.Validation.Profile.HasDuplicateKey ? string.Join("+", profile.Validation.Profile.Source.DuplicateKeyFields) : "none")}{(IsSynthetic ? "; SYNTHETIC DEMO RULES" : string.Empty)}"
            : string.Join("\n", profile.Validation.Errors);
    }

    public InstalledProfile Installed { get; }

    public string FileName { get; }

    public string Id { get; }

    public string DisplayName { get; }

    public string Version { get; }

    public bool IsValid { get; }

    public bool IsSynthetic { get; }

    public bool IsActive { get; }

    public string Status { get; }

    public string Details { get; }
}

public sealed partial class ProfilesViewModel : PageViewModel
{
    private readonly IProfileRegistry _registry;
    private readonly IOptionsMonitor<ProcessingOptions> _options;
    private readonly UserSettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly ILogger<ProfilesViewModel> _logger;

    [ObservableProperty]
    private ProfileRowViewModel? _selected;

    [ObservableProperty]
    private string _schemaText = string.Empty;

    public ProfilesViewModel(IProfileRegistry registry, IOptionsMonitor<ProcessingOptions> options, UserSettingsStore settings, IDialogService dialogs, IShellService shell, ILogger<ProfilesViewModel> logger)
    {
        _registry = registry;
        _options = options;
        _settings = settings;
        _dialogs = dialogs;
        _shell = shell;
        _logger = logger;
    }

    public override string Title => "Profiles";

    public ObservableCollection<ProfileRowViewModel> Profiles { get; } = [];

    public override Task ActivateAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => GuardAsync(async () =>
    {
        string active = _options.CurrentValue.ActiveProfileId;
        Profiles.Clear();
        foreach (InstalledProfile profile in await _registry.ListAsync(CancellationToken.None))
        {
            Profiles.Add(new ProfileRowViewModel(profile, string.Equals(profile.Id, active, StringComparison.Ordinal)));
        }

        SchemaText = ProfileLoader.SchemaJson;
    }, _logger, null, "Loading profiles");

    [RelayCommand]
    private Task ImportAsync() => GuardAsync(async () =>
    {
        string? path = await _dialogs.PickFileAsync("Choose a mapping profile to import", "Profile JSON", "*.json");
        if (path is null)
        {
            return;
        }

        ProfileValidationResult result = await _registry.ImportAsync(path, CancellationToken.None);
        if (!result.IsValid)
        {
            await _dialogs.ShowMessageAsync("Profile not imported", "The profile failed validation:\n\n" + string.Join("\n", result.Errors.Take(20)));
            return;
        }

        await RefreshAsync();
    }, _logger, _dialogs, "Import profile");

    [RelayCommand]
    private Task ExportAsync(ProfileRowViewModel? row) => GuardAsync(async () =>
    {
        if (row is null)
        {
            return;
        }

        string? target = await _dialogs.SaveFileAsync("Export profile", row.FileName);
        if (target is not null)
        {
            File.Copy(row.Installed.Path, target, overwrite: true);
        }
    }, _logger, _dialogs, "Export profile");

    [RelayCommand]
    private Task SetActiveAsync(ProfileRowViewModel? row) => GuardAsync(async () =>
    {
        if (row is null || !row.IsValid)
        {
            return;
        }

        UserSettingsStore.UserSettings settings = await _settings.LoadAsync(CancellationToken.None);
        settings.Processing.ActiveProfileId = row.Id;
        await _settings.SaveAsync(settings, CancellationToken.None);
        await Task.Delay(300); // let the configuration reload observe the file change
        await RefreshAsync();
    }, _logger, _dialogs, "Activate profile");

    [RelayCommand]
    private void RevealFolder()
    {
        if (Profiles.Count > 0)
        {
            _shell.Reveal(Path.GetDirectoryName(Profiles[0].Installed.Path)!);
        }
    }
}
