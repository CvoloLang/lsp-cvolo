using Cvolo.LanguageServer.Observability;
using Cvolo.LanguageServer.Core.Documents;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public sealed class CaptureConsentStateTests
{
    private const string ServerId = "server-instance-A";
    private const string Document = "file:///C:/project/main.cvl";

    [Fact]
    public void Ttl_expiration_erases_scope_without_sleeping_or_granting_capture()
    {
        DateTimeOffset now = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
        var state = new CaptureConsentState(() => now);
        var accepted = state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1,
            ServerInstanceId = ServerId,
            UserConfirmed = true,
            CaptureSources = true,
            TtlSeconds = 10,
            DocumentUris = [Document],
        }, ServerId);
        Assert.Equal("registered_not_capturing", accepted.Status);
        Assert.True(state.Allows(Document, sources: true));
        Assert.False(state.Allows("file:///C:/project/other.cvl", sources: true));
        Assert.False(state.Allows(Document, sources: false));
        Assert.False(state.Status().CaptureActive);
        now = now.AddSeconds(11);
        Assert.False(state.Allows(Document, sources: true));
        Assert.Equal("expired", state.Status().Status);
        Assert.Null(state.Status().LeaseId);
        Assert.Equal(0, state.Status().ScopedDocumentCount);
    }

    [Fact]
    public void Invalid_scope_and_overlong_ttl_do_not_replace_a_valid_lease()
    {
        var state = new CaptureConsentState();
        CaptureConsentGrant grant = new()
        {
            ProtocolVersion = 1,
            ServerInstanceId = ServerId,
            UserConfirmed = true,
            CapturePaths = true,
            TtlSeconds = 60,
            DocumentUris = [Document],
        };
        var first = state.Grant(grant, ServerId);
        Assert.Equal("registered_not_capturing", first.Status);

        grant.DocumentUris = ["https://example.com/private.cvl"];
        Assert.Equal("invalid_consent_scope", state.Grant(grant, ServerId).Reason);
        grant.DocumentUris = [Document];
        grant.TtlSeconds = 901;
        Assert.Equal("invalid_consent_ttl", state.Grant(grant, ServerId).Reason);
        Assert.Equal(first.LeaseId, state.Status().LeaseId);
        Assert.True(state.Allows(Document, sources: false));
        Assert.False(state.Allows(Document, sources: true));
    }

    [Fact]
    public void Journal_retains_only_scoped_versions_then_revoke_removes_source()
    {
        var state = new CaptureConsentState();
        var granted = state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            UserConfirmed = true, CaptureSources = true, TtlSeconds = 120,
            DocumentUris = [Document],
        }, ServerId);

        Assert.Equal("armed_in_memory", state.Arm(new CaptureJournalArm
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            LeaseId = granted.LeaseId,
        }, ServerId).Status);
        state.CaptureCommitted(new Uri("file:///C:/project/unrelated.cvl"), 1, "not-consented");
        state.CaptureCommitted(new Uri(Document), 1, "hello");
        state.CaptureCommitted(new Uri(Document), 2, "hello world");
        state.CaptureCommitted(new Uri(Document), 2, "ignored-duplicate");

        var snapshots = state.CopyJournalForVerification();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal("hello", snapshots[0].Text);
        Assert.Equal("hello world", snapshots[1].Text);
        Assert.Equal(2, state.Status().CapturedSnapshots);
        Assert.Equal(16, state.Status().CapturedCharacters);
        Assert.True(state.Status().JournalIncomplete);
        Assert.True(state.Status().JournalActive);
        Assert.False(state.Status().SourceExportSupported);

        state.Revoke(new CaptureConsentRevoke
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
        }, ServerId);
        Assert.Empty(state.CopyJournalForVerification());
        Assert.False(state.Status().JournalActive);
        Assert.Equal(0, state.Status().CapturedCharacters);
        state.CaptureCommitted(new Uri(Document), 3, "after-revoke");
        Assert.Empty(state.CopyJournalForVerification());
    }

    [Fact]
    public void Journal_capacity_is_bounded_and_expiry_erases_retained_text()
    {
        DateTimeOffset now = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
        var state = new CaptureConsentState(() => now);
        var granted = state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            UserConfirmed = true, CaptureSources = true, TtlSeconds = 10,
            DocumentUris = [Document],
        }, ServerId);
        state.Arm(new CaptureJournalArm
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            LeaseId = granted.LeaseId,
        }, ServerId);

        state.CaptureCommitted(new Uri(Document), 1, new string('a', CaptureConsentState.MaxSnapshotChars + 1));
        Assert.Empty(state.CopyJournalForVerification());
        Assert.True(state.Status().JournalIncomplete);
        state.CaptureCommitted(new Uri(Document), 2, "ok");
        Assert.Single(state.CopyJournalForVerification());

        now = now.AddSeconds(11);
        Assert.Empty(state.CopyJournalForVerification());
        Assert.False(state.Status().JournalActive);
        Assert.Equal("expired", state.Status().Status);
        state.CaptureCommitted(new Uri(Document), 3, "after-expiry");
        Assert.Empty(state.CopyJournalForVerification());
    }

    [Fact]
    public void Journal_cannot_be_armed_without_matching_lease_and_source_permission()
    {
        var state = new CaptureConsentState();
        var grant = state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            UserConfirmed = true, CapturePaths = true, TtlSeconds = 60,
            DocumentUris = [Document],
        }, ServerId);
        var request = new CaptureJournalArm
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            LeaseId = grant.LeaseId,
        };
        Assert.Equal("source_permission_required", state.Arm(request, ServerId).Reason);
        state.CaptureCommitted(new Uri(Document), 1, "should-not-be-captured");
        Assert.Empty(state.CopyJournalForVerification());
    }

    [Fact]
    public void Verified_snapshots_preserve_utf16_hash_and_observed_baseline()
    {
        var state = new CaptureConsentState();
        var grant = state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            UserConfirmed = true, CaptureSources = true, TtlSeconds = 60,
            DocumentUris = [Document],
        }, ServerId);
        state.Arm(new CaptureJournalArm
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            LeaseId = grant.LeaseId,
        }, ServerId);

        state.CaptureCommitted(new Uri(Document), 1, "x😀", isDidOpen: true);
        state.CaptureCommitted(new Uri(Document), 3, "x😀!", isDidOpen: false,
            appliedChanges: [new DocumentChange(
                new TextRange(new TextPosition(0, 3), new TextPosition(0, 3)), "!")]);

        var result = state.InspectReplay();
        Assert.Equal("retained_snapshots_inspected", result.Status);
        Assert.False(result.ReplayComplete);
        Assert.True(result.HistoryPrefixUnknown);
        Assert.Equal("unknown", result.TraceCompleteness);
        Assert.Equal("partial", result.DocumentReplayCompleteness);
        Assert.Equal("unknown", result.ProjectInputsCompleteness);
        Assert.Equal("unknown", result.SemanticReplayCompleteness);
        Assert.Equal(2, result.CapturedSnapshots);
        Assert.Null(result.FirstObservedIssue);
        Assert.Equal("did_open", result.Snapshots[0].InputKind);
        Assert.Equal(3, result.Snapshots[0].Utf16CodeUnits);
        Assert.Equal(CaptureReplayVerifier.ComputeDigest("x😀"),
            result.Snapshots[0].Sha256Utf16Le);
        Assert.Equal("did_change", result.Snapshots[1].InputKind);
        Assert.Equal(3, result.Snapshots[1].Version);
        Assert.DoesNotContain("project", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Journal_without_didopen_reports_first_unverifiable_input()
    {
        var state = new CaptureConsentState();
        var grant = state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            UserConfirmed = true, CaptureSources = true, TtlSeconds = 60,
            DocumentUris = [Document],
        }, ServerId);
        state.Arm(new CaptureJournalArm
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            LeaseId = grant.LeaseId,
        }, ServerId);
        state.CaptureCommitted(new Uri(Document), 2, "text", isDidOpen: false);
        var evidence = state.InspectReplay();
        Assert.Equal("did_open_not_observed", evidence.FirstObservedIssue);
        Assert.Equal("retained_snapshot_issues", evidence.Status);
        Assert.False(evidence.ReplayComplete);
        state.Revoke(new CaptureConsentRevoke
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
        }, ServerId);
        Assert.Empty(state.InspectReplay().Snapshots);
    }

    [Fact]
    public void Accepted_utf16_edit_batches_replay_across_surrogate_pairs_and_crlf()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 1, "a\r\n😀", isDidOpen: true);
        state.CaptureCommitted(new Uri(Document), 2, "a\r\n😀!", appliedChanges:
        [
            new DocumentChange(
                new TextRange(new TextPosition(1, 2), new TextPosition(1, 2)), "!"),
        ]);
        state.CaptureCommitted(new Uri(Document), 3, "replaced\r\n💡", appliedChanges:
        [
            new DocumentChange(null, "replaced\r\n💡"),
        ]);
        var evidence = state.InspectReplay();
        Assert.Null(evidence.FirstObservedIssue);
        Assert.Equal(2, Assert.Single(evidence.Documents).VerifiedEditBatches);
        Assert.Equal(1, evidence.Snapshots[1].CapturedEdits);
        Assert.False(evidence.ReplayComplete);
    }

    [Fact]
    public void Mismatched_edit_replay_reports_first_failing_version()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 1, "abc", isDidOpen: true);
        state.CaptureCommitted(new Uri(Document), 2, "WRONG", appliedChanges:
        [
            new DocumentChange(
                new TextRange(new TextPosition(0, 1), new TextPosition(0, 2)), "X"),
        ]);
        var evidence = state.InspectReplay();
        Assert.Equal("replayed_text_mismatch", evidence.FirstObservedIssue);
        Assert.Equal("partial", evidence.DocumentReplayCompleteness);
        Assert.Equal("replayed_text_mismatch", evidence.Snapshots[1].Issue);
        Assert.Equal("retained_snapshot_issues", evidence.Status);
        Assert.False(evidence.ReplayComplete);
    }

    [Fact]
    public void Reopening_document_starts_new_replay_segment_with_reset_version()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 10, "old", isDidOpen: true);
        state.CaptureCommitted(new Uri(Document), 1, "new", isDidOpen: true);
        var evidence = state.InspectReplay();
        Assert.Equal(2, Assert.Single(evidence.Documents).ObservedDidOpens);
        Assert.Null(evidence.FirstObservedIssue);
        Assert.Equal(2, evidence.CapturedSnapshots);
    }

    [Fact]
    public void Missing_edit_batch_is_reported_and_no_raw_edit_escapes_in_evidence()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 1, "secret-baseline", isDidOpen: true);
        state.CaptureCommitted(new Uri(Document), 2, "secret-updated");
        var evidence = state.InspectReplay();
        Assert.Equal("edit_batch_not_retained", evidence.FirstObservedIssue);
        Assert.Null(evidence.Snapshots[1].CapturedEdits);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public void Interleaved_rpc_ingress_does_not_imply_missing_document_edit()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 1, "A", isDidOpen: true,
            ingressSequence: 10);
        state.CaptureCommitted(new Uri(Document), 2, "AB", appliedChanges:
        [
            new DocumentChange(new TextRange(new TextPosition(0, 1),
                new TextPosition(0, 1)), "B"),
        ], ingressSequence: 15);
        var evidence = state.InspectReplay();
        Assert.Null(evidence.FirstObservedIssue);
        Assert.Equal(10L, evidence.Snapshots[0].IngressSequence);
        Assert.Equal(15L, evidence.Snapshots[1].IngressSequence);
        Assert.Equal(1, Assert.Single(evidence.Documents).VerifiedEditBatches);
        Assert.False(evidence.ReplayComplete);
    }

    [Fact]
    public void Close_and_reopen_create_distinct_verification_segments()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 20, "old", isDidOpen: true,
            ingressSequence: 4);
        state.CaptureClosed(new Uri(Document), ingressSequence: 5);
        state.CaptureCommitted(new Uri(Document), 1, "new", isDidOpen: true,
            ingressSequence: 9);
        var evidence = state.InspectReplay();
        Assert.Null(evidence.FirstObservedIssue);
        Assert.Equal("did_close", evidence.Snapshots[1].InputKind);
        Assert.Equal(5L, evidence.Snapshots[1].IngressSequence);
        Assert.Equal(2, Assert.Single(evidence.Documents).ObservedDidOpens);
        Assert.False(evidence.ReplayComplete);
        Assert.DoesNotContain("old", System.Text.Json.JsonSerializer.Serialize(evidence));
        Assert.DoesNotContain("new", System.Text.Json.JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public void Duplicate_ingress_and_change_after_close_are_not_claimed_complete()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 1, "A", isDidOpen: true,
            ingressSequence: 10);
        state.CaptureClosed(new Uri(Document), ingressSequence: 11);
        state.CaptureCommitted(new Uri(Document), 2, "AB", appliedChanges:
        [
            new DocumentChange(new TextRange(new TextPosition(0, 1),
                new TextPosition(0, 1)), "B"),
        ], ingressSequence: 11);
        var evidence = state.InspectReplay();
        Assert.Equal("non_increasing_document_ingress", evidence.FirstObservedIssue);
        Assert.False(evidence.ReplayComplete);
        Assert.Equal("non_increasing_document_ingress", evidence.Snapshots[2].Issue);
    }

    [Fact]
    public void Close_outside_consent_scope_and_after_revoke_never_retains_evidence()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 1, "private", isDidOpen: true,
            ingressSequence: 1);
        state.CaptureClosed(new Uri("file:///C:/project/unrelated.cvl"),
            ingressSequence: 2);
        Assert.Single(state.CopyJournalForVerification());

        state.Revoke(new CaptureConsentRevoke
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
        }, ServerId);
        state.CaptureClosed(new Uri(Document), ingressSequence: 3);
        Assert.Empty(state.CopyJournalForVerification());
        Assert.Empty(state.InspectReplay().Snapshots);
    }

    [Fact]
    public void Close_does_not_claim_a_complete_document_replay()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 1, "private", isDidOpen: true,
            ingressSequence: 17);
        state.CaptureClosed(new Uri(Document), ingressSequence: 19);
        var result = state.InspectReplay();
        Assert.Null(result.FirstObservedIssue);
        Assert.False(result.ReplayComplete);
        Assert.Equal("did_close", result.Snapshots[1].InputKind);
        Assert.Equal(19L, result.Snapshots[1].IngressSequence);
        Assert.Equal(0, result.Snapshots[1].Utf16CodeUnits);
    }

    [Fact]
    public void Actual_captured_outcomes_are_reconciled_with_source_snapshots()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 1, "start", isDidOpen: true,
            ingressSequence: 10);
        state.CaptureSettled(new Uri(Document), "did_open", "applied", 1, 10);
        state.CaptureSettled(new Uri(Document), "did_change", "rejected", 2, 11);
        state.CaptureSettled(new Uri(Document), "did_change", "applied", 3, 12);

        var evidence = state.InspectReplay();
        Assert.Equal("applied_input_snapshot_missing", evidence.InputReconciliation!.FirstIssue);
        Assert.Equal(12L, evidence.InputReconciliation.FirstIssueIngressSequence);
        Assert.Equal("partial", evidence.DocumentReplayCompleteness);
        Assert.False(evidence.ReplayComplete);
        Assert.Equal(3, state.Status().CapturedInputOutcomes);
    }

    [Fact]
    public void Input_receipts_are_bounded_and_removed_on_revoke()
    {
        var state = ArmedJournal();
        for (int i = 0; i <= CaptureConsentState.MaxInputReceipts; i++)
            state.CaptureSettled(new Uri(Document), "did_change", "rejected", i, i + 1);

        Assert.True(state.Status().InputLedgerIncomplete);
        Assert.Equal(CaptureConsentState.MaxInputReceipts, state.Status().CapturedInputOutcomes);
        Assert.Equal("partial", state.InspectReplay().TraceCompleteness);
        state.Revoke(new CaptureConsentRevoke
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
        }, ServerId);
        Assert.Equal(0, state.Status().CapturedInputOutcomes);
        Assert.Empty(state.InspectReplay().Snapshots);
    }

    [Fact]
    public void Handler_entries_are_bounded_and_revoke_clears_them()
    {
        var state = ArmedJournal();
        state.CaptureHandlerEntered(new Uri("file:///C:/project/unrelated.cvl"),
            "did_open", 1, 1);
        Assert.Equal(0, state.Status().HandlerEntries);
        for (int i = 0; i <= CaptureConsentState.MaxInputReceipts; i++)
            state.CaptureHandlerEntered(new Uri(Document), "did_change", i, i + 2);
        Assert.Equal(CaptureConsentState.MaxInputReceipts, state.Status().HandlerEntries);
        Assert.True(state.Status().InputLedgerIncomplete);
        Assert.Equal("partial", state.InspectReplay().TraceCompleteness);
        state.Revoke(new CaptureConsentRevoke
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
        }, ServerId);
        Assert.Equal(0, state.Status().HandlerEntries);
    }

    [Fact]
    public void Handler_entry_can_be_inspected_before_settlement()
    {
        var state = ArmedJournal();
        state.CaptureHandlerEntered(new Uri(Document), "did_open", 1, 77);
        var e = state.InspectReplay();
        Assert.Equal(1, e.InputReconciliation!.UnsettledHandlerEntries);
        Assert.Null(e.FirstObservedIssue);
        Assert.False(e.ReplayComplete);
        state.CaptureCommitted(new Uri(Document), 1, "secret", isDidOpen: true,
            ingressSequence: 77);
        state.CaptureSettled(new Uri(Document), "did_open", "applied", 1, 77);
        var after = state.InspectReplay();
        Assert.Equal(0, after.InputReconciliation!.UnsettledHandlerEntries);
        Assert.Equal(1, after.InputReconciliation.MatchedSnapshots);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(after));
    }

    [Fact]
    public void Document_session_ids_track_close_reopen_without_source_leakage()
    {
        var state = ArmedJournal();
        state.CaptureCommitted(new Uri(Document), 10, "secret-old",
            isDidOpen: true, ingressSequence: 3);
        state.CaptureSettled(new Uri(Document), "did_open", "applied", 10, 3);
        state.CaptureClosed(new Uri(Document), ingressSequence: 4);
        state.CaptureSettled(new Uri(Document), "did_close", "applied", null, 4);
        state.CaptureCommitted(new Uri(Document), 1, "secret-new",
            isDidOpen: true, ingressSequence: 5);
        state.CaptureSettled(new Uri(Document), "did_open", "applied", 1, 5);
        var report = state.InspectReplay();
        Assert.Null(report.FirstObservedIssue);
        Assert.Equal(3, report.Snapshots.Count);
        Assert.Equal(report.Snapshots[0].DocumentSessionId,
            report.Snapshots[1].DocumentSessionId);
        Assert.NotEqual(report.Snapshots[0].DocumentSessionId,
            report.Snapshots[2].DocumentSessionId);
        Assert.StartsWith("doc-0001/session-", report.Snapshots[0].DocumentSessionId);
        string json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.DoesNotContain("secret-old", json);
        Assert.DoesNotContain("secret-new", json);
        Assert.DoesNotContain("project", json);
        Assert.False(report.ReplayComplete);
    }

    [Fact]
    public void Out_of_scope_input_receipts_are_never_retained()
    {
        var state = ArmedJournal();
        state.CaptureSettled(new Uri("file:///C:/project/unrelated.cvl"),
            "did_change", "rejected", 2, 5);
        Assert.Equal(0, state.Status().CapturedInputOutcomes);
        Assert.Equal(0, state.InspectReplay().InputReconciliation!.ObservedInputs);
    }

    private static CaptureConsentState ArmedJournal()
    {
        var state = new CaptureConsentState();
        var granted = state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            UserConfirmed = true, CaptureSources = true,
            TtlSeconds = 60, DocumentUris = [Document],
        }, ServerId);
        Assert.Equal("registered_not_capturing", granted.Status);
        Assert.Equal("armed_in_memory", state.Arm(new CaptureJournalArm
        {
            ProtocolVersion = 1, ServerInstanceId = ServerId,
            LeaseId = granted.LeaseId,
        }, ServerId).Status);
        return state;
    }

    [Fact]
    public void Revoke_is_idempotent_and_clears_source_and_path_scope()
    {
        var state = new CaptureConsentState();
        state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1,
            ServerInstanceId = ServerId,
            UserConfirmed = true,
            CaptureSources = true,
            CapturePaths = true,
            TtlSeconds = 100,
            DocumentUris = [Document],
        }, ServerId);
        Assert.True(state.Allows(Document, sources: true));
        Assert.True(state.Allows(Document, sources: false));

        var wrongServer = state.Revoke(new CaptureConsentRevoke
        {
            ProtocolVersion = 1, ServerInstanceId = "other"
        }, ServerId);
        Assert.Equal("rejected", wrongServer.Status);
        Assert.True(state.Allows(Document, sources: true));

        for (int i = 0; i < 2; i++)
        {
            var response = state.Revoke(new CaptureConsentRevoke
            {
                ProtocolVersion = 1, ServerInstanceId = ServerId
            }, ServerId);
            Assert.Equal("revoked", response.Status);
            Assert.False(state.Allows(Document, sources: true));
            Assert.False(state.Allows(Document, sources: false));
        }
    }
}
