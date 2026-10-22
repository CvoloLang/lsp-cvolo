using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Packages only the already-closed JSONL stream and its retained rotations.
/// SHA-256 covers each exact uncompressed archive member; a manifest does not
/// prove that earlier rotated-away events ever existed or were preserved.
/// </summary>
internal static class RecordingBundleExporter
{
    internal const int FormatVersion = 1;
    private const int BufferSize = 64 * 1024;

    internal static RecordingArchiveResult Write(
        string configuredTracePath,
        RecordingCheckpoint checkpoint,
        int maxRotatedFiles,
        long maxTotalBytes)
    {
        string tracePath = Path.GetFullPath(configuredTracePath);
        if (!File.Exists(tracePath))
            throw new FileNotFoundException("JSONL trace was not found");

        string outputDirectory = Path.GetDirectoryName(tracePath)!;
        string filename = $"cvolo-diagnostics-{checkpoint.RecordingEpoch}-{Guid.NewGuid():N}.zip";
        string archivePath = Path.Combine(outputDirectory, filename);
        string temporaryPath = archivePath + ".partial";

        // The writer has already released its handles. Enumerate only known
        // rotation suffixes, never arbitrary sibling files, paths or content.
        var sources = new List<string>();
        for (int index = Math.Max(0, maxRotatedFiles); index >= 1; --index)
        {
            string rotated = tracePath + "." + index;
            if (File.Exists(rotated))
                sources.Add(rotated);
        }
        sources.Add(tracePath);

        try
        {
            var segments = new List<RecordingArchiveSegment>(sources.Count);
            long totalBytes = 0;
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    for (int index = 0; index < sources.Count; ++index)
                    {
                        string sourcePath = sources[index];
                        if ((File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) != 0)
                            throw new IOException("Refusing symlinked trace segment");

                        using var input = new FileStream(sourcePath, FileMode.Open,
                            FileAccess.Read, FileShare.Read);
                        long inputLength = input.Length;
                        // Bound the amount of data copied even if another process
                        // modified the log after the writer closed it.
                        if (inputLength < 0 || inputLength > maxTotalBytes - totalBytes)
                            throw new IOException("Trace exceeds the configured retention budget");

                        string entryName = $"events/segment-{index:D4}.jsonl";
                        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                        long copied = 0;
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        using (Stream output = entry.Open())
                        {
                            byte[] buffer = new byte[BufferSize];
                            int n;
                            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                copied += n;
                                if (copied > inputLength)
                                    throw new IOException("Trace segment changed during export");
                                hash.AppendData(buffer, 0, n);
                                output.Write(buffer, 0, n);
                            }
                        }

                        if (copied != inputLength)
                            throw new IOException("Trace segment was truncated during export");

                        segments.Add(new RecordingArchiveSegment(
                            entryName, copied,
                            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                        totalBytes += copied;
                    }

                    // Never put absolute filesystem paths, source text or raw
                    // environment variables in the manifest.
                    var manifest = new
                    {
                        formatVersion = FormatVersion,
                        format = "cvolo.diagnostic-bundle",
                        generatedAtUtc = DateTimeOffset.UtcNow,
                        checkpoint,
                        segments,
                        hashAlgorithm = "sha256",
                        integrityScope = "uncompressed_archive_members",
                        completenessVerified = false,
                        producerBarrierVerified = false,
                        privacyAuditVerified = false,
                        mayContainEarlierOrRotatedAwayGaps = true,
                    };
                    byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                        manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                        {
                            WriteIndented = true,
                        });
                    var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                    using (Stream manifestStream = manifestEntry.Open())
                        manifestStream.Write(manifestBytes);

                    // Computed against exactly the bytes in the archived manifest,
                    // so the caller can independently inspect the returned artifact.
                    string manifestSha256 =
                        Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
                    zip.Dispose();
                    file.Flush(flushToDisk: true);
                    file.Dispose();
                    File.Move(temporaryPath, archivePath);
                    return new RecordingArchiveResult(
                        archivePath, segments.Count, totalBytes, manifestSha256);
                }
            }
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}

internal sealed record RecordingArchiveSegment(
    string Entry, long SizeBytes, string Sha256);

internal sealed record RecordingArchiveResult(
    string Path, int SegmentCount, long TotalUncompressedBytes, string ManifestSha256);
