using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Desktop.Services;
using FinXmlProcessor.Desktop.ViewModels;
using FinXmlProcessor.Desktop.Views;
using FinXmlProcessor.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Desktop;

public partial class App : Avalonia.Application
{
    private IHost? _host;

    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not built.");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            HostApplicationBuilder builder = FinXmlHost.CreateBuilder(desktop.Args ?? [], console: false);
            builder.Services.AddSingleton<ProcessingRunner>();
            builder.Services.AddSingleton<IShellService, ShellService>();
            builder.Services.AddSingleton<IDialogService>(sp => new DialogService(() => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow));
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddTransient<DashboardViewModel>();
            builder.Services.AddTransient<ProcessFileViewModel>();
            builder.Services.AddTransient<HistoryViewModel>();
            builder.Services.AddTransient<QuarantineViewModel>();
            builder.Services.AddTransient<ProfilesViewModel>();
            builder.Services.AddTransient<SettingsViewModel>();
            _host = builder.Build();

            var main = Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = main };
            desktop.ShutdownRequested += OnShutdownRequested;
            desktop.Exit += (_, _) => _host?.Dispose();
            _ = InitializeAsync(main);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task InitializeAsync(MainViewModel main)
    {
        try
        {
            await Services.GetRequiredService<IProcessingRepository>().InitializeAsync(CancellationToken.None);
            string samples = Path.Combine(AppContext.BaseDirectory, "samples", "profiles");
            if (Directory.Exists(samples))
            {
                var builtIn = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string file in Directory.EnumerateFiles(samples, "*.json"))
                {
                    builtIn[Path.GetFileName(file)] = await File.ReadAllTextAsync(file);
                }

                await Services.GetRequiredService<IProfileRegistry>().EnsureBuiltInProfilesAsync(builtIn, CancellationToken.None);
            }

            string? appearance = await Services.GetRequiredService<IProcessingRepository>().GetSettingAsync("appearance", CancellationToken.None);
            ApplyTheme(appearance);
            await main.InitializeAsync();
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<ILogger<App>>().LogError(ex, "Startup initialisation failed");
            await Services.GetRequiredService<IDialogService>().ShowMessageAsync("Startup problem", $"The application could not finish initialising ({ex.GetType().Name}). Check the log folder for details.");
        }
    }

    public static void ApplyTheme(string? appearance)
    {
        if (Current is null)
        {
            return;
        }

        Current.RequestedThemeVariant = appearance switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        var runner = Services.GetRequiredService<ProcessingRunner>();
        if (!runner.IsBusy)
        {
            return;
        }

        e.Cancel = true;
        _ = ConfirmShutdownAsync(runner);
    }

    private async Task ConfirmShutdownAsync(ProcessingRunner runner)
    {
        var dialogs = Services.GetRequiredService<IDialogService>();
        bool cancelJob = await dialogs.ConfirmAsync("A job is still running", "Cancel the running job and quit? No output will be published for a cancelled job. Choose Keep running to leave the job going.", "Cancel job and quit", "Keep running");
        if (!cancelJob)
        {
            return;
        }

        runner.Cancel();
        await runner.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested -= OnShutdownRequested;
            desktop.Shutdown();
        }
    }
}
