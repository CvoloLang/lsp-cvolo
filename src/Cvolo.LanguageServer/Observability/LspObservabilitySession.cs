using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Process-owned observability writer. A disk sink is created lazily on the first
/// successful recording activation; the existing verified JSONL writer handles
/// background batches, rotation, loss counters and shutdown flush.
/// </summary>
internal sealed class LspObservabilitySession : IDisposable
{
    internal const string ModeEnvironmentVariable = "CVOLO_OBS_MODE";
    internal const string TracePathEnvironmentVariable = "CVOLO_TRACE_PATH";

    private readonly ObservabilityOptions _options;
    private readonly BoundedEventQueue _queue;
    private readonly IObservabilityClock _clock;
    private readonly object _gate = new();
    private JsonlEventWriter? _writer;
    // A Stop closes the file handle. A later Start must not reuse that
    // disposed writer (Windows also requires this for read/export access).
    private bool _writerStopped;
    private Task<int>? _pendingDrain;
    private bool _drainFaulted;
    // Preserve writer counters across Start/Stop cycles. The queue and observer
    // identity survive a writer restart, so checkpoint loss must survive too.
    private long _previousWriterLost;
    private long _previousWrittenLines;
    private long _previousLastDurableSequence = -1;
    private bool _previousWriterDegraded;
    private string? _writerOpenFailure;
    private int _stopped;

    private LspObservabilitySession(
        CompilerObserver observer, ObservabilityOptions options,
        BoundedEventQueue queue, IObservabilityClock clock, JsonlEventWriter? writer)
    {
        Observer = observer;
        _options = options;
        _queue = queue;
        _clock = clock;
        _writer = writer;
    }

    internal CompilerObserver Observer { get; }

    internal bool SupportsBundleExport => _options.OutputPath is not null;

    /// <summary>
    /// An explicit export request only succeeds after Stop released the writer.
    /// Holding the session gate prevents another Start while source segments
    /// are being snapshotted, hashed and packed into the bundle.
    /// </summary>
    internal RecordingArchiveResult ExportClosedBundle(RecordingCheckpoint checkpoint)
    {
        lock (_gate)
        {
            FinishCompletedDrainLocked();
            if (_options.OutputPath is null)
                throw new InvalidOperationException("no_disk_sink");
            if (Observer.Mode != ObservabilityMode.Off)
                throw new InvalidOperationException("recording_active");
            if (_pendingDrain is not null)
                throw new InvalidOperationException("drain_in_progress");
            if (_writer is null || !_writerStopped)
                throw new InvalidOperationException("writer_not_closed");
            if (_queue.Count != 0)
                throw new InvalidOperationException("events_pending");
            if (Volatile.Read(ref _stopped) != 0)
                throw new InvalidOperationException("session_stopped");

            return RecordingBundleExporter.Write(
                _options.OutputPath, checkpoint, _options.MaxRotatedFiles,
                _options.MaxTotalSizeBytes);
        }
    }

    internal static LspObservabilitySession Start()
    {
        var mode = ParseMode(Environment.GetEnvironmentVariable(ModeEnvironmentVariable));
        var path = Environment.GetEnvironmentVariable(TracePathEnvironmentVariable);
        return Create(mode, path);
    }

    /// <summary>Isolated test factory: does not read or modify process-wide environment.</summary>
    internal static LspObservabilitySession StartForTesting(
        string? outputPath, ObservabilityMode mode = ObservabilityMode.Off)
        => Create(mode, outputPath);

    private static LspObservabilitySession Create(ObservabilityMode mode, string? outputPath)
    {
        var options = new ObservabilityOptions
        {
            Mode = mode,
            Emitter = "lsp",
            OutputPath = string.IsNullOrWhiteSpace(outputPath) ? null : outputPath,
            QueueCapacity = 16384,
        };
        var clock = new SystemObservabilityClock();
        var queue = new BoundedEventQueue(options.QueueCapacity);
        var observer = new CompilerObserver(
            options,
            clock: clock,
            queue: queue,
            catalog: EventCatalog.CreateKernelCatalog());

        JsonlEventWriter? writer = null;
        if (options.OutputPath is not null && mode != ObservabilityMode.Off)
            writer = new JsonlEventWriter(queue, observer.Identity, clock, options);

        var session = new LspObservabilitySession(observer, options, queue, clock, writer);
        session.Emit(new CompilerEvent
        {
            Event = "session.started",
            Kind = CompilerEventKind.Lifecycle,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P0,
            Attributes =
            [
                new("emitter", observer.Identity.Emitter),
                new("sessionId", observer.Identity.SessionId),
                new("serverInstanceId", observer.Identity.ServerInstanceId),
                new("processId", Environment.ProcessId.ToString()),
            ],
        });
        return session;
    }

    internal static ObservabilityMode ParseMode(string? raw)
        => raw?.Trim().ToLowerInvariant() switch
        {
            "basic" => ObservabilityMode.Basic,
            "detailed" => ObservabilityMode.Detailed,
            "repro" => ObservabilityMode.Repro,
            _ => ObservabilityMode.Off,
        };

    /// <summary>
    /// The writer is prepared before a mode is enabled, so the ACK cannot claim
    /// successful disk recording if FileStream.Open failed. Stops gate new
    /// producer events first, then initiate bounded best-effort drain. If the
    /// deadline is exceeded, the caller receives an explicitly partial outcome.
    /// </summary>
    internal RecordingWriterTransition ApplyMode(ObservabilityMode mode)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _stopped) != 0)
                return new(false, false, "session_stopped");

            FinishCompletedDrainLocked();
            if (_pendingDrain is not null)
                return new(false, false, "drain_in_progress");

            if (Observer.Mode == mode)
                return new(true, true, null);

            if (mode != ObservabilityMode.Off)
            {
                if (_options.OutputPath is not null && (_writer is null || _writerStopped))
                {
                    var candidate = new JsonlEventWriter(
                        _queue, Observer.Identity, _clock, _options);
                    if (candidate.IsDegraded)
                    {
                        _writerOpenFailure = candidate.DegradedReason ?? "writer_open_failed";
                        candidate.Dispose();
                        return new(false, false, "writer_open_failed");
                    }
                    if (_writerStopped && _writer is not null)
                    {
                        _previousWriterLost += _writer.WriterLost;
                        _previousWrittenLines += _writer.WrittenLines;
                        _previousLastDurableSequence = Math.Max(
                            _previousLastDurableSequence, _writer.LastDurableProducerSequence);
                        _previousWriterDegraded |= _writer.IsDegraded;
                    }
                    _writer = candidate;
                    _writerStopped = false;
                    _writerOpenFailure = null;
                }

                if (_writer?.IsDegraded == true)
                    return new(false, false, "writer_degraded");

                Observer.SetMode(mode);
                return new(true, true, null);
            }

            // Off is applied before any IO or drain. The producer hot path is
            // no longer writing new L0/L1 events when the ACK is prepared.
            Observer.SetMode(ObservabilityMode.Off);
            if (_writer is null)
                return new(true, true, null);

            var writer = _writer;
            _pendingDrain = Task.Run(writer.Flush);
            bool completed;
            try
            {
                completed = _pendingDrain.Wait(_options.ShutdownFlushDeadline);
            }
            catch (AggregateException)
            {
                completed = true;
                _drainFaulted = true;
            }

            if (!completed)
                return new(true, false, "drain_timeout");

            FinishCompletedDrainLocked();
            if (_drainFaulted || writer.ReplayIncomplete)
                return new(true, false, _drainFaulted ? "writer_flush_faulted" : "writer_incomplete");
            return new(true, true, null);
        }
    }

    internal RecordingWriterStatus GetWriterStatus()
    {
        lock (_gate)
        {
            FinishCompletedDrainLocked();
            return GetWriterStatusLocked();
        }
    }

    private RecordingWriterStatus GetWriterStatusLocked()
    {
        long writerLost = _previousWriterLost + (_writer?.WriterLost ?? 0);
        long writtenLines = _previousWrittenLines + (_writer?.WrittenLines ?? 0);
        long lastSequence = Math.Max(_previousLastDurableSequence,
            _writer?.LastDurableProducerSequence ?? -1);
        long? durableSequence = lastSequence >= 0 ? lastSequence : null;
        bool degraded = _previousWriterDegraded || _drainFaulted ||
            (_writer?.IsDegraded ?? false);

        if (_options.OutputPath is null)
            return new("collector_only", false, false, null, writerLost, writtenLines);
        if (_writerOpenFailure is not null && _writer is null)
            return new("failed", false, true, durableSequence, writerLost, writtenLines);
        if (_writer is null)
            return new("not_started", false, false, durableSequence, writerLost, writtenLines);
        if (_pendingDrain is not null)
            return new("draining", false, true, durableSequence, writerLost, writtenLines);

        bool active = Observer.Mode != ObservabilityMode.Off && !degraded;
        string state = degraded ? "degraded" : active ? "active" : "idle";
        return new(state, active,
            degraded || _writer.ReplayIncomplete || writerLost > 0,
            durableSequence, writerLost, writtenLines);
    }

    private void FinishCompletedDrainLocked()
    {
        if (_pendingDrain is not { IsCompleted: true } task)
            return;

        if (task.IsFaulted || task.IsCanceled)
            _drainFaulted = true;
        _pendingDrain = null;

        // A fully drained recording boundary has to release its file handle.
        // Otherwise Windows cannot read the JSONL for export after Stop ACK,
        // and a subsequent Start would accidentally reuse the stopped writer.
        if (Observer.Mode == ObservabilityMode.Off && _writer is not null && !_writerStopped)
        {
            _writer.Dispose();
            _writerStopped = true;
        }
    }

    /// <summary>Lifecycle records bypass the normal mode gate.</summary>
    internal void Emit(CompilerEvent evt) => Observer.EmitLifecycle(evt);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        Emit(new CompilerEvent
        {
            Event = "session.ended",
            Kind = CompilerEventKind.Lifecycle,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P0,
            Attributes =
            [
                new("sessionId", Observer.Identity.SessionId),
                new("serverInstanceId", Observer.Identity.ServerInstanceId),
            ],
        });

        lock (_gate)
        {
            // JsonlEventWriter.Dispose already implements the configured
            // shutdown flush deadline. Do not dispose it concurrently with a
            // still-running explicit stop drain.
            if (_pendingDrain is { IsCompleted: false } task)
            {
                try { task.Wait(_options.ShutdownFlushDeadline); }
                catch (AggregateException) { _drainFaulted = true; }
            }
            if ((_pendingDrain is null || _pendingDrain.IsCompleted) && !_writerStopped)
            {
                _writer?.Dispose();
                _writerStopped = true;
            }
        }
    }
}

internal readonly record struct RecordingWriterTransition(
    bool Accepted, bool FullyDrained, string? Reason);

internal readonly record struct RecordingWriterStatus(
    string State, bool DiskRecordingActive, bool ReplayIncomplete,
    long? LastDurableProducerSequence,
    long WriterLost = 0, long WrittenLines = 0);
