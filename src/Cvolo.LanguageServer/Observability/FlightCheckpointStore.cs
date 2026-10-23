using System.Security.Cryptography;
using System.Text.Json;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Bounded, metadata-only incident checkpoint. A completed file is atomically
/// renamed after a flushed write; directory fsync and preservation of an
/// asynchronously pending incident across sudden process exit are NOT proven.
/// This format must never contain RPC arguments, file paths or source text.
/// </summary>
internal sealed class FlightCheckpointStore
{
    internal const int MaxBytes = 64 * 1024;
    internal const int MaxFiles = 8;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string _writeState = "not_started";
    private string? _failure;

    internal FlightCheckpointStore(string? directory = null,
        Func<DateTimeOffset>? clock = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cvolo", "FlightRecorder");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    internal FlightCheckpointStatus Write(FlightRecorderSnapshot snapshot)
    {
        lock (_gate)
        {
            string? temporary = null;
            try
            {
                if (snapshot.IncidentId is null || !snapshot.TriggerRetained
                    || snapshot.SourceContentCaptured)
                    return Fail("no_retained_incident");
                if (snapshot.Events.Count > LspFlightRecorder.MaxEvents)
                    return Fail("too_many_events");

                var events = new List<FlightCheckpointEvent>(snapshot.Events.Count);
                long prior = 0;
                foreach (var e in snapshot.Events)
                {
                    if (e.Sequence <= prior || !IsKnownEvent(new FlightCheckpointEvent(
                    e.Sequence, e.ObservedAtUtc, e.Kind, e.Stage,
                    e.IngressSequence, e.Outcome)))
                        return Fail("unsafe_or_invalid_event");
                    prior = e.Sequence;
                    events.Add(new(e.Sequence, e.ObservedAtUtc, e.Kind, e.Stage,
                        e.IngressSequence, e.Outcome));
                }
                var data = new FlightCheckpointPayload(1, snapshot.IncidentId,
                    snapshot.IncidentReason, snapshot.HistoryPrefixTruncated,
                    snapshot.EvictedEvents, events);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, Json);
                if (bytes.Length > MaxBytes / 2)
                    return Fail("metadata_budget_exceeded");
                string digest = Hash(bytes);
                byte[] checkpoint = JsonSerializer.SerializeToUtf8Bytes(
                    new FlightCheckpointEnvelope(1, digest, data), Json);
                if (checkpoint.Length > MaxBytes)
                    return Fail("checkpoint_budget_exceeded");
                if (!Validate(checkpoint))
                    return Fail("checkpoint_self_validation_failed");

                Directory.CreateDirectory(_directory);
                if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
                    return Fail("unsafe_checkpoint_directory");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_directory,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                string file = Path.Combine(_directory, "flight-" +
                    _clock().UtcTicks.ToString("D19",
                        System.Globalization.CultureInfo.InvariantCulture) + "-" +
                    Guid.NewGuid().ToString("N") + ".json");
                temporary = file + ".partial";
                using (var output = new FileStream(temporary, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    output.Write(checkpoint);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temporary, file);
                temporary = null;
                Prune();
                _writeState = "verified_file_published";
                _failure = null;
                return new(_writeState, null, snapshot.IncidentId,
                    true, true, false, false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or ArgumentException or System.Security.SecurityException
                or JsonException or InvalidDataException)
            {
                return Fail("checkpoint_io_failed");
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    internal FlightCheckpointStatus Inspect()
    {
        lock (_gate)
        {
            return new(_writeState, _failure, null,
                _writeState == "verified_file_published", false, false, false);
        }
    }

    internal FlightCheckpointRecovery RecoverLatest()
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(_directory))
                    return new("no_checkpoint", null, false, false);
                if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
                    return new("unsafe_checkpoint_directory", null, false, false);
                var files = Directory.GetFiles(_directory, "flight-*.json")
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                    .Take(MaxFiles * 4 + 1).ToArray();
                if (files.Length == 0)
                    return new("no_checkpoint", null, false, false);
                foreach (string file in files)
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    var info = new FileInfo(file);
                    if (info.Length > MaxBytes)
                        continue;
                    byte[] bytes = File.ReadAllBytes(file);
                    if (!Validate(bytes))
                        continue;
                    var envelope = JsonSerializer.Deserialize<FlightCheckpointEnvelope>(bytes, Json);
                    return new("verified_metadata_recovered",
                        envelope?.Payload?.IncidentId, true, false);
                }
                return new("no_valid_checkpoint", null, false, false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or ArgumentException or JsonException
                or System.Security.SecurityException)
            {
                return new("recovery_read_failed", null, false, false);
            }
        }
    }

    internal static bool Validate(byte[] checkpoint)
    {
        try
        {
            if (checkpoint.Length is <= 0 or > MaxBytes)
                return false;
            var parsed = JsonSerializer.Deserialize<FlightCheckpointEnvelope>(checkpoint, Json);
            if (parsed is not { ProtocolVersion: 1 }
                || parsed.Payload is not { ProtocolVersion: 1 } data
                || !IsIncidentId(data.IncidentId)
                || data.IncidentReason is not ("manual" or "document_handler_faulted")
                || data.EvictedEvents < 0
                || data.Events is null || data.Events.Count == 0
                || data.Events.Count > LspFlightRecorder.MaxEvents)
                return false;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, Json);
            if (!string.Equals(Hash(bytes), parsed.PayloadSha256,
                StringComparison.Ordinal))
                return false;
            long previous = 0;
            bool trigger = false;
            foreach (var e in data.Events)
            {
                if (e.Sequence <= previous || !IsKnownEvent(e))
                    return false;
                previous = e.Sequence;
                if (e.Kind == "incident" && e.Stage == "trigger"
                    && e.Outcome == data.IncidentReason
                    && data.IncidentId == "incident-" + e.Sequence.ToString("D6",
                        System.Globalization.CultureInfo.InvariantCulture))
                    trigger = true;
            }
            return trigger;
        }
        catch (Exception e) when (e is JsonException or ArgumentException
            or InvalidDataException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsIncidentId(string? id)
        => id is { Length: >= 15 and <= 32 }
            && id.StartsWith("incident-", StringComparison.Ordinal)
            && id.AsSpan(9).IndexOfAnyExceptInRange('0', '9') < 0;

    private static bool IsKnownEvent(FlightCheckpointEvent e)
        => e.Sequence > 0
            && e.IngressSequence is null or > 0
            && (e.Kind, e.Stage, e.Outcome) is
            ("did_open" or "did_change" or "did_close", "transport_read", null) or
            ("did_open" or "did_change" or "did_close", "handler_entered", null) or
            ("did_open" or "did_change" or "did_close", "handler_settled",
                "applied" or "rejected" or "faulted") or
            ("incident", "trigger", "manual" or "document_handler_faulted");

    private void Prune()
    {
        var files = Directory.GetFiles(_directory, "flight-*.json")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
        foreach (string file in files.Skip(MaxFiles))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                File.Delete(file);
        }
    }

    private FlightCheckpointStatus Fail(string reason)
    {
        _writeState = "failed";
        _failure = reason;
        return new("failed", reason, null, false, false, false, false);
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal sealed record FlightCheckpointEvent(
    long Sequence, DateTimeOffset ObservedAtUtc,
    string Kind, string Stage, long? IngressSequence, string? Outcome);

internal sealed record FlightCheckpointPayload(
    int ProtocolVersion, string IncidentId, string? IncidentReason,
    bool HistoryPrefixTruncated, int EvictedEvents,
    IReadOnlyList<FlightCheckpointEvent> Events);

internal sealed record FlightCheckpointEnvelope(
    int ProtocolVersion, string PayloadSha256, FlightCheckpointPayload Payload);

internal sealed record FlightCheckpointStatus(
    string Status, string? FailureReason, string? IncidentId,
    bool FilePublished, bool VerifiedAfterWrite,
    bool PendingWriteSurvivesCrash, bool DirectorySyncProven);

internal sealed record FlightCheckpointRecovery(
    string Status, string? IncidentId, bool ValidCheckpointRecovered,
    bool AllProcessHistoryRecovered);
