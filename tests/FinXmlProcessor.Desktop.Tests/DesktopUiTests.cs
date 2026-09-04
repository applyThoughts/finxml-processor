using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Desktop.Services;
using FinXmlProcessor.Desktop.ViewModels;
using FinXmlProcessor.Desktop.Views;
using FinXmlProcessor.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

[assembly: AvaloniaTestApplication(typeof(FinXmlProcessor.Desktop.Tests.TestAppBuilder))]

namespace FinXmlProcessor.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>Uses the real App styles without the desktop lifetime bootstrapping.</summary>
public sealed class HeadlessApp : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://FinXmlProcessor.Desktop/")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
    }
}

public sealed class DesktopHost : IAsyncDisposable
{
    public DesktopHost()
    {
        Root = Path.Combine(Path.GetTempPath(), "finxml-tests", "ui", Guid.NewGuid().ToString("N"));
        HostApplicationBuilder builder = FinXmlHost.CreateBuilder(["Processing:StabilityWindowMilliseconds=0"], console: false, Root);
        builder.Services.AddSingleton<ProcessingRunner>();
        builder.Services.AddSingleton<IShellService, ShellService>();
        builder.Services.AddSingleton<IDialogService>(new FakeDialogService());
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<ProcessFileViewModel>();
        builder.Services.AddTransient<HistoryViewModel>();
        builder.Services.AddTransient<QuarantineViewModel>();
        builder.Services.AddTransient<ProfilesViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        Host = builder.Build();
    }

    public IHost Host { get; }

    public string Root { get; }

    public static string DemoInputPath => Path.Combine(AppContext.BaseDirectory, "samples", "input", "demo-transactions.xml");

    public async Task InitializeAsync()
    {
        await Host.Services.GetRequiredService<IProcessingRepository>().InitializeAsync(CancellationToken.None);
        string profile = Path.Combine(AppContext.BaseDirectory, "samples", "profiles", "demo-fintech-v1.json");
        await Host.Services.GetRequiredService<IProfileRegistry>().EnsureBuiltInProfilesAsync(new Dictionary<string, string> { ["demo-fintech-v1.json"] = await File.ReadAllTextAsync(profile) }, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        Host.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private sealed class FakeDialogService : IDialogService
    {
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "OK", string cancelLabel = "Cancel") => Task.FromResult(true);

        public Task<string?> PickFileAsync(string title, string filterName, params string[] patterns) => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
    }
}

public class DesktopUiTests
{
    [AvaloniaFact]
    public async Task Main_window_shows_navigation_and_switches_pages()
    {
        await using var host = new DesktopHost();
        await host.InitializeAsync();
        var main = host.Host.Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = main };
        window.Show();
        await main.InitializeAsync();
        Dispatcher.RunJobs();

        main.Items.Select(i => i.Title).Should().Equal("Dashboard", "Process File", "History", "Quarantine", "Profiles", "Settings");
        main.CurrentPage.Should().BeOfType<DashboardViewModel>();
        window.GetVisualDescendants().OfType<ListBox>().Should().Contain(l => l.ItemCount == 6);

        main.Navigate("Settings");
        Dispatcher.RunJobs();
        main.CurrentPage.Should().BeOfType<SettingsViewModel>();
        var settings = (SettingsViewModel)main.CurrentPage!;
        await WaitUntilAsync(() => !settings.IsLoading);
        settings.ScheduleTime.Should().Be("19:00");
        settings.AgentSupported.Should().Be(OperatingSystem.IsMacOS());

        main.Navigate("Profiles");
        Dispatcher.RunJobs();
        var profiles = (ProfilesViewModel)main.CurrentPage!;
        await WaitUntilAsync(() => profiles.Profiles.Count > 0);
        profiles.Profiles.Should().Contain(p => p.Id == "demo-fintech-v1" && p.IsActive && p.IsSynthetic);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Process_file_page_runs_the_demo_file_and_history_shows_it()
    {
        await using var host = new DesktopHost();
        await host.InitializeAsync();
        var main = host.Host.Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = main };
        window.Show();
        await main.InitializeAsync();
        main.Navigate("Process File");
        Dispatcher.RunJobs();
        var page = (ProcessFileViewModel)main.CurrentPage!;
        await WaitUntilAsync(() => page.Profiles.Count > 0);

        page.AcceptDroppedFile(DesktopHost.DemoInputPath);
        page.OutputDirectory = Path.Combine(host.Root, "out");
        page.StartCommand.CanExecute(null).Should().BeTrue();
        await page.PreviewCommand.ExecuteAsync(null);
        page.PreviewText.Should().Contain("File OK");

        await page.StartCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !page.Runner.IsBusy && page.ResultText is not null);
        page.ResultText.Should().Contain("Completed with warnings");
        page.ResultIsWarn.Should().BeTrue();
        page.ResultOutputPath.Should().NotBeNull();
        File.Exists(page.ResultOutputPath).Should().BeTrue();

        main.Navigate("History");
        Dispatcher.RunJobs();
        var history = (HistoryViewModel)main.CurrentPage!;
        await WaitUntilAsync(() => history.Jobs.Count > 0);
        history.Jobs.Should().ContainSingle().Which.Status.Should().Be("CompletedWithWarnings");
        history.SelectedJob = history.Jobs[0];
        await WaitUntilAsync(() => history.SelectedReportText is not null);
        history.SelectedReportText.Should().Contain("Records:");
        window.Close();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 30_000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            Dispatcher.RunJobs();
            await Task.Delay(25);
        }
    }
}

internal static class Dispatcher
{
    public static void RunJobs() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}
