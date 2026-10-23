namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Produces ONLY direct causal evidence from the bounded per-process Flight
/// Recorder. Does not infer a root cause, client-to-server frame identity,
/// inter-handler dependencies, or an end-to-end critical path.
/// </summary>
internal static class FlightCausalAnalyzer
{
    internal const int MaxEdges = LspFlightRecorder.MaxEvents;

    internal static FlightCausalEvidence Analyze(FlightRecorderSnapshot snapshot)
    {
        FlightRecorderEvent[] events = snapshot.Events
            .OrderBy(e => e.Sequence).ToArray();
        var edges = new List<FlightCausalEdge>();
        var entries = new Dictionary<(string Kind, long Ingress), List<FlightRecorderEvent>>();
        var settles = new Dictionary<(string Kind, long Ingress), List<FlightRecorderEvent>>();
        int unknownIngress = 0, invalidClock = 0, ambiguous = 0;
        int missingEntry = 0, missingSettlement = 0;
        int linkedFaults = 0;
        double? longestHandlerMs = null;
        long? longestIngress = null;

        // Pairing is by explicit handler kind + assigned ordinal. A duplicate
        // key cannot be disambiguated by timing or by nearest-neighbor guesses.
        foreach (var e in events)
        {
            if (e.Kind is not ("did_open" or "did_change" or "did_close")
                || e.Stage is not ("handler_entered" or "handler_settled"))
                continue;
            if (e.IngressSequence is not > 0)
            {
                unknownIngress++;
                continue;
            }

            var key = (e.Kind, e.IngressSequence.Value);
            var map = e.Stage == "handler_entered" ? entries : settles;
            if (!map.TryGetValue(key, out var records))
                map[key] = records = [];
            records.Add(e);
        }

        var keys = entries.Keys.Concat(settles.Keys).Distinct()
            .OrderBy(k => k.Ingress).ThenBy(k => k.Kind, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            entries.TryGetValue(key, out var begins);
            settles.TryGetValue(key, out var ends);
            int starts = begins?.Count ?? 0;
            int finishes = ends?.Count ?? 0;
            if (starts == 0)
            {
                missingEntry += finishes;
                continue;
            }
            if (finishes == 0)
            {
                missingSettlement += starts;
                continue;
            }
            if (starts != 1 || finishes != 1)
            {
                ambiguous += starts + finishes;
                continue;
            }
            var begin = begins![0];
            var end = ends![0];
            if (end.Sequence <= begin.Sequence
                || begin.MonotonicTicks is not >= 0
                || end.MonotonicTicks is not >= 0
                || end.MonotonicTicks < begin.MonotonicTicks)
            {
                invalidClock++;
                continue;
            }

            // A same-key handler entry and settlement is a documented local
            // lifecycle relation, not proof of transport admission.
            edges.Add(new(begin.Sequence, end.Sequence, key.Ingress,
                "handler_lifecycle", "directly_observed"));
            double elapsed = (end.MonotonicTicks.Value -
                (double)begin.MonotonicTicks.Value) * 1000 /
                System.Diagnostics.Stopwatch.Frequency;
            if (double.IsFinite(elapsed) && elapsed >= 0
                && (longestHandlerMs is null || elapsed > longestHandlerMs))
            {
                longestHandlerMs = elapsed;
                longestIngress = key.Ingress;
            }
        }

        for (int i = 1; i < events.Length; i++)
        {
            var trigger = events[i];
            var preceding = events[i - 1];
            // LspFlightRecorder now writes the fault settlement + trigger
            // under ONE lock, guaranteeing immediate event adjacency.
            if (trigger is not { Kind: "incident", Stage: "trigger",
                                 Outcome: "document_handler_faulted" }
                || preceding is not { Kind: "did_open" or "did_change" or "did_close",
                                     Stage: "handler_settled", Outcome: "faulted" }
                || trigger.Sequence != preceding.Sequence + 1)
                continue;
            edges.Add(new(preceding.Sequence, trigger.Sequence,
                preceding.IngressSequence, "automatic_fault_trigger",
                "atomic_emission_invariant"));
            linkedFaults++;
        }

        edges.Sort((a,b) => a.ToEventSequence != b.ToEventSequence
            ? a.ToEventSequence.CompareTo(b.ToEventSequence)
            : a.FromEventSequence.CompareTo(b.FromEventSequence));
        bool truncated = snapshot.HistoryPrefixTruncated;
        bool partial = truncated || unknownIngress > 0 || invalidClock > 0
            || ambiguous > 0 || missingEntry > 0 || missingSettlement > 0
            || events.Length == 0;
        return new FlightCausalEvidence(
            1, partial ? "partial" : "observed_relations_only",
            edges.Take(MaxEdges).ToArray(),
            entries.Count, settles.Count,
            edges.Count(e => e.Relation == "handler_lifecycle"),
            linkedFaults, ambiguous, missingEntry, missingSettlement,
            invalidClock, unknownIngress, longestHandlerMs, longestIngress,
            truncated || edges.Count > MaxEdges,
            "unknown", false, false, false, false);
    }
}

/// <summary>
/// The From/To event sequences are from the same bounded process-local ring.
/// No arbitrary identifiers, URIs or user-supplied strings.
/// </summary>
internal sealed record FlightCausalEdge(
    long FromEventSequence, long ToEventSequence,
    long? IngressSequence, string Relation, string Proof);

internal sealed record FlightCausalEvidence(
    int ProtocolVersion, string Coverage,
    IReadOnlyList<FlightCausalEdge> DirectEdges,
    int DistinctHandlerEntryKeys, int DistinctHandlerSettlementKeys,
    int MatchedHandlerLifecycleEdges, int LinkedAutomaticFaultTriggers,
    int AmbiguousDuplicateEvents, int MissingHandlerEntries,
    int MissingHandlerSettlements, int InvalidClockPairs,
    int UnknownIngressEvents, double? LongestObservedHandlerMs,
    long? LongestObservedHandlerIngress,
    bool HistoryOrEdgeBudgetTruncated,
    string RootCause, bool RootCauseProven,
    bool TransportToHandlerCorrelationProven,
    bool EndToEndCriticalPathProven,
    bool CompleteDocumentHistoryProven);
