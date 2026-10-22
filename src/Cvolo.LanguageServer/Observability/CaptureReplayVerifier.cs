using System.Security.Cryptography;
using System.Text;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Checks only retained immutable full-text editor snapshots. This is not
/// full document-input replay: missing pre-arm didOpen, unrecorded UTF-16 edit
/// batches and gaps in the LSP notification stream cannot be reconstructed.
/// No document URI, source text or user-defined metadata leaves this verifier.
/// </summary>
internal static class CaptureReplayVerifier
{
    internal const string DigestAlgorithm = "sha256-utf16le";

    internal static string ComputeDigest(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(text)))
            .ToLowerInvariant();

    internal static CaptureReplayEvidence Inspect(
        IReadOnlyList<CapturedDocumentSnapshot> snapshots, bool journalIncomplete,
        IReadOnlyList<CapturedDocumentInput>? inputs = null,
        bool inputLedgerIncomplete = false,
        IReadOnlyList<CapturedDocumentHandlerEntry>? entered = null,
        CaptureTransportWitness? transportWitness = null)
    {
        var perDocument = new Dictionary<string, DocumentEvidenceBuilder>(StringComparer.Ordinal);
        var entries = new List<CaptureSnapshotEvidence>(snapshots.Count);
        string? firstIssue = null;

        foreach (CapturedDocumentSnapshot snapshot in snapshots)
        {
            if (!perDocument.TryGetValue(snapshot.DocumentUri, out var doc))
            {
                doc = new DocumentEvidenceBuilder("doc-" + (perDocument.Count + 1)
                    .ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
                perDocument.Add(snapshot.DocumentUri, doc);
            }

            string? issue = null;
            if (snapshot.IngressSequence is { } ingress)
            {
                if (doc.LastIngressSequence is { } previousIngress && ingress <= previousIngress)
                    issue = "non_increasing_document_ingress";
                doc.LastIngressSequence = ingress;
            }
            // Gaps are normal: ingress ordinals include unrelated RPC frames.
            if (snapshot.IsDidClose)
            {
                if (!doc.HasObservedBaseline)
                    issue ??= "close_without_observed_open";
                doc.HasObservedBaseline = false;
                doc.LastText = null;
                doc.LastVersion = null;
                doc.Count++;
                doc.AnyIssue |= issue is not null;
                firstIssue ??= issue;
                entries.Add(new CaptureSnapshotEvidence(doc.Id, snapshot.Version,
                    "did_close", 0, snapshot.Sha256Utf16Le, issue, null,
                    snapshot.IngressSequence, doc.SessionId));
                doc.SessionId = null;
                continue;
            }
            if (!string.Equals(
                ComputeDigest(snapshot.Text), snapshot.Sha256Utf16Le,
                StringComparison.Ordinal))
            {
                issue ??= "snapshot_digest_mismatch";
            }
            else if (snapshot.IsDidOpen)
            {
                doc.Segments++;
                doc.SessionId = doc.Id + "/session-" +
                    doc.Segments.ToString("D4",
                        System.Globalization.CultureInfo.InvariantCulture);
                doc.HasObservedBaseline = true;
                doc.LastVersion = null;
            }
            else if (!doc.HasObservedBaseline)
            {
                issue ??= "did_open_not_observed";
            }

            if (!snapshot.IsDidOpen
                && doc.LastVersion is { } previous
                && snapshot.Version <= previous)
                issue ??= "non_increasing_document_version";

            if (!snapshot.IsDidOpen)
            {
                if (snapshot.AppliedChanges is null)
                    issue ??= "edit_batch_not_retained";
                else if (doc.LastText is not null)
                {
                    // Independently replay accepted LSP UTF-16 edits using an
                    // implementation separate from DocumentStore and its editor.
                    // This checks
                    // CRLF, surrogate pairs and multi-edit batches agree.
                    if (!CaptureEditReplayer.TryReplay(
                        doc.LastText, snapshot.AppliedChanges, out string reproduced))
                        issue ??= "replay_invalid_utf16_range";
                    else if (!string.Equals(reproduced, snapshot.Text,
                        StringComparison.Ordinal))
                        issue ??= "replayed_text_mismatch";
                    else if (issue is null)
                        doc.VerifiedEdits++;
                }
            }

            doc.LastText = snapshot.Text;
            doc.LastVersion = snapshot.Version;
            doc.Count++;
            doc.AnyIssue |= issue is not null;
            firstIssue ??= issue;
            entries.Add(new CaptureSnapshotEvidence(
                doc.Id, snapshot.Version,
                snapshot.IsDidOpen ? "did_open" : "did_change",
                snapshot.Text.Length, snapshot.Sha256Utf16Le, issue,
                snapshot.AppliedChanges?.Count, snapshot.IngressSequence,
                doc.SessionId));
        }

        var documents = perDocument.Values.Select(d => new CaptureDocumentEvidence(
            d.Id, d.Count, d.Segments, d.HasObservedBaseline,
            d.LastVersion, d.AnyIssue, d.VerifiedEdits)).ToArray();
        CaptureInputReconciliation reconciliation = CaptureInputReconciler.Reconcile(
            inputs ?? Array.Empty<CapturedDocumentInput>(), snapshots, entered);
        bool problems = firstIssue is not null || reconciliation.FirstIssue is not null;
        // Compute the earliest KNOWN ingress violation across independent
        // snapshot and input ledgers. An unknown sequence cannot be ordered,
        // so the "all orders known" flag must remain false in that case.
        var candidates = entries
            .Where(e => e.Issue is not null)
            .Select(e => new CaptureFirstKnownFailure(
                e.Issue!, e.IngressSequence, e.Version, "snapshot"))
            .ToList();
        if (reconciliation.FirstIssue is not null)
            candidates.Add(new CaptureFirstKnownFailure(
                reconciliation.FirstIssue,
                reconciliation.FirstIssueIngressSequence, null, "input_reconciliation"));
        bool allOrdersKnown = candidates.All(c => c.IngressSequence is > 0);
        CaptureFirstKnownFailure? firstKnown = candidates
            .Where(c => c.IngressSequence is > 0)
            .OrderBy(c => c.IngressSequence)
            .FirstOrDefault()
            ?? candidates.FirstOrDefault();
        if (firstKnown is not null)
            firstKnown = firstKnown with { AllFailureOrdersKnown = allOrdersKnown };
        return new CaptureReplayEvidence(
            ProtocolVersion: 1,
            Status: problems ? "retained_snapshot_issues" : "retained_snapshots_inspected",
            ReplayComplete: false,
            HistoryPrefixUnknown: journalIncomplete,
            CapturedSnapshots: snapshots.Count,
            DigestAlgorithm: DigestAlgorithm,
            FirstObservedIssue: firstKnown?.Reason ?? firstIssue ?? reconciliation.FirstIssue,
            Documents: documents,
            Snapshots: entries.ToArray(),
            Limitations:
            [
                "some_utf16_edit_batches_may_not_be_retained",
                "notification_stream_completeness_not_proven",
                "ingress_gaps_include_non_document_rpc_frames",
                "snapshot_hashes_do_not_prove_original_editor_inputs",
                "document_uris_and_raw_source_omitted",
                "input_receipts_do_not_prove_transport_capture_completeness",
                "unsettled_handler_entries_are_not_proof_of_loss",
                "transport_counters_are_unscoped_and_not_per_document_proof",
                "transport_to_handler_mapping_not_yet_proven",
            ],
            TraceCompleteness: problems || inputLedgerIncomplete ||
                transportWitness?.CountersOverflowed == true ? "partial" : "unknown",
            DocumentReplayCompleteness: problems || journalIncomplete ||
                inputLedgerIncomplete || transportWitness?.CountersOverflowed == true
                    ? "partial" : "unknown",
            ProjectInputsCompleteness: "unknown",
            SemanticReplayCompleteness: "unknown",
            InputReconciliation: reconciliation,
            InputLedgerIncomplete: inputLedgerIncomplete,
            TransportWitness: transportWitness,
            FirstKnownFailure: firstKnown);
    }

    private sealed class DocumentEvidenceBuilder(string id)
    {
        internal string Id { get; } = id;
        internal int Count;
        internal int Segments;
        internal int? LastVersion;
        internal string? LastText;
        internal int VerifiedEdits;
        internal bool HasObservedBaseline;
        internal string? SessionId;
        internal long? LastIngressSequence;
        internal bool AnyIssue;
    }
}

internal sealed record CaptureSnapshotEvidence(
    string DocumentId, int Version, string InputKind,
    int Utf16CodeUnits, string Sha256Utf16Le, string? Issue,
    int? CapturedEdits, long? IngressSequence = null,
    string? DocumentSessionId = null);

internal sealed record CaptureDocumentEvidence(
    string DocumentId, int CapturedSnapshots, int ObservedDidOpens,
    bool HasObservedBaseline, int? LastVersion, bool HasIssue,
    int VerifiedEditBatches);

internal sealed record CaptureReplayEvidence(
    int ProtocolVersion, string Status, bool ReplayComplete,
    bool HistoryPrefixUnknown, int CapturedSnapshots,
    string DigestAlgorithm, string? FirstObservedIssue,
    IReadOnlyList<CaptureDocumentEvidence> Documents,
    IReadOnlyList<CaptureSnapshotEvidence> Snapshots,
    IReadOnlyList<string> Limitations,
    string TraceCompleteness = "unknown",
    string DocumentReplayCompleteness = "unknown",
    string ProjectInputsCompleteness = "unknown",
    string SemanticReplayCompleteness = "unknown",
    CaptureInputReconciliation? InputReconciliation = null,
    bool InputLedgerIncomplete = false,
    CaptureTransportWitness? TransportWitness = null,
    CaptureFirstKnownFailure? FirstKnownFailure = null,
    CaptureVerificationBarrier? VerificationBarrier = null,
    bool InMemoryJournalSealed = false);

/// <summary>
/// The earliest known failure by an observed ingress ordinal. This is not
/// a proof of first failure in the original client transport when entries
/// or ordinals are missing. No raw document path or source is exposed.
/// </summary>
internal sealed record CaptureFirstKnownFailure(
    string Reason, long? IngressSequence, int? Version, string EvidenceSource,
    bool AllFailureOrdersKnown = false);

/// <summary>
/// Read-only fence over the ordered INPUT QUEUE only. Satisfied does not
/// mean raw transport or source capture is complete or a durable producer
/// has been sealed. Safe machine metadata only.
/// </summary>
internal sealed record CaptureVerificationBarrier(
    string Status,
    long? RequestIngressSequence,
    long? LastSettledHighWatermark,
    int OutstandingRegisteredInputs,
    bool TransportToHandlerCompleteProven,
    bool DurableProducerClosureProven);

/// <summary>
/// Privacy-safe, process-local count of document notification frames observed
/// at the transport read boundary after journal arming. The counters include
/// OTHER documents that did not receive consent, so neither the total nor a
/// difference from scoped handler counts proves lost frames.
/// </summary>
internal sealed record CaptureTransportWitness(
    long DidOpen, long DidChange, long DidClose, bool CountersOverflowed)
{
    public long ObservedNotifications => DidOpen + DidChange + DidClose;
    public string Scope => "unscoped_transport";
    public bool TransportToHandlerCorrelationProven => false;
    public bool CompleteDocumentReplayProven => false;
}
