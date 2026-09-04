using System.Diagnostics;

namespace FinXmlProcessor.Infrastructure.Diagnostics;

public sealed record MemoryMeasurement(long PeakWorkingSetBytes, long PeakManagedHeapBytes, long TotalAllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections, TimeSpan Elapsed);

/// <summary>Samples the process working set and managed heap on a background timer. Used by the benchmark command and tests.</summary>
public sealed class PeakMemorySampler : IDisposable
{
    private readonly Timer _timer;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly long _allocatedAtStart = GC.GetTotalAllocatedBytes(precise: false);
    private readonly int _gen0AtStart = GC.CollectionCount(0);
    private readonly int _gen1AtStart = GC.CollectionCount(1);
    private readonly int _gen2AtStart = GC.CollectionCount(2);
    private long _peakWorkingSet;
    private long _peakManaged;

    public PeakMemorySampler(TimeSpan? interval = null)
    {
        Sample();
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, interval ?? TimeSpan.FromMilliseconds(200));
    }

    public MemoryMeasurement Measure()
    {
        Sample();
        return new MemoryMeasurement(
            Interlocked.Read(ref _peakWorkingSet),
            Interlocked.Read(ref _peakManaged),
            GC.GetTotalAllocatedBytes(precise: false) - _allocatedAtStart,
            GC.CollectionCount(0) - _gen0AtStart,
            GC.CollectionCount(1) - _gen1AtStart,
            GC.CollectionCount(2) - _gen2AtStart,
            _stopwatch.Elapsed);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _stopwatch.Stop();
    }

    private void Sample()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long ws = process.WorkingSet64;
            long managed = GC.GetTotalMemory(forceFullCollection: false);
            long current;
            while (ws > (current = Interlocked.Read(ref _peakWorkingSet)) && Interlocked.CompareExchange(ref _peakWorkingSet, ws, current) != current)
            {
            }

            while (managed > (current = Interlocked.Read(ref _peakManaged)) && Interlocked.CompareExchange(ref _peakManaged, managed, current) != current)
            {
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
