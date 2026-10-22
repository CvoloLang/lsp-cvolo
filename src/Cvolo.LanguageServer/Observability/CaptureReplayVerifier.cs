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
        IReadOnlyList<CapturedDocumentSnapshot> snapshots, bool journalIncomplete)
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
            if (!string.Equals(
                ComputeDigest(snapshot.Text), snapshot.Sha256Utf16Le,
                StringComparison.Ordinal))
            {
                issue = "snapshot_digest_mismatch";
            }
            else if (snapshot.IsDidOpen)
            {
                doc.Segments++;
                doc.HasObservedBaseline = true;
                doc.LastVersion = null;
            }
            else if (!doc.HasObservedBaseline)
            {
                issue = "did_open_not_observed";
            }

            if (!snapshot.IsDidOpen
                && doc.LastVersion is { } previous
                && snapshot.Version <= previous)
            {
                issue ??= "non_increasing_document_version";
            }

            doc.LastVersion = snapshot.Version;
            doc.Count++;
            doc.AnyIssue |= issue is not null;
            firstIssue ??= issue;
            entries.Add(new CaptureSnapshotEvidence(
                doc.Id, snapshot.Version,
                snapshot.IsDidOpen ? "did_open" : "did_change",
                snapshot.Text.Length, snapshot.Sha256Utf16Le, issue));
        }

        var documents = perDocument.Values.Select(d => new CaptureDocumentEvidence(
            d.Id, d.Count, d.Segments, d.HasObservedBaseline,
            d.LastVersion, d.AnyIssue)).ToArray();
        bool problems = firstIssue is not null;
        return new CaptureReplayEvidence(
            ProtocolVersion: 1,
            Status: problems ? "retained_snapshot_issues" : "retained_snapshots_inspected",
            ReplayComplete: false,
            HistoryPrefixUnknown: journalIncomplete,
            CapturedSnapshots: snapshots.Count,
            DigestAlgorithm: DigestAlgorithm,
            FirstObservedIssue: firstIssue,
            Documents: documents,
            Snapshots: entries.ToArray(),
            Limitations:
            [
                "utf16_edit_batches_not_recorded",
                "notification_stream_completeness_not_proven",
                "snapshot_hashes_do_not_prove_original_editor_inputs",
                "document_uris_and_raw_source_omitted",
            ]);
    }

    private sealed class DocumentEvidenceBuilder(string id)
    {
        internal string Id { get; } = id;
        internal int Count;
        internal int Segments;
        internal int? LastVersion;
        internal bool HasObservedBaseline;
        internal bool AnyIssue;
    }
}

internal sealed record CaptureSnapshotEvidence(
    string DocumentId, int Version, string InputKind,
    int Utf16CodeUnits, string Sha256Utf16Le, string? Issue);

internal sealed record CaptureDocumentEvidence(
    string DocumentId, int CapturedSnapshots, int ObservedDidOpens,
    bool HasObservedBaseline, int? LastVersion, bool HasIssue);

internal sealed record CaptureReplayEvidence(
    int ProtocolVersion, string Status, bool ReplayComplete,
    bool HistoryPrefixUnknown, int CapturedSnapshots,
    string DigestAlgorithm, string? FirstObservedIssue,
    IReadOnlyList<CaptureDocumentEvidence> Documents,
    IReadOnlyList<CaptureSnapshotEvidence> Snapshots,
    IReadOnlyList<string> Limitations);
