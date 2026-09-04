using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Desktop.ViewModels;

public sealed class QuarantineRowViewModel
{
    public QuarantineRowViewModel(QuarantineEntry entry)
    {
        Entry = entry;
        When = entry.QuarantinedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        FileName = Path.GetFileName(entry.OriginalPath);
        Reason = $"[{entry.ReasonCode}] {entry.SanitizedReason}";
        Status = entry.Status;
        HasCopy = entry.QuarantinedPath is not null && File.Exists(entry.QuarantinedPath);
        Location = entry.QuarantinedPath ?? entry.OriginalPath;
    }

    public QuarantineEntry Entry { get; }

    public string When { get; }

    public string FileName { get; }

    public string Reason { get; }

    public string Status { get; }

    public bool HasCopy { get; }

    public string Location { get; }
}

public sealed partial class QuarantineViewModel : PageViewModel
{
    private readonly IQuarantineService _quarantine;
    private readonly IShellService _shell;
    private readonly IDialogService _dialogs;
    private readonly ILogger<QuarantineViewModel> _logger;

    [ObservableProperty]
    private QuarantineRowViewModel? _selected;

    public QuarantineViewModel(IQuarantineService quarantine, IShellService shell, IDialogService dialogs, ILogger<QuarantineViewModel> logger)
    {
        _quarantine = quarantine;
        _shell = shell;
        _dialogs = dialogs;
        _logger = logger;
    }

    public override string Title => "Quarantine";

    public ObservableCollection<QuarantineRowViewModel> Entries { get; } = [];

    public override Task ActivateAsync() => RefreshAsync();

    [RelayCommand]
    private Task RefreshAsync() => GuardAsync(async () =>
    {
        Entries.Clear();
        foreach (QuarantineEntry entry in await _quarantine.ListAsync(CancellationToken.None))
        {
            Entries.Add(new QuarantineRowViewModel(entry));
        }
    }, _logger, null, "Loading quarantine");

    [RelayCommand]
    private void Reveal(QuarantineRowViewModel? row)
    {
        if (row is not null)
        {
            _shell.Reveal(row.Location);
        }
    }

    [RelayCommand]
    private Task RestoreAsync(QuarantineRowViewModel? row) => GuardAsync(async () =>
    {
        if (row is null || !row.HasCopy)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync("Restore file", $"Move '{row.FileName}' back to the input folder so it can be processed again?", "Restore");
        if (ok)
        {
            await _quarantine.RestoreAsync(row.Entry.Id, CancellationToken.None);
            await RefreshAsync();
        }
    }, _logger, _dialogs, "Restore");

    [RelayCommand]
    private Task DeleteAsync(QuarantineRowViewModel? row) => GuardAsync(async () =>
    {
        if (row is null)
        {
            return;
        }

        bool ok = await _dialogs.ConfirmAsync("Delete quarantined copy", row.HasCopy ? $"Permanently delete the quarantined copy of '{row.FileName}'? The original external file (if any) is never touched." : $"Remove the quarantine record for '{row.FileName}'? No file will be deleted.", "Delete");
        if (ok)
        {
            await _quarantine.DeleteAsync(row.Entry.Id, CancellationToken.None);
            await RefreshAsync();
        }
    }, _logger, _dialogs, "Delete");
}
