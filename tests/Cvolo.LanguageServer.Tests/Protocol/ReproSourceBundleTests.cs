using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
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
    public void Empty_or_close_only_captures_are_not_valid_source_archives()
    {
        var empty = Array.Empty<CapturedDocumentSnapshot>();
        Assert.Throws<InvalidDataException>(() => ReproSourceBundle.Build(
            new CaptureExportSnapshot("lease", empty,
                CaptureReplayVerifier.Inspect(empty, true))));
        CapturedDocumentSnapshot[] closeOnly =
        [
            new(Uri, 0, "", CaptureReplayVerifier.ComputeDigest(""),
                false, null, 1, true),
        ];
        Assert.Throws<InvalidDataException>(() => ReproSourceBundle.Build(
            new CaptureExportSnapshot("lease", closeOnly,
                CaptureReplayVerifier.Inspect(closeOnly, true))));
    }

    [Fact]
    public void Recomputed_sha_cannot_smuggle_unknown_root_or_record_fields()
    {
        var bundle = MinimalBundle();
        var extraRoot = RewriteManifest(bundle.Bytes, json =>
            json["sourceAbsolutePath"] = "C:/private/workspace/main.cvl");
        var extraRecord = RewriteManifest(bundle.Bytes, json =>
            json["snapshots"]!.AsArray()[0]!["documentUri"] =
                "file:///private/workspace/main.cvl");
        foreach (var altered in new[] { extraRoot, extraRecord })
        {
            var evidence = ReproSourceBundle.Verify(altered.Archive, altered.ManifestHash);
            Assert.False(evidence.IntegrityValid);
            Assert.Equal("invalid_manifest_schema", evidence.Reason);
        }
    }

    [Fact]
    public void Nested_edit_metadata_must_not_accept_sensitive_unknown_properties()
    {
        var bundle = EditedBundle();
        var altered = RewriteManifest(bundle.Bytes, json =>
            json["snapshots"]!.AsArray()[1]!["changes"]!.AsArray()[0]!["workspacePath"]
                = "/home/user/private-project");
        var evidence = ReproSourceBundle.Verify(altered.Archive, altered.ManifestHash);
        Assert.False(evidence.IntegrityValid);
        Assert.Equal("invalid_manifest_schema", evidence.Reason);
    }

    [Fact]
    public void Duplicate_json_keys_are_rejected_even_when_manifest_hash_matches()
    {
        var bundle = MinimalBundle();
        var mutated = RewriteRawManifest(bundle.Bytes, raw =>
            raw.Replace("\"documentId\": \"doc-0001\"",
                "\"documentId\": \"doc-0001\", \"documentId\": \"doc-0001\"",
                StringComparison.Ordinal));
        var result = ReproSourceBundle.Verify(mutated.Archive, mutated.ManifestHash);
        Assert.False(result.IntegrityValid);
        Assert.Equal("invalid_manifest_schema", result.Reason);
    }

    [Fact]
    public void Missing_or_null_snapshot_records_are_not_accepted_as_repro()
    {
        var bundle = MinimalBundle();
        var missing = RewriteManifest(bundle.Bytes, json =>
            json.AsObject().Remove("scope"));
        var nullRecord = RewriteManifest(bundle.Bytes, json =>
            json["snapshots"]!.AsArray()[0] = null);
        foreach (var altered in new[] { missing, nullRecord })
        {
            var result = ReproSourceBundle.Verify(altered.Archive, altered.ManifestHash);
            Assert.False(result.IntegrityValid);
            Assert.Equal("invalid_manifest_schema", result.Reason);
        }
    }

    private static ReproBundleBytes EditedBundle()
    {
        CapturedDocumentSnapshot[] snapshots =
        [
            new(Uri, 1, "before",
                CaptureReplayVerifier.ComputeDigest("before"), true, null, 1),
            new(Uri, 2, "after",
                CaptureReplayVerifier.ComputeDigest("after"), false,
                [new DocumentChange(null, "after")], 2),
        ];
        return ReproSourceBundle.Build(new CaptureExportSnapshot(
            "lease", snapshots, CaptureReplayVerifier.Inspect(snapshots, true)));
    }

    private static (byte[] Archive, string ManifestHash) RewriteRawManifest(
        byte[] archive, Func<string, string> mutate)
    {
        using var input = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        using var reader = new StreamReader(input.GetEntry("repro-manifest.json")!.Open());
        string original = reader.ReadToEnd();
        string changed = mutate(original);
        Assert.NotEqual(original, changed);
        byte[] manifestBytes = Encoding.UTF8.GetBytes(changed);
        string hash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                var target = zip.CreateEntry(entry.FullName);
                using Stream stream = target.Open();
                if (entry.FullName == "repro-manifest.json")
                    stream.Write(manifestBytes);
                else
                {
                    using Stream originalStream = entry.Open();
                    originalStream.CopyTo(stream);
                }
            }
        }
        return (output.ToArray(), hash);
    }

    [Fact]
    public void Recomputed_manifest_hash_does_not_authorize_raw_document_paths()
    {
        var bundle = MinimalBundle();
        var altered = RewriteManifest(bundle.Bytes, json =>
        {
            json["snapshots"]!.AsArray()[0]!["documentId"] = "file:///private/main.cvl";
        });
        var verified = ReproSourceBundle.Verify(altered.Archive, altered.ManifestHash);
        Assert.False(verified.IntegrityValid);
        Assert.Equal("invalid_repro_record", verified.Reason);
    }

    [Fact]
    public void Recomputed_hash_with_renumbered_source_member_is_rejected()
    {
        var bundle = MinimalBundle();
        var altered = RewriteManifest(bundle.Bytes, json =>
        {
            json["snapshots"]!.AsArray()[0]!["sourceEntry"] = "sources/step-9999.cvl";
        }, renameSource: "sources/step-9999.cvl");
        var verified = ReproSourceBundle.Verify(altered.Archive, altered.ManifestHash);
        Assert.False(verified.IntegrityValid);
        Assert.Equal("invalid_snapshot", verified.Reason);
    }

    [Fact]
    public void Forged_complete_status_is_rejected_even_with_matching_manifest_hash()
    {
        var bundle = MinimalBundle();
        var altered = RewriteManifest(bundle.Bytes, json =>
        {
            json["traceCompleteness"] = "complete";
        });
        var verified = ReproSourceBundle.Verify(altered.Archive, altered.ManifestHash);
        Assert.False(verified.IntegrityValid);
        Assert.Equal("unsupported_or_invalid_manifest", verified.Reason);
    }

    [Fact]
    public void Recomputed_hash_does_not_permit_invalid_document_session()
    {
        var bundle = MinimalBundle();
        var altered = RewriteManifest(bundle.Bytes, json =>
        {
            json["snapshots"]!.AsArray()[0]!["documentSessionId"] = "doc-0001/session-0042";
        });
        var verified = ReproSourceBundle.Verify(altered.Archive, altered.ManifestHash);
        Assert.False(verified.IntegrityValid);
        Assert.Equal("invalid_document_session", verified.Reason);
    }

    private static ReproBundleBytes MinimalBundle()
    {
        CapturedDocumentSnapshot[] snapshots =
        [
            new(Uri, 1, "source",
                CaptureReplayVerifier.ComputeDigest("source"), true, null, 1),
        ];
        return ReproSourceBundle.Build(new CaptureExportSnapshot(
            "lease", snapshots, CaptureReplayVerifier.Inspect(snapshots, true)));
    }

    private static (byte[] Archive, string ManifestHash) RewriteManifest(
        byte[] archive, Action<JsonObject> mutate, string? renameSource = null)
    {
        using var input = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        var manifestEntry = input.GetEntry("repro-manifest.json")!;
        JsonObject json;
        using (var reader = new StreamReader(manifestEntry.Open()))
            json = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        mutate(json);
        byte[] bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        string manifestHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in input.Entries)
            {
                var name = entry.FullName == "repro-manifest.json" ? entry.FullName
                    : renameSource ?? entry.FullName;
                var copied = zip.CreateEntry(name);
                using var target = copied.Open();
                if (entry.FullName == "repro-manifest.json")
                    target.Write(bytes);
                else
                {
                    using var stream = entry.Open();
                    stream.CopyTo(target);
                }
            }
        }
        return (output.ToArray(), manifestHash);
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
    public void Sealed_empty_capture_rejects_export_without_publishing_a_file()
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
            ProtocolVersion = 1, ServerInstanceId = serverId,
            LeaseId = grant.LeaseId,
        }, serverId);
        state.Seal(new CaptureJournalSeal
        {
            ProtocolVersion = 1, ServerInstanceId = serverId,
            LeaseId = grant.LeaseId,
        }, serverId);
        Assert.False(state.TryPrepareSourceExport(new ReproExportRequest
        {
            ProtocolVersion = 1, ServerInstanceId = serverId,
            LeaseId = grant.LeaseId, ExplicitUserConfirmation = true,
        }, serverId, out _, out string? reason));
        Assert.Equal("no_scoped_source_snapshots", reason);
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
