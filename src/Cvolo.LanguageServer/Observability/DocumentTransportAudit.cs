namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Privacy-safe independent document transport/handler accounting.
/// A read is observed by HeaderDelimitedMessageHandler, while handler entry
/// and settlement are recorded by TextDocumentSyncHandler. Neither side
/// reads or retains document URI, request arguments or source contents.
///
/// IMPORTANT: StreamJsonRpc's SynchronizationContext.Post does not identify
/// its source frame. Comparing process-wide COUNTS is never proof that any
/// individual transport frame reached a particular handler, and even equal
/// counts cannot prove a complete producer history or replay.
/// This audit never affects admission, queue order or RPC dispatch.
/// </summary>
internal sealed class DocumentTransportAudit(long maxObservationsPerLane = 65_536)
{
    private readonly object _gate = new();
    private readonly long[] _reads = new long[3];
    private readonly long[] _entries = new long[3];
    private readonly long[] _settlements = new long[3];
    private readonly long _limit = maxObservationsPerLane > 0
        ? maxObservationsPerLane : throw new ArgumentOutOfRangeException(nameof(maxObservationsPerLane));
    private bool _overflowed;

    internal void OnTransportRead(string? method)
    {
        int index = method switch
        {
            "textDocument/didOpen" => 0,
            "textDocument/didChange" => 1,
            "textDocument/didClose" => 2,
            _ => -1,
        };
        if (index >= 0)
            Increment(_reads, index);
    }

    internal void OnHandlerEntered(string? kind)
    {
        int index = Index(kind);
        if (index >= 0)
            Increment(_entries, index);
    }

    internal void OnHandlerSettled(string? kind, string? outcome)
    {
        if (outcome is not ("applied" or "rejected" or "faulted"))
            return;
        int index = Index(kind);
        if (index >= 0)
            Increment(_settlements, index);
    }

    internal DocumentTransportAuditEvidence Inspect()
    {
        lock (_gate)
        {
            var lanes = new[]
            {
                Lane(0, "did_open"), Lane(1, "did_change"), Lane(2, "did_close"),
            };
            bool sameCounts = lanes.All(l => l.TransportReads == l.HandlerEntries
                && l.HandlerEntries == l.HandlerSettlements);
            return new DocumentTransportAuditEvidence(
                1, "process_unscoped_metadata",
                "counts_only", lanes,
                _reads.Sum(), _entries.Sum(), _settlements.Sum(),
                sameCounts && !_overflowed,
                _overflowed,
                // A gap may be an in-flight callback, a malformed/unhandled
                // notification, direct test invocation or instrumentation loss.
                // No read/dispatch pair is authenticated by this ledger.
                "unclassified_in_flight_or_unobserved",
                false, false, false, false);
        }
    }

    private DocumentTransportAuditLane Lane(int i, string kind)
        => new(kind, _reads[i], _entries[i], _settlements[i],
            Math.Max(0L, _reads[i] - _entries[i]),
            Math.Max(0L, _entries[i] - _reads[i]),
            Math.Max(0L, _entries[i] - _settlements[i]),
            Math.Max(0L, _settlements[i] - _entries[i]));

    private void Increment(long[] counters, int index)
    {
        lock (_gate)
        {
            if (counters[index] < _limit)
                counters[index]++;
            else
                _overflowed = true;
        }
    }

    private static int Index(string? kind) => kind switch
    {
        "did_open" => 0,
        "did_change" => 1,
        "did_close" => 2,
        _ => -1,
    };
}

internal sealed record DocumentTransportAuditLane(
    string Kind, long TransportReads, long HandlerEntries,
    long HandlerSettlements, long ReadsWithoutObservedEntry,
    long EntriesWithoutObservedRead, long EntriesWithoutObservedSettlement,
    long SettlementsWithoutObservedEntry);

internal sealed record DocumentTransportAuditEvidence(
    int ProtocolVersion, string Scope, string CorrelationLevel,
    IReadOnlyList<DocumentTransportAuditLane> Lanes,
    long TransportReads, long HandlerEntries, long HandlerSettlements,
    bool CountersConcordantAtObservation, bool CounterOverflowed,
    string GapInterpretation,
    bool IndividualFrameIdentityProven,
    bool ClosedTransportProducerProven,
    bool DocumentReplayCompleteProven,
    bool UnmatchedCountersProveDroppedFrames);
