using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FinXmlProcessor.Desktop.Views;

namespace FinXmlProcessor.Desktop.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "OK", string cancelLabel = "Cancel");

    Task<string?> PickFileAsync(string title, string filterName, params string[] patterns);

    Task<string?> PickFolderAsync(string title);

    Task<string?> SaveFileAsync(string title, string suggestedName);
}

public interface IShellService
{
    /// <summary>Opens the containing folder (or the folder itself) in Finder/Explorer.</summary>
    void Reveal(string path);

    void OpenUrl(string url);
}

public sealed class DialogService : IDialogService
{
    private readonly Func<Window?> _owner;

    public DialogService(Func<Window?> owner)
    {
        _owner = owner;
    }

    public Task ShowMessageAsync(string title, string message) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Window? owner = _owner();
            var dialog = new MessageDialog(title, message, "OK", null);
            if (owner is null)
            {
                dialog.Show();
                return;
            }

            await dialog.ShowDialog<bool>(owner);
        });

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "OK", string cancelLabel = "Cancel") =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Window? owner = _owner();
            if (owner is null)
            {
                return false;
            }

            var dialog = new MessageDialog(title, message, confirmLabel, cancelLabel);
            return await dialog.ShowDialog<bool>(owner);
        });

    public async Task<string?> PickFileAsync(string title, string filterName, params string[] patterns)
    {
        Window? owner = _owner();
        if (owner is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        Window? owner = _owner();
        if (owner is null)
        {
            return null;
        }

        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName)
    {
        Window? owner = _owner();
        if (owner is null)
        {
            return null;
        }

        IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, ShowOverwritePrompt = true });
        return file?.TryGetLocalPath();
    }
}

public sealed class ShellService : IShellService
{
    public void Reveal(string path)
    {
        string target = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { File.Exists(path) ? "-R" : target, File.Exists(path) ? path : string.Empty }, UseShellExecute = false });
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { File.Exists(path) ? $"/select,{path}" : target }, UseShellExecute = false });
            }
            else
            {
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { target }, UseShellExecute = false });
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best effort; the path is shown in the UI regardless.
        }
    }

    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
