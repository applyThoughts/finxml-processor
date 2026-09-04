using NodaTime;

namespace FinXmlProcessor.Application.Abstractions;

/// <summary>Thin wrapper so both DateTimeOffset-based persistence and NodaTime-based scheduling share one clock.</summary>
public interface IProcessingClock : IClock
{
    DateTimeOffset UtcNowOffset { get; }
}

public sealed class SystemProcessingClock : IProcessingClock
{
    public Instant GetCurrentInstant() => SystemClock.Instance.GetCurrentInstant();

    public DateTimeOffset UtcNowOffset => DateTimeOffset.UtcNow;
}
