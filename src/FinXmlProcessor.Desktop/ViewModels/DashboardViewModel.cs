using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Desktop.Services;
using FinXmlProcessor.Domain.Jobs;
using FinXmlProcessor.Infrastructure.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace FinXmlProcessor.Desktop.ViewModels;

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly IProcessingRepository _repository;
    private readonly IScheduleService _schedule;
    private readonly IOptionsMonitor<ScheduleOptions> _scheduleOptions;
    private readonly IOptionsMonitor<ProcessingOptions> _processingOptions;
    private readonly IProfileRegistry _profiles;
    private readonly IAppPaths _paths;
    private readonly IProcessingClock _clock;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;
    private readonly ILogger<DashboardViewModel> _logger;

    [ObservableProperty]
    private string _latestSummary = "No jobs yet.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestIsOk), nameof(LatestIsWarn), nameof(LatestIsError))]
    private string _latestStatusClass = "muted";

    [ObservableProperty]
    private string? _latestOutputPath;

    [ObservableProperty]
    private string? _latestReportPath;

    [ObservableProperty]
    private string _scheduleSummary = string.Empty;

    [ObservableProperty]
    private string _nextRunEastern = string.Empty;

    [ObservableProperty]
    private string _nextRunLocal = string.Empty;

    [ObservableProperty]
    private string _inputReadiness = string.Empty;

    [ObservableProperty]
    private string _inputFolder = string.Empty;

    [ObservableProperty]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    private string _activeProfileSummary = string.Empty;

    [ObservableProperty]
    private bool _activeProfileIsSynthetic;

    public DashboardViewModel(IProcessingRepository repository, IScheduleService schedule, IOptionsMonitor<ScheduleOptions> scheduleOptions, IOptionsMonitor<ProcessingOptions> processingOptions, IProfileRegistry profiles, IAppPaths paths, IProcessingClock clock, ProcessingRunner runner, IShellService shell, IDialogService dialogs, ILogger<DashboardViewModel> logger)
    {
        _repository = repository;
        _schedule = schedule;
        _scheduleOptions = scheduleOptions;
        _processingOptions = processingOptions;
        _profiles = profiles;
        _paths = paths;
        _clock = clock;
        Runner = runner;
        _shell = shell;
        _dialogs = dialogs;
        _logger = logger;
        Runner.Completed += (_, _) => _ = ActivateAsync();
    }

    public override string Title => "Dashboard";

    public ProcessingRunner Runner { get; }

    public bool LatestIsOk => LatestStatusClass == "status-ok";

    public bool LatestIsWarn => LatestStatusClass == "status-warn";

    public bool LatestIsError => LatestStatusClass == "status-error";

    public override Task ActivateAsync() => GuardAsync(RefreshAsync, _logger, null, "Refreshing the dashboard");

    private async Task RefreshAsync()
    {
        IReadOnlyList<ProcessingJob> jobs = await _repository.QueryJobsAsync(new JobQuery(Limit: 1), CancellationToken.None);
        ProcessingJob? latest = jobs.Count > 0 ? jobs[0] : null;
        if (latest is null)
        {
            LatestSummary = "No jobs yet. Use Process File or Run Now to get started.";
            LatestStatusClass = "muted";
            LatestOutputPath = null;
            LatestReportPath = null;
        }
        else
        {
            ProcessingCounts c = latest.Counts;
            LatestSummary = $"{latest.Status} — {latest.SourceFileName} on {latest.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}. Seen {c.RecordsSeen:N0}, accepted {c.RecordsAccepted:N0}, rejected {c.RecordsRejected:N0}, duplicates {c.RecordDuplicates:N0}.";
            LatestStatusClass = latest.Status switch
            {
                JobStatus.Completed or JobStatus.Delivered => "status-ok",
                JobStatus.CompletedWithWarnings or JobStatus.Delivering => "status-warn",
                _ => "status-error",
            };
            LatestOutputPath = latest.OutputPath;
            LatestReportPath = latest.ReportPath;
        }

        ScheduleOptions schedule = _scheduleOptions.CurrentValue;
        Instant now = _clock.GetCurrentInstant();
        ScheduledOccurrence next = _schedule.NextOccurrence(now);
        ScheduleSummary = schedule.Enabled ? $"Enabled: every day at {schedule.Time} America/New_York. Missed runs are caught up within {schedule.CatchUpWindowHours} hours." : "Scheduled processing is disabled. Enable it in Settings.";
        NextRunEastern = next.BusinessTime.ToString("dddd yyyy-MM-dd HH:mm 'Eastern' (o<g>)", CultureInfo.InvariantCulture);
        NextRunLocal = next.Instant.InZone(DateTimeZoneProviders.Tzdb.GetSystemDefault()).ToString("yyyy-MM-dd HH:mm 'on this Mac' (o<g>)", CultureInfo.InvariantCulture);

        ProcessingOptions processing = _processingOptions.CurrentValue;
        InputFolder = processing.InputDirectory ?? _paths.DefaultInput;
        OutputFolder = processing.OutputDirectory ?? _paths.DefaultOutput;
        int candidates = Directory.Exists(InputFolder) ? Directory.EnumerateFiles(InputFolder, processing.InputPattern).Count() : -1;
        InputReadiness = candidates switch
        {
            < 0 => "Input folder does not exist.",
            0 => "No XML files are waiting in the input folder.",
            1 => "1 XML file is waiting in the input folder.",
            _ => $"{candidates} XML files are waiting in the input folder (newest first).",
        };

        ProfileValidationResult active = await _profiles.GetByIdAsync(processing.ActiveProfileId, CancellationToken.None);
        ActiveProfileIsSynthetic = active.Profile?.IsSynthetic == true;
        ActiveProfileSummary = active.IsValid ? $"{active.Profile!.DisplayName} ({active.Profile.Id} {active.Profile.Version})" : $"Active profile '{processing.ActiveProfileId}' is not usable: {(active.Errors.Count > 0 ? active.Errors[0] : string.Empty)}";
    }

    [RelayCommand]
    private async Task RunNowAsync()
    {
        await GuardAsync(async () =>
        {
            ScheduledRunResult? result = await Runner.RunNowAsync();
            if (result is not null && !result.Ran)
            {
                await _dialogs.ShowMessageAsync("Nothing to process", result.Message);
            }
        }, _logger, _dialogs, "Run Now");
    }

    [RelayCommand]
    private void RevealOutput()
    {
        if (LatestOutputPath is not null)
        {
            _shell.Reveal(LatestOutputPath);
        }
    }

    [RelayCommand]
    private void RevealReport()
    {
        if (LatestReportPath is not null)
        {
            _shell.Reveal(LatestReportPath);
        }
    }

    [RelayCommand]
    private void OpenInputFolder()
    {
        Directory.CreateDirectory(InputFolder);
        _shell.Reveal(InputFolder);
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        Directory.CreateDirectory(OutputFolder);
        _shell.Reveal(OutputFolder);
    }
}
