using FinXmlProcessor.Application.Abstractions;
using FinXmlProcessor.Infrastructure.Paths;
using Microsoft.Extensions.Options;
using NodaTime;

namespace FinXmlProcessor.Infrastructure.Tests;

/// <summary>A disposable per-test application root under the temp folder.</summary>
public sealed class TempRoot : IDisposable
{
    public TempRoot()
    {
        Paths = new AppPaths(Path.Combine(Path.GetTempPath(), "finxml-tests", "roots", Guid.NewGuid().ToString("N")));
        Paths.EnsureCreated();
    }

    public AppPaths Paths { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Paths.Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A clock the tests can move.</summary>
public sealed class FakeClock : IProcessingClock
{
    public FakeClock(Instant now)
    {
        Now = now;
    }

    public Instant Now { get; set; }

    public Instant GetCurrentInstant() => Now;

    public DateTimeOffset UtcNowOffset => Now.ToDateTimeOffset();

    public void Advance(Duration duration) => Now += duration;
}

public static class Options
{
    public static IOptionsMonitor<T> Monitor<T>(T value)
        where T : class
    {
        var monitor = Substitute.For<IOptionsMonitor<T>>();
        monitor.CurrentValue.Returns(value);
        return monitor;
    }
}
