using System.IO.Compression;
using System.Text;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Observability;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public sealed class ReproSourceBundleTests
{
    private const string Uri = "file:///C:/secret/project/main.cvl";

    [Fact]
    public void Archive_is_privately_scoped_hashed_and_independently_replayed()
    {
        CapturedDocumentSnapshot[] snapshots =
        [
            new(Uri, 1, "a\r\n😀", CaptureReplayVerifier.ComputeDigest("a\r\n😀"), true, null, 1),
            new(Uri, 2, "b\r\n😀!", CaptureReplayVerifier.ComputeDigest("b\r\n😀!"), false,
            [
                new(new TextRange(new TextPosition(0, 0), new TextPosition(0, 1)), "b"),
                new(new TextRange(new TextPosition(1, 2), new TextPosition(1, 2)), "!"),
            ], 2),
            new(Uri, 2, "", CaptureReplayVerifier.ComputeDigest(""), false, null, 3, true),
            new(Uri, 1, "reopened", CaptureReplayVerifier.ComputeDigest("reopened"), true, null, 4),
        ];
        CaptureReplayEvidence evidence = CaptureReplayVerifier.Inspect(snapshots, true);
        var bundle = ReproSourceBundle.Build(new CaptureExportSnapshot("lease", snapshots, evidence));
        var verified = ReproSourceBundle.Verify(bundle.Bytes, bundle.ManifestSha256);
        Assert.True(verified.IntegrityValid, verified.Reason);
        Assert.True(verified.RetainedEditsReplayed);
        Assert.Equal(3, verified.VerifiedSources);
        using var zip = new ZipArchive(new MemoryStream(bundle.Bytes), ZipArchiveMode.Read);
        string manifest;
        using (var reader = new StreamReader(zip.GetEntry("repro-manifest.json")!.Open()))
            manifest = reader.ReadToEnd();
        Assert.DoesNotContain("secret/project", manifest);
        Assert.DoesNotContain("main.cvl", manifest);
        Assert.Contains("doc-0001/session-0002", manifest);
        using var json = System.Text.Json.JsonDocument.Parse(manifest);
        var root = json.RootElement;
        Assert.False(root.GetProperty("replayComplete").GetBoolean());
        Assert.True(root.TryGetProperty("semanticReplayCompleteness", out _));
        Assert.False(root.GetProperty("transportProducerClosed").GetBoolean());
    }

    [Fact]
    public void Hash_valid_baseline_only_archive_does_not_claim_edit_replay()
    {
        CapturedDocumentSnapshot[] snapshots =
        [
            new(Uri, 1, "source", CaptureReplayVerifier.ComputeDigest("source"), true),
        ];
        var bundle = ReproSourceBundle.Build(new CaptureExportSnapshot(
            "lease", snapshots, CaptureReplayVerifier.Inspect(snapshots, true)));
        var result = ReproSourceBundle.Verify(bundle.Bytes, bundle.ManifestSha256);
        Assert.True(result.IntegrityValid);
        Assert.Equal(1, result.VerifiedSources);
        Assert.False(result.RetainedEditsReplayed);
    }

    [Fact]
    public void Archive_verification_detects_tampered_source_member_and_manifest()
    {
        CapturedDocumentSnapshot[] snapshots =
        [
            new(Uri, 1, "private", CaptureReplayVerifier.ComputeDigest("private"), true),
        ];
        var bundle = ReproSourceBundle.Build(new CaptureExportSnapshot("lease", snapshots,
            CaptureReplayVerifier.Inspect(snapshots, true)));
        Assert.False(ReproSourceBundle.Verify(bundle.Bytes,
            new string('0', 64)).IntegrityValid);

        using var input = new ZipArchive(new MemoryStream(bundle.Bytes), ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var rewrite = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var original in input.Entries)
            {
                var entry = rewrite.CreateEntry(original.FullName);
                using var writer = entry.Open();
                if (original.FullName.StartsWith("sources/", StringComparison.Ordinal))
                    writer.Write(Encoding.UTF8.GetBytes("TAMPERED"));
                else
                {
                    using var stream = original.Open();
                    stream.CopyTo(writer);
                }
            }
        var verification = ReproSourceBundle.Verify(output.ToArray(), bundle.ManifestSha256);
        Assert.False(verification.IntegrityValid);
        Assert.Equal("source_digest_mismatch", verification.Reason);
    }

    [Fact]
    public void Revoked_lease_cannot_commit_even_prepared_export()
    {
        var state = new CaptureConsentState();
        const string serverId = "test-server";
        var grant = state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1, ServerInstanceId = serverId,
            UserConfirmed = true, CaptureSources = true,
            TtlSeconds = 60, DocumentUris = [Uri],
        }, serverId);
        state.Arm(new CaptureJournalArm
        {
            ProtocolVersion = 1, ServerInstanceId = serverId, LeaseId = grant.LeaseId,
        }, serverId);
        state.CaptureCommitted(new System.Uri(Uri), 1, "PRIVATE", isDidOpen: true);
        state.Seal(new CaptureJournalSeal
        {
            ProtocolVersion = 1, ServerInstanceId = serverId, LeaseId = grant.LeaseId,
        }, serverId);
        var request = new ReproExportRequest
        {
            ProtocolVersion = 1, ServerInstanceId = serverId, LeaseId = grant.LeaseId,
            ExplicitUserConfirmation = true,
        };
        Assert.True(state.TryPrepareSourceExport(request, serverId,
            out CaptureExportSnapshot? snapshot, out _));
        Assert.NotNull(snapshot);
        state.Revoke(new CaptureConsentRevoke
        {
            ProtocolVersion = 1, ServerInstanceId = serverId,
        }, serverId);
        bool committed = false;
        Assert.False(state.CommitExport(snapshot!.LeaseId, () => committed = true));
        Assert.False(committed);
        Assert.False(state.TryPrepareSourceExport(request, serverId, out _, out _));
    }

    [Fact]
    public void Explicit_reconfirmation_and_seal_required_for_export()
    {
        var state = new CaptureConsentState();
        const string serverId = "test-server";
        var grant = state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1, ServerInstanceId = serverId,
            UserConfirmed = true, CaptureSources = true,
            TtlSeconds = 60, DocumentUris = [Uri],
        }, serverId);
        var request = new ReproExportRequest
        {
            ProtocolVersion = 1, ServerInstanceId = serverId, LeaseId = grant.LeaseId,
            ExplicitUserConfirmation = false,
        };
        Assert.False(state.TryPrepareSourceExport(request, serverId, out _, out string? reason));
        Assert.Equal("explicit_export_confirmation_required", reason);
        request.ExplicitUserConfirmation = true;
        Assert.False(state.TryPrepareSourceExport(request, serverId, out _, out reason));
        Assert.Equal("sealed_source_journal_required", reason);
    }
}
