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
