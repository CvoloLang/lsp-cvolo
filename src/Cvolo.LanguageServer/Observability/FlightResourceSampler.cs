using System.Diagnostics;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Metadata-only same-process resource snapshot for exploratory performance
/// measurements. All counters are process-wide; two reads bound an interval
/// but cannot attribute resource usage to one RPC, telemetry mode or thread.
/// Sampling is outside the benchmarked request loop.
/// </summary>
internal static class FlightResourceSampler
{
    internal static FlightResourceSnapshot Capture()
    {
        // Capture monotonic clock before resource probes; the sampled fields
        // are not an atomic OS snapshot, so no exact concurrency claim.
        long ticks = Stopwatch.GetTimestamp();
        using Process process = Process.GetCurrentProcess();
        return new(
            ProtocolVersion: 1,
            Scope: "same_process_cumulative_non_atomic",
            MonotonicTicks: ticks,
            MonotonicFrequency: Stopwatch.Frequency,
            ProcessCpuTimeTicks: process.TotalProcessorTime.Ticks,
            TotalAllocatedBytes: GC.GetTotalAllocatedBytes(precise: false),
            ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
            WorkingSetBytes: process.WorkingSet64,
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            ResourceAttributionToRequestProven: false,
            IsolatedCpuMeasurementProven: false,
            PeakMemoryObserved: false);
    }
}

internal sealed record FlightResourceSnapshot(
    int ProtocolVersion, string Scope,
    long MonotonicTicks, long MonotonicFrequency,
    long ProcessCpuTimeTicks, long TotalAllocatedBytes,
    long ManagedHeapBytes, long WorkingSetBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections,
    bool ResourceAttributionToRequestProven,
    bool IsolatedCpuMeasurementProven,
    bool PeakMemoryObserved);
