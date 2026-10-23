namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Evidence-only per-handler latency statistics from same-process monotonic
/// timestamps. No inferred causal edges or end-to-end client request latency.
/// These metrics cover only retained matched handler events, not all RPC work.
/// </summary>
internal static class FlightPerformanceAnalyzer
{
    internal static FlightPerformanceEvidence Analyze(FlightRecorderSnapshot snapshot)
    {
        var started = new Dictionary<(string Kind, long Ingress), long>();
        var intervals = new List<(long Start, long End)>();
        var durationMs = new List<double>();
        int entries = 0, settlements = 0, unknownIngress = 0;
        int duplicateEntries = 0, unmatchedSettlements = 0, invalidClock = 0;
        int faults = 0, rejected = 0;

        foreach (FlightRecorderEvent e in snapshot.Events.OrderBy(e => e.Sequence))
        {
            if (e.Kind is not ("did_open" or "did_change" or "did_close"))
                continue;
            if (e.Stage is not ("handler_entered" or "handler_settled"))
                continue;

            bool entering = e.Stage == "handler_entered";
            if (entering) entries++;
            else
            {
                settlements++;
                if (e.Outcome == "faulted") faults++;
                if (e.Outcome == "rejected") rejected++;
            }

            if (e.IngressSequence is not > 0 || e.MonotonicTicks is not >= 0)
            {
                unknownIngress++;
                continue;
            }

            var key = (e.Kind, e.IngressSequence.Value);
            if (entering)
            {
                if (!started.TryAdd(key, e.MonotonicTicks.Value))
                    duplicateEntries++;
                continue;
            }

            if (!started.Remove(key, out long start))
            {
                unmatchedSettlements++;
                continue;
            }

            long end = e.MonotonicTicks.Value;
            if (end < start)
            {
                invalidClock++;
                continue;
            }
            double ms = (end - (double)start) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
            if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0)
            {
                invalidClock++;
                continue;
            }
            intervals.Add((start, end));
            durationMs.Add(ms);
        }

        double observedBusyMs = 0;
        long? rangeStart = null, rangeEnd = null;
        foreach (var (start, end) in intervals.OrderBy(i => i.Start).ThenBy(i => i.End))
        {
            if (rangeStart is null)
            {
                rangeStart = start;
                rangeEnd = end;
            }
            else if (start <= rangeEnd)
            {
                rangeEnd = Math.Max(rangeEnd.Value, end);
            }
            else
            {
                observedBusyMs += (rangeEnd.Value - (double)rangeStart.Value)
                    * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                rangeStart = start;
                rangeEnd = end;
            }
        }
        if (rangeStart is not null)
            observedBusyMs += (rangeEnd!.Value - (double)rangeStart.Value)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        durationMs.Sort();
        double? max = durationMs.Count == 0 ? null : durationMs[^1];
        double? p95 = durationMs.Count == 0 ? null :
            durationMs[(int)Math.Ceiling(durationMs.Count * 0.95) - 1];
        bool incomplete = snapshot.HistoryPrefixTruncated
            || started.Count > 0 || unknownIngress > 0
            || duplicateEntries > 0 || unmatchedSettlements > 0
            || invalidClock > 0 || durationMs.Count == 0;
        return new FlightPerformanceEvidence(
            1, incomplete ? "partial" : "observed_only",
            entries, settlements, durationMs.Count,
            started.Count, unmatchedSettlements, unknownIngress,
            duplicateEntries, invalidClock, faults, rejected,
            durationMs.Sum(), observedBusyMs, max, p95,
            SnapshotHistoryTruncated: snapshot.HistoryPrefixTruncated,
            MonotonicClockScope: "same_process_only",
            EndToEndLatencyProven: false,
            CausalCriticalPathProven: false,
            AllOperationsCapturedProven: false);
    }
}

internal sealed record FlightPerformanceEvidence(
    int ProtocolVersion, string Coverage,
    int HandlerEntries, int HandlerSettlements,
    int MatchedDurations, int UnsettledHandlerEntries,
    int UnmatchedSettlements, int UnknownIngressOrClock,
    int DuplicateEntries, int InvalidClockPairs,
    int FaultedSettlements, int RejectedSettlements,
    double SumObservedHandlerTimeMs, double ObservedBusyTimeMs,
    double? MaxHandlerTimeMs, double? P95HandlerTimeMs,
    bool SnapshotHistoryTruncated,
    string MonotonicClockScope,
    bool EndToEndLatencyProven, bool CausalCriticalPathProven,
    bool AllOperationsCapturedProven);
