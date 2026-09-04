using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Application.Profiles;
using FinXmlProcessor.Infrastructure.Hosting;
using FinXmlProcessor.Infrastructure.Paths;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FinXmlProcessor.IntegrationTests;

/// <summary>Composes the real host against a throw-away application root with fast test settings.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public TestHost(params string[] extraArgs)
    {
        Root = Path.Combine(Path.GetTempPath(), "finxml-tests", "hosts", Guid.NewGuid().ToString("N"));
        string[] args =
        [
            "Processing:StabilityWindowMilliseconds=0",
            "Processing:ProgressIntervalMilliseconds=1",
            .. extraArgs,
        ];
        HostApplicationBuilder builder = FinXmlHost.CreateBuilder(args, console: false, Root);
        Host = builder.Build();
        Paths = Host.Services.GetRequiredService<AppPaths>();
    }

    public IHost Host { get; }

    public AppPaths Paths { get; }

    public string Root { get; }

    public static string DemoProfilePath => Path.Combine(AppContext.BaseDirectory, "samples", "profiles", "demo-fintech-v1.json");

    public static string DemoInputPath => Path.Combine(AppContext.BaseDirectory, "samples", "input", "demo-transactions.xml");

    public T Get<T>()
        where T : notnull => Host.Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        await Get<IProcessingRepository>().InitializeAsync(CancellationToken.None);
        await Get<IProfileRegistry>().EnsureBuiltInProfilesAsync(new Dictionary<string, string> { ["demo-fintech-v1.json"] = await File.ReadAllTextAsync(DemoProfilePath) }, CancellationToken.None);
    }

    /// <summary>Copies the demo input into the managed input folder so quarantine may move it.</summary>
    public string StageDemoInput(string? name = null)
    {
        string target = Path.Combine(Paths.DefaultInput, name ?? "demo-transactions.xml");
        File.Copy(DemoInputPath, target, overwrite: true);
        return target;
    }

    public async ValueTask DisposeAsync()
    {
        Host.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        await Task.CompletedTask;
    }
}
