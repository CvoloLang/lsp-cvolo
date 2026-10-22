using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// A private, explicitly confirmed source archive. No project files are opened:
/// the input consists exclusively of consent-scoped retained editor snapshots.
/// The manifest uses synthetic document IDs and explicitly disclaims replay,
/// project closure, environment closure, provenance and crash durability.
/// </summary>
internal static class ReproSourceBundle
{
    internal const int FormatVersion = 1;
    internal const int MaxArchiveBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    internal static ReproBundleBytes Build(CaptureExportSnapshot source)
    {
        var documents = new Dictionary<string, (string Id, int Session)>(
            StringComparer.Ordinal);
        var records = new List<ReproSourceRecord>(source.Snapshots.Count);
        var contents = new List<(string Member, byte[] Bytes)>();
        int nextDocument = 0;
        foreach (var snap in source.Snapshots)
        {
            if (!documents.TryGetValue(snap.DocumentUri, out var document))
                document = ("doc-" + (++nextDocument).ToString("D4",
                    System.Globalization.CultureInfo.InvariantCulture), 0);
            if (snap.IsDidOpen)
                document.Session++;
            documents[snap.DocumentUri] = document;
            string kind = snap.IsDidClose ? "did_close"
                : snap.IsDidOpen ? "did_open" : "did_change";
            string sessionId = document.Id + "/session-" + document.Session.ToString("D4",
                System.Globalization.CultureInfo.InvariantCulture);
            string? member = null, sha = null;
            if (!snap.IsDidClose)
            {
                // Each step contains the actual observed editor state, allowing
                // independent text-digest validation even with missing edits.
                byte[] data = Encoding.UTF8.GetBytes(snap.Text);
                member = "sources/step-" + records.Count.ToString("D4",
                    System.Globalization.CultureInfo.InvariantCulture) + ".cvl";
                sha = Hash(data);
                contents.Add((member, data));
            }
            records.Add(new ReproSourceRecord(
                document.Id, sessionId, kind, snap.Version,
                snap.IngressSequence, member, sha, snap.Sha256Utf16Le,
                snap.AppliedChanges?.ToArray()));
        }

        ReproSourceManifest manifest = new(
            FormatVersion, "cvolo.private-source-repro",
            "sha256", "utf16le-sha256", records,
            source.Evidence.TraceCompleteness,
            source.Evidence.DocumentReplayCompleteness,
            source.Evidence.ProjectInputsCompleteness,
            source.Evidence.SemanticReplayCompleteness,
            true, false, false, false, false,
            "privacy_scoped_retained_editor_documents_only",
            "replay_history_and_external_project_inputs_not_proven");

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (member, data) in contents)
            {
                var entry = zip.CreateEntry(member, CompressionLevel.Optimal);
                using Stream output = entry.Open();
                output.Write(data);
            }
            var manifestEntry = zip.CreateEntry("repro-manifest.json", CompressionLevel.Optimal);
            using Stream manifestStream = manifestEntry.Open();
            manifestStream.Write(manifestBytes);
        }
        if (buffer.Length > MaxArchiveBytes)
            throw new InvalidDataException("archive_budget_exceeded");
        byte[] archive = buffer.ToArray();
        string manifestHash = Hash(manifestBytes);
        // Verify from the actual serialized archive, not just the model used
        // to generate it. Integrity != signature, authorship or completeness.
        ReproBundleVerification verified = Verify(archive, manifestHash);
        if (!verified.IntegrityValid)
            throw new InvalidDataException("archive_self_validation_failed");
        return new(archive, manifestHash, records.Count, contents.Count);
    }

    internal static ReproBundleVerification Verify(
        byte[] archive, string expectedManifestHash)
    {
        try
        {
            if (archive.Length > MaxArchiveBytes)
                return new(false, "archive_budget_exceeded", 0, false);
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > CaptureConsentState.MaxJournalSnapshots + 1)
                return new(false, "too_many_members", 0, false);
            if (zip.Entries.Any(e =>
                    e.FullName.StartsWith("/", StringComparison.Ordinal)
                    || e.FullName.Contains("..", StringComparison.Ordinal)
                    || e.FullName.Contains('\\')
                    || (e.FullName != "repro-manifest.json"
                        && !e.FullName.StartsWith("sources/step-", StringComparison.Ordinal))
                    || e.Length > CaptureConsentState.MaxSnapshotChars * 4L)
                || zip.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count()
                    != zip.Entries.Count)
                return new(false, "invalid_archive_member", 0, false);
            var entry = zip.GetEntry("repro-manifest.json");
            if (entry is null || entry.Length > 512 * 1024)
                return new(false, "manifest_missing_or_oversize", 0, false);
            byte[] manifestBytes = ReadExactBounded(entry, 512 * 1024);
            if (!string.Equals(Hash(manifestBytes), expectedManifestHash,
                StringComparison.OrdinalIgnoreCase))
                return new(false, "manifest_hash_mismatch", 0, false);
            ReproSourceManifest? manifest =
                JsonSerializer.Deserialize<ReproSourceManifest>(manifestBytes, Json);
            if (manifest is null || manifest.FormatVersion != FormatVersion
                || manifest.Format != "cvolo.private-source-repro"
                || manifest.HashAlgorithm != "sha256"
                || manifest.ReplayComplete || manifest.TransportProducerClosed
                || manifest.ProjectClosureVerified || manifest.SemanticClosureVerified
                || !manifest.ContainsSensitiveSources
                || manifest.Snapshots is null
                || manifest.Snapshots.Count > CaptureConsentState.MaxJournalSnapshots)
                return new(false, "unsupported_or_invalid_manifest", 0, false);
            if (manifestBytes.Length > 512 * 1024
                || zip.Entries.Any(e => e.FullName != "repro-manifest.json"
                    && !manifest.Snapshots.Any(s => s.SourceEntry == e.FullName)))
                return new(false, "unexpected_archive_member", 0, false);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var lastByDocument = new Dictionary<string, string>(StringComparer.Ordinal);
            int verified = 0;
            int replayedEditBatches = 0;
            bool allReplayable = true;
            foreach (var record in manifest.Snapshots)
            {
                if (record.Kind == "did_close")
                {
                    if (record.SourceEntry is not null) return new(false, "close_contains_source", verified, false);
                    lastByDocument.Remove(record.DocumentId);
                    continue;
                }
                if (record.Kind is not ("did_open" or "did_change")
                    || record.SourceEntry is null || record.SourceSha256 is null
                    || !used.Add(record.SourceEntry))
                    return new(false, "invalid_snapshot", verified, false);
                var sourceEntry = zip.GetEntry(record.SourceEntry);
                if (sourceEntry is null || sourceEntry.Length > CaptureConsentState.MaxSnapshotChars * 4L)
                    return new(false, "source_missing_or_oversize", verified, false);
                byte[] bytes = ReadExactBounded(sourceEntry, CaptureConsentState.MaxSnapshotChars * 4);
                if (Hash(bytes) != record.SourceSha256)
                    return new(false, "source_digest_mismatch", verified, false);
                string text = new UTF8Encoding(false, true).GetString(bytes);
                if (CaptureReplayVerifier.ComputeDigest(text) != record.Utf16Sha256)
                    return new(false, "utf16_text_digest_mismatch", verified, false);
                if (record.Kind == "did_open")
                    lastByDocument[record.DocumentId] = text;
                else if (record.Changes is not null
                    && lastByDocument.TryGetValue(record.DocumentId, out string? previous))
                {
                    if (!CaptureEditReplayer.TryReplay(previous, record.Changes, out string replay)
                        || !string.Equals(replay, text, StringComparison.Ordinal))
                        return new(false, "utf16_edit_replay_mismatch", verified, false);
                    lastByDocument[record.DocumentId] = text;
                    replayedEditBatches++;
                }
                else
                {
                    allReplayable = false;
                    lastByDocument[record.DocumentId] = text;
                }
                verified++;
            }
            if (used.Count != zip.Entries.Count - 1)
                return new(false, "unreferenced_archive_member", verified, false);
            // A set of snapshots with zero edit batches cannot prove edit
            // replay correctness, even if every individual digest matches.
            return new(true, null, verified,
                allReplayable && replayedEditBatches > 0);
        }
        catch (Exception error) when (error is InvalidDataException
            or JsonException or IOException or ArgumentException
            or DecoderFallbackException or OverflowException)
        {
            return new(false, "archive_read_failed", 0, false);
        }
    }

    private static byte[] ReadExactBounded(ZipArchiveEntry entry, int maxBytes)
    {
        if (entry.Length > maxBytes) throw new InvalidDataException("member_oversize");
        using var stream = entry.Open();
        using var output = new MemoryStream();
        byte[] bytes = new byte[32 * 1024];
        int n;
        while ((n = stream.Read(bytes)) > 0)
        {
            if (output.Length + n > maxBytes)
                throw new InvalidDataException("expanded_member_oversize");
            output.Write(bytes, 0, n);
        }
        if (output.Length != entry.Length)
            throw new InvalidDataException("member_length_changed");
        return output.ToArray();
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Writes in a private application directory. The final rename is done by
    /// consent state under its short commit lock so revoke and publish have
    /// a single definite order. Only local files; no network operations.
    /// </summary>
    internal static ReproExportAck Store(CaptureConsentState consent, CaptureExportSnapshot data)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cvolo", "Repro");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("reparse_point_export_directory");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ReproBundleBytes bundle = Build(data);
        string output = Path.Combine(directory,
            "cvolo-repro-" + Guid.NewGuid().ToString("N") + ".zip");
        string temporary = output + ".partial";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                file.Write(bundle.Bytes);
                file.Flush(flushToDisk: true);
            }
            if (!consent.CommitExport(data.LeaseId, () => File.Move(temporary, output)))
                return new(1, "rejected", "consent_changed_during_export",
                    null, null, 0, 0, false, false);
            return new(1, "exported_private_source_bundle", null, output,
                bundle.ManifestSha256, bundle.Bytes.Length, bundle.SourceMembers,
                true, false);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed record ReproSourceRecord(
    string DocumentId, string DocumentSessionId, string Kind, int Version,
    long? IngressSequence, string? SourceEntry, string? SourceSha256,
    string Utf16Sha256, IReadOnlyList<DocumentChange>? Changes);

internal sealed record ReproSourceManifest(
    int FormatVersion, string Format, string HashAlgorithm, string TextDigestAlgorithm,
    IReadOnlyList<ReproSourceRecord> Snapshots,
    string TraceCompleteness, string DocumentReplayCompleteness,
    string ProjectInputsCompleteness, string SemanticReplayCompleteness,
    bool ContainsSensitiveSources, bool ReplayComplete,
    bool TransportProducerClosed, bool ProjectClosureVerified,
    bool SemanticClosureVerified, string Scope,
    string CompletenessLimitations);

internal sealed record ReproBundleBytes(
    byte[] Bytes, string ManifestSha256, int Snapshots, int SourceMembers);

internal sealed record ReproBundleVerification(
    bool IntegrityValid, string? Reason, int VerifiedSources,
    bool RetainedEditsReplayed);

internal sealed record CaptureExportSnapshot(
    string LeaseId, IReadOnlyList<CapturedDocumentSnapshot> Snapshots,
    CaptureReplayEvidence Evidence);

internal sealed class ReproExportRequest
{
    public int ProtocolVersion { get; set; }
    public string? ServerInstanceId { get; set; }
    public string? LeaseId { get; set; }
    public bool ExplicitUserConfirmation { get; set; }
}

internal sealed record ReproExportAck(
    int ProtocolVersion, string Status, string? Reason,
    string? BundlePath, string? ManifestSha256,
    int ArchiveBytes, int SourceMembers,
    bool LocalFileCreated, bool CrashDurabilityGuaranteed);
