using CommunityToolkit.Mvvm.ComponentModel;
using FinXmlProcessor.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Desktop.ViewModels;

public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public abstract string Title { get; }

    /// <summary>Called each time the page becomes visible. Implementations refresh their data.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;

    /// <summary>Runs an action, converting unexpected exceptions into a sanitized message with an error reference.</summary>
    protected async Task GuardAsync(Func<Task> action, ILogger logger, IDialogService? dialogs = null, string? context = null)
    {
        try
        {
            ErrorMessage = null;
            IsLoading = true;
            await action();
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            string reference = Guid.NewGuid().ToString("N")[..8];
            logger.LogError(ex, "UI action failed (reference {Reference}) {Context}", reference, context);
            ErrorMessage = $"{context ?? "The action"} failed ({ex.GetType().Name}). Error reference {reference}; export a diagnostic bundle from Settings if this persists.";
            if (dialogs is not null)
            {
                await dialogs.ShowMessageAsync("Something went wrong", ErrorMessage);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }
}
