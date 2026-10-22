namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// An in-memory, consent-scoped record of a document notification's disposition.
/// The URI never leaves the private journal. An ingress ordinal belongs to ANY
/// inbound RPC frame and does not imply consecutive document edits.
/// </summary>
internal sealed record CapturedDocumentInput(
    string DocumentUri, string InputKind, string Outcome,
    int? Version, long? IngressSequence);

/// <summary>
/// A privacy-scoped handler-entry witness. It is NOT transport admission.
/// Entries may arrive out of order due to concurrent handler dispatch.
/// </summary>
internal sealed record CapturedDocumentHandlerEntry(
    string DocumentUri, string InputKind, int? Version, long? IngressSequence);

internal sealed record CaptureInputReconciliation(
    string? FirstIssue, long? FirstIssueIngressSequence,
    int ObservedInputs, int AppliedInputs, int RejectedInputs,
    int MatchedSnapshots, int UnknownIngressInputs,
    int HandlerEntries = 0, int UnsettledHandlerEntries = 0,
    int HandlerEntriesWithoutIngress = 0,
    int SettlementsWithoutObservedHandler = 0,
    int DuplicateHandlerEntries = 0);

/// <summary>
/// Independently reconciles server-settled document inputs with retained source
/// snapshots. It does not infer gaps from nonconsecutive global RPC ordinals.
/// Missing source or event evidence is never interpreted as a complete replay.
/// </summary>
internal static class CaptureInputReconciler
{
    internal static CaptureInputReconciliation Reconcile(
        IReadOnlyList<CapturedDocumentInput> inputs,
        IReadOnlyList<CapturedDocumentSnapshot> snapshots,
        IReadOnlyList<CapturedDocumentHandlerEntry>? entered = null)
    {
        var byIngress = new Dictionary<(string Uri, long Sequence), CapturedDocumentSnapshot>();
        var duplicateSnapshotKeys = new HashSet<(string Uri, long Sequence)>();
        foreach (var snapshot in snapshots)
        {
            if (snapshot.IngressSequence is not > 0)
                continue;
            var key = (snapshot.DocumentUri, snapshot.IngressSequence.Value);
            if (!byIngress.TryAdd(key, snapshot))
                duplicateSnapshotKeys.Add(key);
        }

        var observed = new HashSet<(string Uri, long Sequence)>();
        var lastByDocument = new Dictionary<string, long>(StringComparer.Ordinal);
        string? firstIssue = null;
        long? firstIssueIngress = null;
        int applied = 0, rejected = 0, matched = 0, unknown = 0;

        foreach (var input in inputs)
        {
            if (input.Outcome == "applied")
                applied++;
            else if (input.Outcome == "rejected")
                rejected++;

            if (input.IngressSequence is not > 0)
            {
                unknown++;
                continue;
            }

            long sequence = input.IngressSequence.Value;
            var key = (input.DocumentUri, sequence);
            string? issue = null;
            if (lastByDocument.TryGetValue(input.DocumentUri, out long previous)
                && sequence <= previous)
                issue = "non_increasing_document_input_ingress";
            lastByDocument[input.DocumentUri] = sequence;
            if (!observed.Add(key))
                issue ??= "duplicate_document_input_ingress";
            // Do not retroactively attribute a duplicate snapshot to the first input.
            // Evaluate duplicate snapshots only after processing the input sequence.

            bool hasSnapshot = byIngress.TryGetValue(key, out var snapshot);
            if (input.Outcome == "applied")
            {
                if (!hasSnapshot)
                    issue ??= "applied_input_snapshot_missing";
                else
                {
                    string expectedKind = snapshot!.IsDidClose ? "did_close"
                        : snapshot.IsDidOpen ? "did_open" : "did_change";
                    if (!string.Equals(expectedKind, input.InputKind, StringComparison.Ordinal)
                        || (input.InputKind != "did_close" && snapshot.Version != input.Version))
                        issue ??= "applied_input_snapshot_mismatch";
                    else
                        matched++;
                }
            }
            else if (hasSnapshot)
                issue ??= "non_applied_input_has_snapshot";
            else if (input.Outcome == "faulted")
                issue ??= "document_input_handler_faulted";

            if (firstIssue is null && issue is not null)
            {
                firstIssue = issue;
                firstIssueIngress = sequence;
            }
        }

        if (firstIssue is null && duplicateSnapshotKeys.Count > 0)
        {
            var seenSnapshotKeys = new HashSet<(string Uri, long Sequence)>();
            foreach (var snapshot in snapshots)
            {
                if (snapshot.IngressSequence is not > 0) continue;
                var key = (snapshot.DocumentUri, snapshot.IngressSequence.Value);
                if (!seenSnapshotKeys.Add(key))
                {
                    firstIssue = "duplicate_retained_snapshot_ingress";
                    firstIssueIngress = snapshot.IngressSequence;
                    break;
                }
            }
        }

        // A retained source snapshot with a known ingress must have a matching
        // applied input receipt when a receipt stream is present. This check is
        // conservative for legacy/direct test callers which provide no receipts.
        if (inputs.Count > 0 && firstIssue is null)
        {
            foreach (var snapshot in snapshots)
            {
                if (snapshot.IngressSequence is not > 0)
                    continue;
                var key = (snapshot.DocumentUri, snapshot.IngressSequence.Value);
                if (!observed.Contains(key))
                {
                    firstIssue = "snapshot_without_input_receipt";
                    firstIssueIngress = snapshot.IngressSequence;
                    break;
                }
            }
        }

        int unknownEntries = 0, unsettled = 0;
        int unmatchedSettlements = 0, duplicateEntries = 0;
        if (entered is not null)
        {
            // No list-order comparisons: the dispatch path is concurrent.
            // Match both identity and method. Version does not participate in
            // the key because rejected requests may carry invalid versions.
            var settlements = inputs.Where(i => i.IngressSequence is > 0)
                .Select(i => (i.DocumentUri, i.IngressSequence!.Value, i.InputKind))
                .ToHashSet();
            var entryKeys = new HashSet<(string DocumentUri, long Value, string InputKind)>();
            foreach (var entry in entered)
            {
                if (entry.IngressSequence is not > 0)
                    unknownEntries++;
                else
                {
                    var key = (entry.DocumentUri, entry.IngressSequence.Value, entry.InputKind);
                    if (!entryKeys.Add(key))
                    {
                        duplicateEntries++;
                        if (firstIssue is null)
                        {
                            firstIssue = "duplicate_document_handler_entry";
                            firstIssueIngress = entry.IngressSequence;
                        }
                    }
                    if (!settlements.Contains(key))
                        unsettled++;
                }
            }
            foreach (var settled in inputs)
                if (settled.IngressSequence is > 0 &&
                    !entryKeys.Contains((settled.DocumentUri,
                        settled.IngressSequence.Value, settled.InputKind)))
                    unmatchedSettlements++;
        }

        // Unsettled is a live/potentially interrupted operation, NOT proven
        // loss. A terminal input barrier would be required to distinguish them.
        return new(firstIssue, firstIssueIngress, inputs.Count,
            applied, rejected, matched, unknown,
            entered?.Count ?? 0, unsettled, unknownEntries,
            unmatchedSettlements, duplicateEntries);
    }
}
