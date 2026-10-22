using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// First version of the LSP recording control protocol. This manages the
/// in-process observer only; it does not claim that a disk writer was started,
/// drained or exported. Sensitive capture requires a separate consent protocol.
/// </summary>
internal sealed class RecordingControlPlane(ICompilerObserver observer, LspObservabilitySession? session = null)
{
    internal const int Version = 1;
    internal const string SetMethod = "cvolo/observability/setRecording.v1";
    internal const string StatusMethod = "cvolo/observability/status.v1";
    internal const string CheckpointMethod = "cvolo/observability/checkpoint.v1";
    internal const string ExportMethod = "cvolo/observability/export.v1";
    internal const string MarkerMethod = "cvolo/observability/addMarker.v1";

    private readonly CompilerObserver? _runtime = observer as CompilerObserver;
    private readonly LspObservabilitySession? _session = session;
    private readonly object _gate = new();

    internal bool Supported => _runtime is not null;

    private RecordingWriterStatus WriterStatus =>
        _session?.GetWriterStatus()
        ?? new RecordingWriterStatus("collector_only", false, false, null);

    internal RecordingPolicyStatus GetStatus()
    {
        lock (_gate)
        {
            return new RecordingPolicyStatus(
                Version,
                Supported,
                _runtime?.Mode.ToString().ToLowerInvariant() ?? "off",
                _runtime?.Identity.RecordingEpoch ?? 0,
                _runtime?.Identity.ServerInstanceId,
                CaptureSources: false,
                CapturePaths: false,
                ForensicScope: false,
                ExportSupported: false,
                EffectiveFromSequence: null,
                WriterState: WriterStatus.State,
                DiskRecordingActive: WriterStatus.DiskRecordingActive,
                ReplayIncomplete: WriterStatus.ReplayIncomplete,
                LastDurableProducerSequence: WriterStatus.LastDurableProducerSequence);
        }
    }

    /// <summary>
    /// Read-only, non-durable checkpoint. The last written producer sequence is
    /// a watermark, NOT a gap-free sequence barrier. No trace files or paths
    /// are returned to the client and this does not create an export bundle.
    /// </summary>
    internal RecordingCheckpoint GetCheckpoint()
    {
        lock (_gate)
        {
            var runtime = _runtime;
            QueueStatistics? stats = runtime?.Queue.Statistics;
            int pending = runtime?.Queue.Count ?? 0;
            RecordingWriterStatus writer = WriterStatus;
            var reasons = new List<string>();

            if (runtime is null)
                reasons.Add("observer_unavailable");
            if (_session is null || writer.State == "collector_only")
                reasons.Add("no_disk_sink");
            if (writer.State == "not_started")
                reasons.Add("writer_not_started");
            if (writer.State == "failed")
                reasons.Add("writer_open_failed");
            if (writer.State == "degraded")
                reasons.Add("writer_degraded");
            if (writer.State == "draining")
                reasons.Add("drain_in_progress");
            if (runtime?.Mode != ObservabilityMode.Off)
                reasons.Add("recording_active");
            if (pending > 0)
                reasons.Add("events_pending");
            if (stats?.HasLoss == true)
                reasons.Add("queue_loss");
            if (writer.WriterLost > 0)
                reasons.Add("writer_loss");
            if (writer.ReplayIncomplete &&
                !reasons.Contains("events_pending") &&
                !reasons.Contains("writer_loss") &&
                !reasons.Contains("writer_degraded") &&
                !reasons.Contains("drain_in_progress"))
                reasons.Add("writer_incomplete");
            if (writer.LastDurableProducerSequence is null && _session is not null)
                reasons.Add("no_durable_sequence");

            // Snapshotting these counters does not perform a producer fence or
            // validate the on-disk bytes. In particular a high watermark must
            // never imply replay completeness without loss accounting.
            bool closedForInspection = runtime?.Mode == ObservabilityMode.Off
                && writer.State == "idle" && pending == 0;
            return new RecordingCheckpoint(
                Version,
                DateTimeOffset.UtcNow,
                runtime?.Identity.SessionId,
                runtime?.Identity.ServerInstanceId,
                runtime?.Identity.RecordingEpoch ?? 0,
                runtime?.Mode.ToString().ToLowerInvariant() ?? "off",
                writer.State,
                writer.DiskRecordingActive,
                closedForInspection,
                ReplayIncomplete: reasons.Count > 0,
                IncompleteReasons: reasons.ToArray(),
                PendingEvents: pending,
                EnqueuedEvents: stats?.Enqueued ?? 0,
                DroppedP0: stats?.DroppedP0 ?? 0,
                DroppedP1: stats?.DroppedP1 ?? 0,
                DroppedP2: stats?.DroppedP2 ?? 0,
                WriterLost: writer.WriterLost,
                WrittenLines: writer.WrittenLines,
                LastDurableProducerSequence: writer.LastDurableProducerSequence,
                // Producer sequence barrier, integrity validation, durable
                // fsync and export packaging are deliberately not implemented.
                ProducerBarrierSequence: null,
                ExportSupported: _session?.SupportsBundleExport == true);
        }
    }

    /// <summary>
    /// Explicit user-triggered export of a stopped recording. A client-supplied
    /// server identity and epoch guard against packaging the wrong session.
    /// The destination path is chosen by the server, never by a JSON-RPC input.
    /// </summary>
    internal RecordingExportAck Export(RecordingExportRequest? request, bool initialized)
    {
        lock (_gate)
        {
            RecordingCheckpoint checkpoint = GetCheckpoint();
            RecordingExportAck Reject(string reason) => new(
                Version, "rejected", reason,
                checkpoint.RecordingEpoch, null, 0, 0, null,
                checkpoint.ReplayIncomplete, false);

            if (!initialized)
                return Reject("server_not_initialized");
            if (request is null || request.ProtocolVersion != Version)
                return Reject("unsupported_protocol_version");
            if (_runtime is null)
                return Reject("observer_unavailable");
            if (_session is null || !_session.SupportsBundleExport)
                return Reject("no_disk_sink");
            if (!string.Equals(request.ServerInstanceId,
                    checkpoint.ServerInstanceId, StringComparison.Ordinal))
                return Reject("server_instance_mismatch");
            if (request.ExpectedEpoch != checkpoint.RecordingEpoch)
                return Reject("recording_epoch_mismatch");
            if (checkpoint.EffectiveMode != "off")
                return Reject("recording_active");
            if (!checkpoint.ClosedForInspection)
                return Reject("writer_not_closed");

            try
            {
                RecordingArchiveResult archive = _session.ExportClosedBundle(checkpoint);
                return new RecordingExportAck(
                    Version, "exported", null, checkpoint.RecordingEpoch,
                    archive.Path, archive.SegmentCount,
                    archive.TotalUncompressedBytes, archive.ManifestSha256,
                    checkpoint.ReplayIncomplete, false);
            }
            catch (InvalidOperationException ex) when (ex.Message is
                "recording_active" or "drain_in_progress" or "writer_not_closed" or
                "events_pending" or "session_stopped" or "no_disk_sink")
            {
                return Reject(ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or ArgumentException)
            {
                return Reject("archive_io_failed");
            }
        }
    }

    /// <summary>
    /// P0 marker for a user-observed incident. The category is a fixed machine
    /// word: no free-form note, source, file URI or cursor content is recorded.
    /// ACK means best-effort enqueue, NOT an fsync or a guarantee against loss.
    /// </summary>
    internal RecordingMarkerAck AddMarker(RecordingMarkerRequest? request, bool initialized)
    {
        lock (_gate)
        {
            RecordingMarkerAck Reject(string reason) => new(
                Version, "rejected", reason, null,
                _runtime?.Identity.RecordingEpoch ?? 0, false);

            if (!initialized)
                return Reject("server_not_initialized");
            if (request is null || request.ProtocolVersion != Version)
                return Reject("unsupported_protocol_version");
            if (_runtime is null)
                return Reject("observer_unavailable");
            if (_runtime.Mode == ObservabilityMode.Off)
                return Reject("recording_off");

            string? category = request.Category;
            if (category is not ("definition" or "completion" or "highlighting"
                or "diagnostics" or "performance" or "other"))
                return Reject("unsupported_category");

            string markerId = "marker-" + Guid.NewGuid().ToString("N");
            long epoch = _runtime.Identity.RecordingEpoch;
            _runtime.EmitLifecycle(new CompilerEvent
            {
                Event = "diagnostic.marker",
                Kind = CompilerEventKind.Lifecycle,
                Detail = DiagnosticDetail.L0,
                Priority = EventPriority.P0,
                Outcome = "added",
                ClientActionId = markerId,
                Attributes = [new("category", category)],
            });
            return new RecordingMarkerAck(
                Version, "queued_best_effort", null, markerId, epoch, false);
        }
    }

    internal RecordingPolicyAck Apply(RecordingPolicyRequest? request, bool initialized)
    {
        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            string requested = request?.Mode?.Trim().ToLowerInvariant() ?? "";
            RecordingPolicyAck Reject(string reason) =>
                Ack("rejected", reason, requested, requestedAt, appliedAt: null);

            if (!initialized)
                return Reject("server_not_initialized");

            if (request is null || request.ProtocolVersion != Version)
                return Reject("unsupported_protocol_version");

            if (_runtime is null)
                return Reject("observer_unavailable");

            // Recording level is NOT permission to capture source code, paths
            // or forensic material. These requests have no effect on the mode.
            if (request.CaptureSources || request.CapturePaths || request.ForensicScope)
                return Reject("consent_required");

            if (requested == "repro")
                return Reject("forensic_consent_required");

            ObservabilityMode mode;
            switch (requested)
            {
                case "off":
                    mode = ObservabilityMode.Off;
                    break;
                case "basic":
                    mode = ObservabilityMode.Basic;
                    break;
                case "detailed":
                    mode = ObservabilityMode.Detailed;
                    break;
                default:
                    return Reject("unsupported_mode");
            }

            // CompilerObserver is the single owner of the epoch and of the
            // transition event. No parallel counter is maintained here.
            RecordingWriterTransition transition;
            if (_session is not null)
                transition = _session.ApplyMode(mode);
            else
            {
                _runtime.SetMode(mode);
                transition = new RecordingWriterTransition(true, true, null);
            }

            if (!transition.Accepted)
                return Reject(transition.Reason ?? "writer_unavailable");

            return Ack(transition.FullyDrained ? "applied" : "partially_applied",
                transition.Reason, requested, requestedAt, DateTimeOffset.UtcNow);
        }
    }

    private RecordingPolicyAck Ack(string status, string? reason, string requested,
        DateTimeOffset requestedAt, DateTimeOffset? appliedAt) =>
        new(
            Version,
            status,
            reason,
            requested,
            _runtime?.Mode.ToString().ToLowerInvariant() ?? "off",
            _runtime?.Identity.RecordingEpoch ?? 0,
            _runtime?.Identity.ServerInstanceId,
            CaptureSources: false,
            CapturePaths: false,
            ForensicScope: false,
            // The current runtime does not expose a producer-sequence barrier.
            // Explicitly unknown is safer than inventing one.
            EffectiveFromSequence: null,
            requestedAt,
            appliedAt,
            WriterState: WriterStatus.State,
            DiskRecordingActive: WriterStatus.DiskRecordingActive,
            ReplayIncomplete: WriterStatus.ReplayIncomplete,
            LastDurableProducerSequence: WriterStatus.LastDurableProducerSequence);
}

internal sealed class RecordingPolicyRequest
{
    public int ProtocolVersion { get; set; }
    public string? Mode { get; set; }
    public bool CaptureSources { get; set; }
    public bool CapturePaths { get; set; }
    public bool ForensicScope { get; set; }
}

internal sealed record RecordingPolicyStatus(
    int ProtocolVersion,
    bool Supported,
    string EffectiveMode,
    long RecordingEpoch,
    string? ServerInstanceId,
    bool CaptureSources,
    bool CapturePaths,
    bool ForensicScope,
    bool ExportSupported,
    long? EffectiveFromSequence,
    string WriterState,
    bool DiskRecordingActive,
    bool ReplayIncomplete,
    long? LastDurableProducerSequence);

internal sealed record RecordingPolicyAck(
    int ProtocolVersion,
    string Status,
    string? Reason,
    string RequestedMode,
    string EffectiveMode,
    long RecordingEpoch,
    string? ServerInstanceId,
    bool CaptureSources,
    bool CapturePaths,
    bool ForensicScope,
    long? EffectiveFromSequence,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? AppliedAtUtc,
    string WriterState,
    bool DiskRecordingActive,
    bool ReplayIncomplete,
    long? LastDurableProducerSequence);

internal sealed record RecordingCheckpoint(
    int ProtocolVersion,
    DateTimeOffset CapturedAtUtc,
    string? SessionId,
    string? ServerInstanceId,
    long RecordingEpoch,
    string EffectiveMode,
    string WriterState,
    bool DiskRecordingActive,
    bool ClosedForInspection,
    bool ReplayIncomplete,
    string[] IncompleteReasons,
    int PendingEvents,
    long EnqueuedEvents,
    long DroppedP0,
    long DroppedP1,
    long DroppedP2,
    long WriterLost,
    long WrittenLines,
    long? LastDurableProducerSequence,
    long? ProducerBarrierSequence,
    bool ExportSupported);

internal sealed class RecordingExportRequest
{
    public int ProtocolVersion { get; set; }
    public string? ServerInstanceId { get; set; }
    public long ExpectedEpoch { get; set; }
}

/// <summary>
/// The manifest hashes archive members but does not certify replay
/// completeness, source redaction or historical retention.
/// </summary>
internal sealed record RecordingExportAck(
    int ProtocolVersion,
    string Status,
    string? Reason,
    long RecordingEpoch,
    string? ArchivePath,
    int SegmentCount,
    long TotalUncompressedBytes,
    string? ManifestSha256,
    bool ReplayIncomplete,
    bool CompletenessVerified);

internal sealed class RecordingMarkerRequest
{
    public int ProtocolVersion { get; set; }
    public string? Category { get; set; }
}

internal sealed record RecordingMarkerAck(
    int ProtocolVersion,
    string Status,
    string? Reason,
    string? MarkerId,
    long RecordingEpoch,
    bool Durable);
