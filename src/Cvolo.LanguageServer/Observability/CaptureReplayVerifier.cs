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
        bool inputLedgerIncomplete = false)
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
                    snapshot.IngressSequence));
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
                snapshot.AppliedChanges?.Count, snapshot.IngressSequence));
        }

        var documents = perDocument.Values.Select(d => new CaptureDocumentEvidence(
            d.Id, d.Count, d.Segments, d.HasObservedBaseline,
            d.LastVersion, d.AnyIssue, d.VerifiedEdits)).ToArray();
        CaptureInputReconciliation reconciliation = CaptureInputReconciler.Reconcile(
            inputs ?? Array.Empty<CapturedDocumentInput>(), snapshots);
        bool problems = firstIssue is not null || reconciliation.FirstIssue is not null;
        return new CaptureReplayEvidence(
            ProtocolVersion: 1,
            Status: problems ? "retained_snapshot_issues" : "retained_snapshots_inspected",
            ReplayComplete: false,
            HistoryPrefixUnknown: journalIncomplete,
            CapturedSnapshots: snapshots.Count,
            DigestAlgorithm: DigestAlgorithm,
            FirstObservedIssue: firstIssue ?? reconciliation.FirstIssue,
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
            ],
            TraceCompleteness: problems || inputLedgerIncomplete ? "partial" : "unknown",
            DocumentReplayCompleteness: problems || journalIncomplete || inputLedgerIncomplete ? "partial" : "unknown",
            ProjectInputsCompleteness: "unknown",
            SemanticReplayCompleteness: "unknown",
            InputReconciliation: reconciliation,
            InputLedgerIncomplete: inputLedgerIncomplete);
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
        internal long? LastIngressSequence;
        internal bool AnyIssue;
    }
}

internal sealed record CaptureSnapshotEvidence(
    string DocumentId, int Version, string InputKind,
    int Utf16CodeUnits, string Sha256Utf16Le, string? Issue,
    int? CapturedEdits, long? IngressSequence = null);

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
    bool InputLedgerIncomplete = false);
