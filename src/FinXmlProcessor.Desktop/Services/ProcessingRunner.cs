using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FinXmlProcessor.Application.Processing;
using FinXmlProcessor.Infrastructure.Scheduling;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Desktop.Services;

/// <summary>
/// Owns the single active job for the desktop UI. Work runs on the thread pool; progress is marshalled to the
/// UI thread at most every 250 ms so a million-record file cannot flood the dispatcher.
/// </summary>
public sealed partial class ProcessingRunner : ObservableObject
{
    private readonly ProcessingPipeline _pipeline;
    private readonly ScheduledRunCoordinator _coordinator;
    private readonly ILogger<ProcessingRunner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _idle;
    private long _lastProgressTicks;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentComplete), nameof(HasProgress), nameof(ProgressText))]
    private ProcessingProgress? _progress;

    [ObservableProperty]
    private ProcessingResult? _lastResult;

    [ObservableProperty]
    private string _statusText = "Idle";

    public ProcessingRunner(ProcessingPipeline pipeline, ScheduledRunCoordinator coordinator, ILogger<ProcessingRunner> logger)
    {
        _pipeline = pipeline;
        _coordinator = coordinator;
        _logger = logger;
    }

    public event EventHandler<ProcessingResult>? Completed;

    public double PercentComplete => Progress?.PercentComplete ?? 0;

    public bool HasProgress => Progress?.PercentComplete is not null;

    public string ProgressText => Progress is null
        ? string.Empty
        : $"{Progress.Phase}: seen {Progress.RecordsSeen:N0}, accepted {Progress.RecordsAccepted:N0}, rejected {Progress.RecordsRejected:N0}, duplicates {Progress.RecordDuplicates:N0}, rows {Progress.RowsWritten:N0}, elapsed {Progress.Elapsed.TotalSeconds:F0}s";

    public async Task<ProcessingResult?> RunFileAsync(ProcessingRequest request)
    {
        return await RunAsync(async ct => await _pipeline.RunAsync(request, CreateProgress(), ct).ConfigureAwait(false), $"Processing {Path.GetFileName(request.InputPath)}").ConfigureAwait(false);
    }

    /// <summary>Run Now: acquire the newest unprocessed input (SFTP if configured, else the input folder) and process it.</summary>
    public async Task<ScheduledRunResult?> RunNowAsync()
    {
        ScheduledRunResult? outcome = null;
        await RunAsync(async ct =>
        {
            outcome = await _coordinator.RunNowAsync(CreateProgress(), ct).ConfigureAwait(false);
            return outcome.Processing;
        }, "Run Now").ConfigureAwait(false);
        return outcome;
    }

    public void Cancel() => _cts?.Cancel();

    public async Task WaitForIdleAsync(TimeSpan timeout)
    {
        TaskCompletionSource? idle = _idle;
        if (idle is null)
        {
            return;
        }

        await Task.WhenAny(idle.Task, Task.Delay(timeout)).ConfigureAwait(false);
    }

    private async Task<ProcessingResult?> RunAsync(Func<CancellationToken, Task<ProcessingResult?>> work, string description)
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return null;
        }

        _cts = new CancellationTokenSource();
        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsBusy = true;
            Progress = null;
            StatusText = description;
        });

        ProcessingResult? result = null;
        try
        {
            result = await Task.Run(() => work(_cts.Token), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Desktop run failed unexpectedly");
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsBusy = false;
                LastResult = result;
                StatusText = result?.SanitizedMessage ?? "Idle";
                if (result is not null)
                {
                    Completed?.Invoke(this, result);
                }
            });
            _idle.TrySetResult();
            _gate.Release();
        }

        return result;
    }

    private IProgress<ProcessingProgress> CreateProgress() => new ThrottledProgress(this);

    private sealed class ThrottledProgress : IProgress<ProcessingProgress>
    {
        private readonly ProcessingRunner _owner;

        public ThrottledProgress(ProcessingRunner owner)
        {
            _owner = owner;
        }

        public void Report(ProcessingProgress value)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _owner._lastProgressTicks) < 250 && value.PercentComplete is < 100)
            {
                return;
            }

            Interlocked.Exchange(ref _owner._lastProgressTicks, now);
            Dispatcher.UIThread.Post(() => _owner.Progress = value, DispatcherPriority.Background);
        }
    }
}
