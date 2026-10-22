using Cvolo.LanguageServer.Observability;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public sealed class CaptureInputReconcilerTests
{
    private const string Document = "file:///C:/project/main.cvl";
    private const string Other = "file:///C:/project/other.cvl";

    private static CapturedDocumentSnapshot Open(string uri, int version, long? ingress)
        => new(uri, version, "x", CaptureReplayVerifier.ComputeDigest("x"),
            true, null, ingress);

    private static CapturedDocumentSnapshot Change(string uri, int version, long? ingress)
        => new(uri, version, "xy", CaptureReplayVerifier.ComputeDigest("xy"),
            false, null, ingress);

    [Fact]
    public void Interleaved_other_rpc_frames_do_not_count_as_missing_changes()
    {
        CapturedDocumentSnapshot[] snapshots = [Open(Document, 1, 10), Change(Document, 2, 19)];
        CapturedDocumentInput[] inputs =
        [
            new(Document, "did_open", "applied", 1, 10),
            new(Document, "did_change", "applied", 2, 19),
        ];

        var result = CaptureInputReconciler.Reconcile(inputs, snapshots);
        Assert.Null(result.FirstIssue);
        Assert.Equal(2, result.MatchedSnapshots);
        Assert.Equal(2, result.AppliedInputs);
    }

    [Fact]
    public void Applied_input_without_snapshot_identifies_exact_ingress()
    {
        var result = CaptureInputReconciler.Reconcile(
            [
                new(Document, "did_open", "applied", 1, 10),
                new(Document, "did_change", "applied", 2, 11),
            ],
            [Open(Document, 1, 10)]);

        Assert.Equal("applied_input_snapshot_missing", result.FirstIssue);
        Assert.Equal(11L, result.FirstIssueIngressSequence);
        Assert.Equal(1, result.MatchedSnapshots);
    }

    [Fact]
    public void Rejected_input_is_not_a_lost_applied_change()
    {
        var result = CaptureInputReconciler.Reconcile(
            [
                new(Document, "did_open", "applied", 1, 4),
                new(Document, "did_change", "rejected", 1, 5),
                new(Document, "did_change", "applied", 2, 6),
            ],
            [Open(Document, 1, 4), Change(Document, 2, 6)]);

        Assert.Null(result.FirstIssue);
        Assert.Equal(1, result.RejectedInputs);
        Assert.Equal(2, result.MatchedSnapshots);
    }

    [Fact]
    public void Close_and_reopen_are_matched_as_distinct_inputs()
    {
        CapturedDocumentSnapshot close = new(Document, 10, "",
            CaptureReplayVerifier.ComputeDigest(""), false, null, 8, true);
        var result = CaptureInputReconciler.Reconcile(
            [
                new(Document, "did_open", "applied", 10, 5),
                new(Document, "did_close", "applied", null, 8),
                new(Document, "did_open", "applied", 1, 12),
            ],
            [Open(Document, 10, 5), close, Open(Document, 1, 12)]);

        Assert.Null(result.FirstIssue);
        Assert.Equal(3, result.MatchedSnapshots);
    }

    [Fact]
    public void Duplicate_and_reordered_document_ingress_are_rejected()
    {
        var result = CaptureInputReconciler.Reconcile(
            [
                new(Document, "did_open", "applied", 1, 9),
                new(Document, "did_change", "applied", 2, 9),
            ],
            [Open(Document, 1, 9), Change(Document, 2, 9)]);

        Assert.Equal("non_increasing_document_input_ingress", result.FirstIssue);
        Assert.Equal(9L, result.FirstIssueIngressSequence);
    }

    [Fact]
    public void Duplicate_snapshots_are_detected_at_conflicting_occurrence()
    {
        var result = CaptureInputReconciler.Reconcile(
            [new(Document, "did_open", "applied", 1, 9)],
            [Open(Document, 1, 9), Open(Document, 1, 9)]);
        Assert.Equal("duplicate_retained_snapshot_ingress", result.FirstIssue);
        Assert.Equal(9L, result.FirstIssueIngressSequence);
    }

    [Fact]
    public void Handler_entry_without_settlement_is_in_flight_not_proven_loss()
    {
        var evidence = CaptureInputReconciler.Reconcile(
            [new(Document, "did_open", "applied", 1, 3)],
            [Open(Document, 1, 3)],
            [
                new(Document, "did_open", 1, 3),
                new(Document, "did_change", 2, 7),
            ]);
        Assert.Null(evidence.FirstIssue);
        Assert.Equal(2, evidence.HandlerEntries);
        Assert.Equal(1, evidence.UnsettledHandlerEntries);
        Assert.Equal(1, evidence.MatchedSnapshots);
    }

    [Fact]
    public void Out_of_order_handler_dispatch_is_safe_to_reconcile()
    {
        var evidence = CaptureInputReconciler.Reconcile(
            [
                new(Document, "did_open", "applied", 1, 2),
                new(Document, "did_change", "applied", 2, 7),
            ],
            [Open(Document, 1, 2), Change(Document, 2, 7)],
            [
                new(Document, "did_change", 2, 7),
                new(Document, "did_open", 1, 2),
            ]);
        Assert.Null(evidence.FirstIssue);
        Assert.Equal(0, evidence.UnsettledHandlerEntries);
    }

    [Fact]
    public void Unknown_handler_ingress_is_not_fabricated()
    {
        var evidence = CaptureInputReconciler.Reconcile(
            Array.Empty<CapturedDocumentInput>(),
            Array.Empty<CapturedDocumentSnapshot>(),
            [new(Document, "did_change", 2, null)]);
        Assert.Equal(1, evidence.HandlerEntriesWithoutIngress);
        Assert.Equal(0, evidence.UnsettledHandlerEntries);
        Assert.Null(evidence.FirstIssue);
    }

    [Fact]
    public void Concurrent_documents_do_not_share_snapshots_by_ingress()
    {
        var result = CaptureInputReconciler.Reconcile(
            [
                new(Document, "did_open", "applied", 1, 2),
                new(Other, "did_open", "applied", 1, 3),
            ],
            [Open(Document, 1, 2), Open(Other, 1, 3)]);

        Assert.Null(result.FirstIssue);
        Assert.Equal(2, result.MatchedSnapshots);
    }

    [Fact]
    public void Unknown_ingress_is_explicitly_uncorrelated_not_fabricated()
    {
        var result = CaptureInputReconciler.Reconcile(
            [new(Document, "did_open", "applied", 1, null)],
            [Open(Document, 1, null)]);

        Assert.Null(result.FirstIssue);
        Assert.Equal(1, result.UnknownIngressInputs);
        Assert.Equal(0, result.MatchedSnapshots);
    }

    [Fact]
    public void Faulted_input_is_not_a_successful_edit()
    {
        var result = CaptureInputReconciler.Reconcile(
            [new(Document, "did_change", "faulted", 4, 13)],
            []);
        Assert.Equal("document_input_handler_faulted", result.FirstIssue);
        Assert.Equal(13L, result.FirstIssueIngressSequence);
    }

    [Fact]
    public void Wrong_snapshot_kind_is_not_silently_matched()
    {
        var result = CaptureInputReconciler.Reconcile(
            [new(Document, "did_change", "applied", 1, 7)],
            [Open(Document, 1, 7)]);
        Assert.Equal("applied_input_snapshot_mismatch", result.FirstIssue);
    }
}
