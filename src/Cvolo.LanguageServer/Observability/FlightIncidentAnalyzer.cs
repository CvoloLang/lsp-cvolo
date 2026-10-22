namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Deterministic, bounded analyzer of the existing metadata-only Flight
/// Recorder. No source contents, paths, exception strings or RPC payloads.
/// A temporal neighbor is never promoted to a root cause.
/// </summary>
internal static class FlightIncidentAnalyzer
{
    internal const int MaxIndexedIncidents = 16;
    internal const int MaxCandidatesPerIncident = 24;
    internal const int BeforeWindow = 16;
    internal const int AfterWindow = 8;

    internal static FlightIncidentIndex Analyze(FlightRecorderSnapshot snapshot)
    {
        var events = snapshot.Events.OrderBy(e => e.Sequence).ToArray();
        var triggers = events.Where(e => e.Kind == "incident"
            && e.Stage == "trigger").ToArray();
        int hidden = Math.Max(0, triggers.Length - MaxIndexedIncidents);
        var incidents = new List<FlightIncidentRecord>();
        foreach (var trigger in triggers.TakeLast(MaxIndexedIncidents))
        {
            int i = Array.FindIndex(events, e => e.Sequence == trigger.Sequence);
            if (i < 0) continue;

            int start = Math.Max(0, i - BeforeWindow);
            int end = Math.Min(events.Length, i + 1 + AfterWindow);
            bool windowTruncated = snapshot.HistoryPrefixTruncated
                || start > 0 || end < events.Length;
            FlightRecorderEvent? observedCause = null;

            // This precise adjacency is a construction invariant of
            // OnHandlerSettled("faulted") -> MarkIncident("document_handler_faulted").
            // It demonstrates WHY the marker was emitted, not WHY the handler failed.
            if (trigger.Outcome == "document_handler_faulted" && i > 0
                && events[i - 1] is { Kind: "did_open" or "did_change" or "did_close",
                                    Stage: "handler_settled", Outcome: "faulted" } fault)
                observedCause = fault;

            var candidateEvents = events.Skip(start).Take(end - start)
                .Where(e => e.Sequence != trigger.Sequence).ToArray();
            bool limited = candidateEvents.Length > MaxCandidatesPerIncident;
            if (limited)
                candidateEvents = candidateEvents
                    .OrderBy(e => Math.Abs((double)e.Sequence - trigger.Sequence))
                    .Take(MaxCandidatesPerIncident)
                    .OrderBy(e => e.Sequence).ToArray();

            var candidates = candidateEvents.Select(e =>
                new FlightIncidentEvidenceLink(
                    e.Sequence, e.IngressSequence, e.Kind, e.Stage,
                    observedCause?.Sequence == e.Sequence
                        ? "causal" : "temporal_only",
                    observedCause?.Sequence == e.Sequence
                        ? "automatic_trigger_source" : "within_incident_window"
                )).ToArray();

            incidents.Add(new FlightIncidentRecord(
                "incident-" + trigger.Sequence.ToString("D6",
                    System.Globalization.CultureInfo.InvariantCulture),
                trigger.Sequence, trigger.ObservedAtUtc,
                trigger.Outcome is "manual" or "document_handler_faulted"
                    ? trigger.Outcome : "unknown",
                candidates,
                observedCause?.Sequence,
                RootCause: "unknown",
                EvidenceComplete: !windowTruncated && !limited,
                WindowTruncated: windowTruncated || limited));
        }

        // Eviction means even the number of incident triggers that were
        // originally observed may no longer be knowable.
        return new FlightIncidentIndex(
            1, incidents,
            snapshot.HistoryPrefixTruncated || hidden > 0,
            hidden,
            "volatile_only",
            "no_root_cause_claimed",
            snapshot.SourceContentCaptured,
            TransportToHandlerCorrelationProven: false);
    }
}

internal sealed record FlightIncidentEvidenceLink(
    long EventSequence,
    long? IngressSequence,
    string Kind,
    string Stage,
    string Association,
    string Explanation);

internal sealed record FlightIncidentRecord(
    string IncidentId,
    long TriggerEventSequence,
    DateTimeOffset TriggeredAtUtc,
    string TriggerReason,
    IReadOnlyList<FlightIncidentEvidenceLink> Candidates,
    long? FirstObservedTriggerSource,
    string RootCause,
    bool EvidenceComplete,
    bool WindowTruncated);

internal sealed record FlightIncidentIndex(
    int ProtocolVersion,
    IReadOnlyList<FlightIncidentRecord> Incidents,
    bool IncidentHistoryIncomplete,
    int OmittedRetainedIncidents,
    string Durability,
    string RootCausePolicy,
    bool SourceContentCaptured,
    bool TransportToHandlerCorrelationProven);
