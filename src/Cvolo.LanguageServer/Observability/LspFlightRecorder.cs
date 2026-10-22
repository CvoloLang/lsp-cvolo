namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Metadata-only pretrigger Flight Recorder. It never reads, receives or stores
/// document URIs, source text, RPC parameters or arbitrary exception messages.
/// A fixed machine vocabulary is enforced at the recording boundary.
/// This ring is volatile: process termination loses its contents.
/// </summary>
internal sealed class LspFlightRecorder(Func<DateTimeOffset>? clock = null)
{
    internal const int MaxEvents = 128;
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(3);

    private readonly Func<DateTimeOffset> _utcNow = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private readonly List<FlightRecorderEvent> _events = [];
    private long _sequence;
    private int _overwritten;
    private long _incidentSequence;
    private string? _incidentId;
    private string? _incidentReason;

    // The transport must pass only these well-known protocol method strings.
    internal void OnTransportNotification(string? method)
    {
        var kind = method switch
        {
            "textDocument/didOpen" => "did_open",
            "textDocument/didChange" => "did_change",
            "textDocument/didClose" => "did_close",
            _ => null,
        };
        if (kind is not null)
            Add(kind, "transport_read", null, null);
    }

    internal void OnHandlerEntered(string kind, long? ingress)
    {
        if (IsDocumentKind(kind))
            Add(kind, "handler_entered", ingress, null);
    }

    internal void OnHandlerSettled(string kind, string outcome, long? ingress)
    {
        if (!IsDocumentKind(kind) || outcome is not ("applied" or "rejected" or "faulted"))
            return;
        Add(kind, "handler_settled", ingress, outcome);
        if (outcome == "faulted")
            MarkIncident("document_handler_faulted");
    }

    internal FlightRecorderTriggerAck Trigger(FlightRecorderTriggerRequest? request, bool initialized)
    {
        if (!initialized)
            return new(1, "rejected", "server_not_initialized", null, false);
        if (request?.ProtocolVersion != 1)
            return new(1, "rejected", "unsupported_protocol_version", null, false);
        if (request.Category is not ("definition" or "completion" or "highlighting"
            or "diagnostics" or "performance" or "other"))
            return new(1, "rejected", "unsupported_category", null, false);

        string id = MarkIncident("manual");
        return new(1, "captured_in_memory", null, id, false);
    }

    internal FlightRecorderSnapshot Inspect()
    {
        lock (_gate)
        {
            Prune(_utcNow());
            int before = _incidentId is null ? 0 :
                _events.Count(e => e.Sequence < _incidentSequence);
            bool triggerRetained = _incidentId is not null
                && _events.Any(e => e.Sequence == _incidentSequence);
            return new FlightRecorderSnapshot(
                1, _incidentId is null ? "untriggered" : "incident_observed_in_memory",
                _events.ToArray(), _events.Count, _overwritten > 0,
                _overwritten, _incidentId, _incidentReason,
                triggerRetained, before, SourceContentCaptured: false,
                DurableCheckpointAvailable: false,
                PreProcessHistoryRecoverable: false,
                TransportToHandlerCorrelationProven: false);
        }
    }

    private static bool IsDocumentKind(string kind)
        => kind is "did_open" or "did_change" or "did_close";

    private string MarkIncident(string reason)
    {
        lock (_gate)
        {
            var stamp = AddUnderLock("incident", "trigger", null, reason);
            _incidentSequence = stamp.Sequence;
            _incidentId = "incident-" + stamp.Sequence.ToString(
                "D6", System.Globalization.CultureInfo.InvariantCulture);
            _incidentReason = reason;
            return _incidentId;
        }
    }

    private void Add(string kind, string stage, long? ingress, string? outcome)
    {
        lock (_gate)
            AddUnderLock(kind, stage, ingress, outcome);
    }

    private FlightRecorderEvent AddUnderLock(
        string kind, string stage, long? ingress, string? outcome)
    {
        DateTimeOffset now = _utcNow();
        Prune(now);
        if (_events.Count >= MaxEvents)
        {
            _events.RemoveAt(0);
            _overwritten++;
        }

        // No user-supplied payload or timestamps from other processes.
        var entry = new FlightRecorderEvent(
            ++_sequence, now, kind, stage, ingress is > 0 ? ingress : null, outcome);
        _events.Add(entry);
        return entry;
    }

    private void Prune(DateTimeOffset now)
    {
        DateTimeOffset oldest = now - Retention;
        while (_events.Count != 0 && _events[0].ObservedAtUtc < oldest)
        {
            _events.RemoveAt(0);
            _overwritten++;
        }
    }
}

internal sealed class FlightRecorderTriggerRequest
{
    public int ProtocolVersion { get; set; }
    public string? Category { get; set; }
}

internal sealed record FlightRecorderTriggerAck(
    int ProtocolVersion, string Status, string? Reason,
    string? IncidentId, bool DurablySaved);

internal sealed record FlightRecorderEvent(
    long Sequence, DateTimeOffset ObservedAtUtc,
    string Kind, string Stage, long? IngressSequence, string? Outcome);

internal sealed record FlightRecorderSnapshot(
    int ProtocolVersion, string Status, IReadOnlyList<FlightRecorderEvent> Events,
    int RetainedEvents, bool HistoryPrefixTruncated, int EvictedEvents,
    string? IncidentId, string? IncidentReason,
    bool TriggerRetained, int RetainedPretriggerEvents,
    bool SourceContentCaptured, bool DurableCheckpointAvailable,
    bool PreProcessHistoryRecoverable, bool TransportToHandlerCorrelationProven);
