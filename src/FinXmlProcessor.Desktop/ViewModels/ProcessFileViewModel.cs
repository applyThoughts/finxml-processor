using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Desktop.Services;
using FinXmlProcessor.Domain.Issues;
using FinXmlProcessor.Domain.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinXmlProcessor.Desktop.ViewModels;

public sealed partial class ProcessFileViewModel : PageViewModel
{
    private const int PreviewRecords = 200;
    private readonly IProfileRegistry _profiles;
    private readonly IInputValidator _inputValidator;
    private readonly IRecordReaderFactory _readerFactory;
    private readonly IEnumerable<IRecordMapperFactory> _mapperFactories;
    private readonly IOptionsMonitor<ProcessingOptions> _options;
    private readonly IAppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly ILogger<ProcessFileViewModel> _logger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PreviewCommand))]
    private string _inputPath = string.Empty;

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private bool _force;

    [ObservableProperty]
    private InstalledProfile? _selectedProfile;

    [ObservableProperty]
    private string? _previewText;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultIsOk), nameof(ResultIsWarn), nameof(ResultIsError))]
    private string _resultClass = "muted";

    [ObservableProperty]
    private string? _resultOutputPath;

    [ObservableProperty]
    private string? _resultReportPath;

    public ProcessFileViewModel(IProfileRegistry profiles, IInputValidator inputValidator, IRecordReaderFactory readerFactory, IEnumerable<IRecordMapperFactory> mapperFactories, IOptionsMonitor<ProcessingOptions> options, IAppPaths paths, ProcessingRunner runner, IDialogService dialogs, IShellService shell, ILogger<ProcessFileViewModel> logger)
    {
        _profiles = profiles;
        _inputValidator = inputValidator;
        _readerFactory = readerFactory;
        _mapperFactories = mapperFactories;
        _options = options;
        _paths = paths;
        Runner = runner;
        _dialogs = dialogs;
        _shell = shell;
        _logger = logger;
        Runner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProcessingRunner.IsBusy))
            {
                StartCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public override string Title => "Process File";

    public ProcessingRunner Runner { get; }

    public ObservableCollection<InstalledProfile> Profiles { get; } = [];

    public bool ResultIsOk => ResultClass == "status-ok";

    public bool ResultIsWarn => ResultClass == "status-warn";

    public bool ResultIsError => ResultClass == "status-error";

    public override Task ActivateAsync() => GuardAsync(async () =>
    {
        if (string.IsNullOrEmpty(OutputDirectory))
        {
            OutputDirectory = _options.CurrentValue.OutputDirectory ?? _paths.DefaultOutput;
        }

        string? selectedId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (InstalledProfile profile in await _profiles.ListAsync(CancellationToken.None))
        {
            if (profile.IsValid)
            {
                Profiles.Add(profile);
            }
        }

        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == (selectedId ?? _options.CurrentValue.ActiveProfileId)) ?? Profiles.FirstOrDefault();
    }, _logger, null, "Loading profiles");

    public void AcceptDroppedFile(string path)
    {
        InputPath = path;
        PreviewText = null;
        ResultText = null;
    }

    private bool CanStart() => !Runner.IsBusy && !string.IsNullOrWhiteSpace(InputPath) && SelectedProfile is not null;

    [RelayCommand]
    private async Task BrowseInputAsync()
    {
        string? path = await _dialogs.PickFileAsync("Choose the XML file to process", "XML files", "*.xml");
        if (path is not null)
        {
            AcceptDroppedFile(path);
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

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task PreviewAsync() => GuardAsync(async () =>
    {
        PreviewText = "Validating…";
        InputValidationResult validation = await _inputValidator.ValidateFileAsync(InputPath, CancellationToken.None);
        if (!validation.IsValid)
        {
            PreviewText = "Input rejected: " + string.Join(" ", validation.Issues.Select(i => $"[{i.Code}] {i.Message}"));
            return;
        }

        CompiledProfile profile = SelectedProfile!.Validation.Profile!;
        IRecordMapperFactory? factory = _mapperFactories.FirstOrDefault(f => f.MapperType == profile.MapperType);
        if (factory is null)
        {
            PreviewText = $"Mapper type '{profile.MapperType}' is not installed.";
            return;
        }

        IRecordMapper mapper = factory.Create(profile);
        long seen = 0, rejected = 0;
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            await Task.Run(async () =>
            {
                await using IRecordReader reader = _readerFactory.Create(InputPath, profile);
                await foreach (SourceRecordEnvelope record in reader.ReadRecordsAsync(CancellationToken.None))
                {
                    seen++;
                    MappedRecord mapped = mapper.Map(record);
                    if (mapped.IsRejected)
                    {
                        rejected++;
                        foreach (RecordIssue issue in mapped.Issues.Where(i => i.Severity >= IssueSeverity.RecordRejected))
                        {
                            codes[issue.Code] = codes.TryGetValue(issue.Code, out int n) ? n + 1 : 1;
                        }
                    }

                    if (seen >= PreviewRecords)
                    {
                        break;
                    }
                }
            });
        }
        catch (ProcessingFatalException ex)
        {
            PreviewText = $"Input cannot be processed: [{ex.Code}] {ex.Message}";
            return;
        }

        string breakdown = codes.Count == 0 ? string.Empty : " Rejection codes: " + string.Join(", ", codes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ×{kv.Value}")) + ".";
        PreviewText = $"File OK ({validation.SizeBytes:N0} bytes, sha256 {validation.Sha256![..12]}…). First {seen:N0} record(s): {seen - rejected:N0} would be accepted, {rejected:N0} rejected by mapping.{breakdown} Field validation and duplicate checks run during processing.";
    }, _logger, _dialogs, "Validation preview");

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => GuardAsync(async () =>
    {
        ResultText = null;
        ResultOutputPath = null;
        ResultReportPath = null;
        var request = new ProcessingRequest(InputPath, SelectedProfile!.Id, string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory, Force, Trigger: "manual");
        ProcessingResult? result = await Runner.RunFileAsync(request);
        if (result is null)
        {
            ResultText = "Another job is already running.";
            ResultClass = "status-warn";
            return;
        }

        ResultText = result.SanitizedMessage;
        ResultClass = result.Outcome switch
        {
            ProcessingOutcome.Completed => "status-ok",
            ProcessingOutcome.CompletedWithWarnings => "status-warn",
            _ => "status-error",
        };
        ResultOutputPath = result.OutputPath;
        ResultReportPath = result.ReportPath;
    }, _logger, _dialogs, "Processing");

    private bool CanCancel() => Runner.IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => Runner.Cancel();

    [RelayCommand]
    private void RevealOutput()
    {
        if (ResultOutputPath is not null)
        {
            _shell.Reveal(ResultOutputPath);
        }
    }

    [RelayCommand]
    private void RevealReport()
    {
        if (ResultReportPath is not null)
        {
            _shell.Reveal(ResultReportPath);
        }
    }
}
