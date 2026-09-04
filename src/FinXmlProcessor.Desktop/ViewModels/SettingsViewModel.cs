using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinXmlProcessor.Application;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Desktop.Services;
using FinXmlProcessor.Infrastructure.Acquisition;
using FinXmlProcessor.Infrastructure.Diagnostics;
using FinXmlProcessor.Infrastructure.Paths;
using FinXmlProcessor.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Desktop.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly UserSettingsStore _store;
    private readonly AppPaths _paths;
    private readonly IProcessingRepository _repository;
    private readonly IBackgroundAgentManager _agent;
    private readonly ISecretStore _secrets;
    private readonly IEnumerable<IInputAcquirer> _acquirers;
    private readonly DiagnosticsService _diagnostics;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly ILogger<SettingsViewModel> _logger;

    // Folders
    [ObservableProperty]
    private string _inputDirectory = string.Empty;

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private string _inputPattern = "*.xml";

    [ObservableProperty]
    private string _maxInputMegabytes = "1024";

    // Schedule
    [ObservableProperty]
    private bool _scheduleEnabled;

    [ObservableProperty]
    private string _scheduleTime = "19:00";

    [ObservableProperty]
    private string _catchUpWindowHours = "20";

    [ObservableProperty]
    private string _agentStatusText = string.Empty;

    [ObservableProperty]
    private bool _agentSupported;

    // Retention
    [ObservableProperty]
    private bool _retainLogs;

    [ObservableProperty]
    private string _logsDays = "90";

    [ObservableProperty]
    private bool _retainReports;

    [ObservableProperty]
    private string _reportsDays = "90";

    [ObservableProperty]
    private bool _retainQuarantine;

    [ObservableProperty]
    private string _quarantineDays = "90";

    [ObservableProperty]
    private bool _retainHistory;

    [ObservableProperty]
    private string _historyDays = "365";

    // SFTP (non-secret)
    [ObservableProperty]
    private bool _sftpEnabled;

    [ObservableProperty]
    private string _sftpHost = string.Empty;

    [ObservableProperty]
    private string _sftpPort = "22";

    [ObservableProperty]
    private string _sftpUsername = string.Empty;

    [ObservableProperty]
    private string _sftpAuthMethod = "key";

    [ObservableProperty]
    private string _sftpPrivateKeyPath = string.Empty;

    [ObservableProperty]
    private string _sftpRemoteDirectory = "/";

    [ObservableProperty]
    private string _sftpFilePattern = "*.xml";

    [ObservableProperty]
    private string _sftpHostKeyAlgorithm = string.Empty;

    [ObservableProperty]
    private string _sftpHostKeyFingerprint = string.Empty;

    [ObservableProperty]
    private string _sftpSecretInput = string.Empty;

    [ObservableProperty]
    private string _sftpSecretStatus = string.Empty;

    [ObservableProperty]
    private string _sftpTestResult = string.Empty;

    // Delivery
    [ObservableProperty]
    private string _deliveryProvider = "none";

    [ObservableProperty]
    private string _deliveryFolder = string.Empty;

    [ObservableProperty]
    private string _deliveryCollisionPolicy = "version";

    // Appearance and diagnostics
    [ObservableProperty]
    private string _appearance = "System";

    [ObservableProperty]
    private string _saveStatus = string.Empty;

    public SettingsViewModel(UserSettingsStore store, AppPaths paths, IProcessingRepository repository, IBackgroundAgentManager agent, ISecretStore secrets, IEnumerable<IInputAcquirer> acquirers, DiagnosticsService diagnostics, IDialogService dialogs, IShellService shell, ILogger<SettingsViewModel> logger)
    {
        _store = store;
        _paths = paths;
        _repository = repository;
        _agent = agent;
        _secrets = secrets;
        _acquirers = acquirers;
        _diagnostics = diagnostics;
        _dialogs = dialogs;
        _shell = shell;
        _logger = logger;
    }

    public override string Title => "Settings";

    public IReadOnlyList<string> AuthMethods { get; } = ["key", "password"];

    public IReadOnlyList<string> DeliveryProviders { get; } = ["none", "local-folder"];

    public IReadOnlyList<string> CollisionPolicies { get; } = ["version", "fail", "overwrite"];

    public IReadOnlyList<string> Appearances { get; } = ["System", "Light", "Dark"];

    public ObservableCollection<DiagnosticFact> Diagnostics { get; } = [];

    public string SecretStoreName => _secrets.ProviderName;

    public string AppVersion => $"{AppInfo.ProductName} {AppInfo.Version}";

    public string DataFolder => _paths.Root;

    public string ReleasesUrl => AppInfo.ReleasesUrl;

    public override Task ActivateAsync() => GuardAsync(async () =>
    {
        UserSettingsStore.UserSettings s = await _store.LoadAsync(CancellationToken.None);
        InputDirectory = s.Processing.InputDirectory ?? _paths.DefaultInput;
        OutputDirectory = s.Processing.OutputDirectory ?? _paths.DefaultOutput;
        InputPattern = s.Processing.InputPattern;
        MaxInputMegabytes = (s.Processing.MaxInputBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ScheduleEnabled = s.Schedule.Enabled;
        ScheduleTime = s.Schedule.Time;
        CatchUpWindowHours = s.Schedule.CatchUpWindowHours.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RetainLogs = s.Retention.Logs.Enabled;
        LogsDays = s.Retention.Logs.MaxAgeDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RetainReports = s.Retention.Reports.Enabled;
        ReportsDays = s.Retention.Reports.MaxAgeDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RetainQuarantine = s.Retention.Quarantine.Enabled;
        QuarantineDays = s.Retention.Quarantine.MaxAgeDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RetainHistory = s.Retention.History.Enabled;
        HistoryDays = s.Retention.History.MaxAgeDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SftpEnabled = s.Sftp.Enabled;
        SftpHost = s.Sftp.Host;
        SftpPort = s.Sftp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SftpUsername = s.Sftp.Username;
        SftpAuthMethod = s.Sftp.AuthMethod;
        SftpPrivateKeyPath = s.Sftp.PrivateKeyPath ?? string.Empty;
        SftpRemoteDirectory = s.Sftp.RemoteDirectory;
        SftpFilePattern = s.Sftp.FilePattern;
        SftpHostKeyAlgorithm = s.Sftp.HostKeyAlgorithm;
        SftpHostKeyFingerprint = s.Sftp.HostKeyFingerprintSha256;
        DeliveryProvider = s.Delivery.Provider;
        DeliveryFolder = s.Delivery.LocalFolder ?? string.Empty;
        DeliveryCollisionPolicy = s.Delivery.CollisionPolicy;
        Appearance = await _repository.GetSettingAsync("appearance", CancellationToken.None) ?? "System";
        await RefreshSecretStatusAsync();
        await RefreshAgentStatusAsync();
        await RefreshDiagnosticsAsync();
    }, _logger, null, "Loading settings");

    [RelayCommand]
    private Task SaveAsync() => GuardAsync(async () =>
    {
        UserSettingsStore.UserSettings s = await _store.LoadAsync(CancellationToken.None);
        s.Processing.InputDirectory = string.IsNullOrWhiteSpace(InputDirectory) ? null : InputDirectory;
        s.Processing.OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory;
        s.Processing.InputPattern = string.IsNullOrWhiteSpace(InputPattern) ? "*.xml" : InputPattern;
        s.Processing.MaxInputBytes = Math.Max(1, ParseInt(MaxInputMegabytes, 1024)) * 1024L * 1024L;
        s.Schedule.Enabled = ScheduleEnabled;
        s.Schedule.Time = ScheduleTime;
        s.Schedule.CatchUpWindowHours = ParseInt(CatchUpWindowHours, 20);
        s.Retention.Logs.Enabled = RetainLogs;
        s.Retention.Logs.MaxAgeDays = ParseInt(LogsDays, 90);
        s.Retention.Reports.Enabled = RetainReports;
        s.Retention.Reports.MaxAgeDays = ParseInt(ReportsDays, 90);
        s.Retention.Quarantine.Enabled = RetainQuarantine;
        s.Retention.Quarantine.MaxAgeDays = ParseInt(QuarantineDays, 90);
        s.Retention.History.Enabled = RetainHistory;
        s.Retention.History.MaxAgeDays = ParseInt(HistoryDays, 365);
        s.Sftp.Enabled = SftpEnabled;
        s.Sftp.Host = SftpHost.Trim();
        s.Sftp.Port = ParseInt(SftpPort, 22);
        s.Sftp.Username = SftpUsername.Trim();
        s.Sftp.AuthMethod = SftpAuthMethod;
        s.Sftp.PrivateKeyPath = string.IsNullOrWhiteSpace(SftpPrivateKeyPath) ? null : SftpPrivateKeyPath.Trim();
        s.Sftp.RemoteDirectory = string.IsNullOrWhiteSpace(SftpRemoteDirectory) ? "/" : SftpRemoteDirectory.Trim();
        s.Sftp.FilePattern = string.IsNullOrWhiteSpace(SftpFilePattern) ? "*.xml" : SftpFilePattern.Trim();
        s.Sftp.HostKeyAlgorithm = SftpHostKeyAlgorithm.Trim();
        s.Sftp.HostKeyFingerprintSha256 = SftpHostKeyFingerprint.Trim();
        s.Delivery.Provider = DeliveryProvider;
        s.Delivery.LocalFolder = string.IsNullOrWhiteSpace(DeliveryFolder) ? null : DeliveryFolder.Trim();
        s.Delivery.CollisionPolicy = DeliveryCollisionPolicy;
        if (SftpEnabled)
        {
            IReadOnlyList<string> problems = SftpAcquirer.ValidateConfiguration(s.Sftp);
            if (problems.Count > 0)
            {
                await _dialogs.ShowMessageAsync("SFTP settings incomplete", "SFTP stays enabled but will not run until these are fixed:\n\n" + string.Join("\n", problems));
            }
        }

        await _store.SaveAsync(s, CancellationToken.None);
        await _repository.SetSettingAsync("appearance", Appearance, CancellationToken.None);
        App.ApplyTheme(Appearance);
        SaveStatus = $"Saved at {DateTime.Now:HH:mm:ss}.";
    }, _logger, _dialogs, "Saving settings");

    [RelayCommand]
    private async Task BrowseInputAsync()
    {
        string? path = await _dialogs.PickFolderAsync("Choose the input folder");
        if (path is not null)
        {
            InputDirectory = path;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        string? path = await _dialogs.PickFolderAsync("Choose the output folder");
        if (path is not null)
        {
            OutputDirectory = path;
        }
    }

    [RelayCommand]
    private async Task BrowseDeliveryAsync()
    {
        string? path = await _dialogs.PickFolderAsync("Choose the delivery folder");
        if (path is not null)
        {
            DeliveryFolder = path;
        }
    }

    [RelayCommand]
    private async Task BrowseKeyAsync()
    {
        string? path = await _dialogs.PickFileAsync("Choose the SSH private key", "Private keys", "*");
        if (path is not null)
        {
            SftpPrivateKeyPath = path;
        }
    }

    [RelayCommand]
    private Task StoreSecretAsync() => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(SftpSecretInput))
        {
            return;
        }

        string name = string.Equals(SftpAuthMethod, "password", StringComparison.OrdinalIgnoreCase) ? SecretNames.SftpPassword : SecretNames.SftpKeyPassphrase;
        await _secrets.StoreAsync(SecretNames.Service, name, SftpSecretInput, CancellationToken.None);
        SftpSecretInput = string.Empty;
        await RefreshSecretStatusAsync();
    }, _logger, _dialogs, "Storing secret");

    [RelayCommand]
    private Task ClearSecretsAsync() => GuardAsync(async () =>
    {
        await _secrets.DeleteAsync(SecretNames.Service, SecretNames.SftpPassword, CancellationToken.None);
        await _secrets.DeleteAsync(SecretNames.Service, SecretNames.SftpKeyPassphrase, CancellationToken.None);
        await RefreshSecretStatusAsync();
    }, _logger, _dialogs, "Clearing secrets");

    [RelayCommand]
    private Task TestSftpAsync() => GuardAsync(async () =>
    {
        SftpTestResult = "Testing…";
        IInputAcquirer sftp = _acquirers.First(a => a.ProviderId == SftpAcquirer.Id);
        IReadOnlyList<string> lines = await Task.Run(() => sftp.TestAsync(CancellationToken.None));
        SftpTestResult = string.Join("\n", lines);
    }, _logger, _dialogs, "SFTP test");

    [RelayCommand]
    private Task InstallAgentAsync() => GuardAsync(async () =>
    {
        await SaveAsync();
        await _agent.InstallOrUpdateAsync(CancellationToken.None);
        await RefreshAgentStatusAsync();
    }, _logger, _dialogs, "Installing the background agent");

    [RelayCommand]
    private Task UninstallAgentAsync() => GuardAsync(async () =>
    {
        await _agent.UninstallAsync(CancellationToken.None);
        await RefreshAgentStatusAsync();
    }, _logger, _dialogs, "Removing the background agent");

    [RelayCommand]
    private Task RefreshDiagnosticsAsync() => GuardAsync(async () =>
    {
        Diagnostics.Clear();
        foreach (KeyValuePair<string, string> fact in await _diagnostics.CollectAsync(CancellationToken.None))
        {
            Diagnostics.Add(new DiagnosticFact(fact.Key.Trim(), fact.Value));
        }
    }, _logger, null, "Diagnostics");

    [RelayCommand]
    private Task ExportBundleAsync() => GuardAsync(async () =>
    {
        string? target = await _dialogs.SaveFileAsync("Export diagnostic bundle", $"finxml-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (target is null)
        {
            return;
        }

        await _diagnostics.ExportBundleAsync(target, CancellationToken.None);
        await _dialogs.ShowMessageAsync("Bundle exported", $"Saved to {target}. It contains sanitized diagnostics, redacted settings, recent logs and reports; never input XML, workbooks, keys or secrets.");
    }, _logger, _dialogs, "Exporting diagnostics");

    [RelayCommand]
    private void OpenDataFolder() => _shell.Reveal(_paths.Root);

    [RelayCommand]
    private void OpenReleases() => _shell.OpenUrl(AppInfo.ReleasesUrl);

    private async Task RefreshSecretStatusAsync()
    {
        try
        {
            bool password = await _secrets.RetrieveAsync(SecretNames.Service, SecretNames.SftpPassword, CancellationToken.None) is not null;
            bool passphrase = await _secrets.RetrieveAsync(SecretNames.Service, SecretNames.SftpKeyPassphrase, CancellationToken.None) is not null;
            SftpSecretStatus = $"Stored in {_secrets.ProviderName}: password {(password ? "yes" : "no")}, key passphrase {(passphrase ? "yes" : "no")}. Values are never displayed.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Secret store unavailable");
            SftpSecretStatus = $"Secret store unavailable ({ex.GetType().Name}).";
        }
    }

    private async Task RefreshAgentStatusAsync()
    {
        AgentStatus status = await _agent.GetStatusAsync(CancellationToken.None);
        AgentSupported = status.IsSupported;
        AgentStatusText = status.IsSupported
            ? $"Installed: {status.IsInstalled}. Loaded in launchd: {status.IsLoaded}.\n" + string.Join("\n", status.Diagnostics)
            : string.Join("\n", status.Diagnostics);
    }

    private static int ParseInt(string text, int fallback) => int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : fallback;
}

public sealed record DiagnosticFact(string Key, string Value);
