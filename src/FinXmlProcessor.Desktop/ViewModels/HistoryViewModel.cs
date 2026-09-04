using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Application.Reports;
using FinXmlProcessor.Desktop.Services;
using FinXmlProcessor.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Desktop.ViewModels;

public sealed class JobRowViewModel
{
    public JobRowViewModel(ProcessingJob job)
    {
        Job = job;
        Id = job.Id;
        ShortId = job.Id.ToString("N")[..8];
        Created = job.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Source = job.SourceFileName;
        Status = job.Status.ToString();
        Trigger = job.Trigger;
        Profile = $"{job.ProfileId} {job.ProfileVersion}";
        Seen = job.Counts.RecordsSeen;
        Accepted = job.Counts.RecordsAccepted;
        Rejected = job.Counts.RecordsRejected;
        Duplicates = job.Counts.RecordDuplicates;
        Duration = job.StartedAt is DateTimeOffset s && job.FinishedAt is DateTimeOffset f ? (f - s).ToString(@"mm\:ss", CultureInfo.InvariantCulture) : "-";
        HasOutput = job.OutputPath is not null && File.Exists(job.OutputPath);
        HasReport = job.ReportPath is not null && File.Exists(job.ReportPath);
        CanRerun = job.Status.IsTerminal() || job.Status is JobStatus.Completed or JobStatus.CompletedWithWarnings;
    }

    public ProcessingJob Job { get; }

    public Guid Id { get; }

    public string ShortId { get; }

    public string Created { get; }

    public string Source { get; }

    public string Status { get; }

    public string Trigger { get; }

    public string Profile { get; }

    public long Seen { get; }

    public long Accepted { get; }

    public long Rejected { get; }

    public long Duplicates { get; }

    public string Duration { get; }

    public bool HasOutput { get; }

    public bool HasReport { get; }

    public bool CanRerun { get; }
}

public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly IProcessingRepository _repository;
    private readonly IReportWriter _reports;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;
    private readonly ILogger<HistoryViewModel> _logger;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private string _statusFilter = "All";

    [ObservableProperty]
    private JobRowViewModel? _selectedJob;

    [ObservableProperty]
    private string? _selectedReportText;

    public HistoryViewModel(IProcessingRepository repository, IReportWriter reports, ProcessingRunner runner, IShellService shell, IDialogService dialogs, ILogger<HistoryViewModel> logger)
    {
        _repository = repository;
        _reports = reports;
        Runner = runner;
        _shell = shell;
        _dialogs = dialogs;
        _logger = logger;
        Runner.Completed += (_, _) => _ = ActivateAsync();
    }

    public override string Title => "History";

    public ProcessingRunner Runner { get; }

    public ObservableCollection<JobRowViewModel> Jobs { get; } = [];

    public IReadOnlyList<string> StatusFilters { get; } = ["All", .. Enum.GetNames<JobStatus>()];

    public override Task ActivateAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => GuardAsync(async () =>
    {
        JobStatus? status = Enum.TryParse(StatusFilter, out JobStatus parsed) ? parsed : null;
        IReadOnlyList<ProcessingJob> jobs = await _repository.QueryJobsAsync(new JobQuery(500, status, string.IsNullOrWhiteSpace(FilterText) ? null : FilterText.Trim()), CancellationToken.None);
        Guid? selected = SelectedJob?.Id;
        Jobs.Clear();
        foreach (ProcessingJob job in jobs)
        {
            Jobs.Add(new JobRowViewModel(job));
        }

        SelectedJob = Jobs.FirstOrDefault(j => j.Id == selected);
    }, _logger, null, "Loading history");

    partial void OnSelectedJobChanged(JobRowViewModel? value)
    {
        _ = LoadReportAsync(value);
    }

    private async Task LoadReportAsync(JobRowViewModel? row)
    {
        SelectedReportText = null;
        if (row is null)
        {
            return;
        }

        try
        {
            ProcessingReport? report = row.HasReport ? await _reports.ReadAsync(row.Job.ReportPath!, CancellationToken.None) : null;
            SelectedReportText = report is null
                ? "Report not available." + (row.Job.Issues.Count > 0 ? "\nIssues:\n" + string.Join('\n', row.Job.Issues.Take(50).Select(i => $"  [{i.Severity}] {i.Code} {i.Message}")) : string.Empty)
                : _reports.RenderText(report);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load report for job {JobId}", row.Id);
            SelectedReportText = "Report could not be read.";
        }
    }

    [RelayCommand]
    private void RevealOutput(JobRowViewModel? row)
    {
        if (row?.Job.OutputPath is not null)
        {
            _shell.Reveal(row.Job.OutputPath);
        }
    }

    [RelayCommand]
    private void RevealReport(JobRowViewModel? row)
    {
        if (row?.Job.ReportPath is not null)
        {
            _shell.Reveal(row.Job.ReportPath);
        }
    }

    [RelayCommand]
    private Task RerunAsync(JobRowViewModel? row) => GuardAsync(async () =>
    {
        if (row is null)
        {
            return;
        }

        string? path = await _dialogs.PickFileAsync($"Choose the input file to rerun for job {row.ShortId} ({row.Source})", "XML files", "*.xml");
        if (path is null)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync("Forced rerun", $"Process '{Path.GetFileName(path)}' again with profile {row.Job.ProfileId}? Duplicate-content protection is bypassed and a new job linked to {row.ShortId} is created.", "Rerun");
        if (!ok)
        {
            return;
        }

        ProcessingResult? result = await Runner.RunFileAsync(new ProcessingRequest(path, row.Job.ProfileId, null, Force: true, Trigger: "manual", RerunOfJobId: row.Id));
        if (result is null)
        {
            await _dialogs.ShowMessageAsync("Busy", "Another job is already running.");
        }
    }, _logger, _dialogs, "Rerun");
}
