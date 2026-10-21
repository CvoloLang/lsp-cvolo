using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Cvolo.Observability;
using System.Collections.Concurrent;
using global::Cvolo.Observability.Abstractions;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// One diagnostic worker per backend project. Requests made during a burst of edits
/// are coalesced; the newest snapshot wins. Stale snapshots are never published.
/// Background work never blocks the JSON-RPC receive loop.
/// </summary>
internal sealed class DiagnosticScheduler(
    DocumentStore store,
    IDiagnosticPublisher publisher,
    ILspLogger logger,
    ICompilerObserver? observer = null) : IDisposable
{
    private const int FailureRecoveryThreshold = 3;
    // Do not parse/resolve the entire project for every intermediate keystroke.
    private static readonly TimeSpan EditQuietPeriod = TimeSpan.FromMilliseconds(350);

    private const string DiagnosticsRequestedEvent = "diagnostics.requested";
    private const string DiagnosticsCoalescedEvent = "diagnostics.coalesced";
    private const string WorkerQueuedEvent = "worker.queued";
    private const string WorkerStartedEvent = "worker.started";
    private const string AnalysisStartedEvent = "analysis.started";
    private const string AnalysisCompletedEvent = "analysis.completed";
    private const string DiagnosticsCommittedEvent = "diagnostics.committed";
    private const string DiagnosticsDiscardedEvent = "diagnostics.discarded";

    private const string DiscardReasonStaleResult = "stale_result";
    private const string DiscardReasonNoTargets = "no_open_documents";

    private readonly ICompilerObserver _observer = observer ?? NoopCompilerObserver.Instance;
    private readonly ConcurrentDictionary<BackendProject, ProjectWorker> _workers = new();
    private readonly ConcurrentDictionary<BackendProject, bool> _unavailable = new();
    private volatile bool _stopped;

    // A counter local to a scheduler would generate the same analysis ids in
    // another scheduler in the same process. The runtime's server identity
    // distinguishes restarts and processes; the global counter distinguishes
    // concurrent scheduler instances in that process.
    private static long _globalAnalysisCounter;
    private string? _analysisNamespace;

    public void Schedule(BackendProject project)
    {
        if (_stopped)
            return;

        ProjectWorker worker = _workers.GetOrAdd(project, p => new ProjectWorker(this, p));
        worker.Request();
    }

    /// <summary>
    /// True when the most recent analyses for this project failed repeatedly and
    /// diagnostics are currently unavailable. Host consumers can distinguish this
    /// explicit state from a successful run that legitimately produced no diagnostics.
    /// </summary>
    public bool IsUnavailable(BackendProject project) => _unavailable.ContainsKey(project);

    public void OnProjectDrained(BackendProject project)
    {
        _unavailable.TryRemove(project, out _);
        if (_workers.TryRemove(project, out ProjectWorker? worker))
            worker.Stop();
    }

    public void Dispose()
    {
        _stopped = true;
        foreach (ProjectWorker worker in _workers.Values)
            worker.Stop();
        _workers.Clear();
    }

    /// <summary>
    /// Emits one diagnostic-pipeline event. Only machine outcomes, reasons and the analysis id are
    /// carried — never document text or paths in basic/detailed modes (§39 privacy).
    /// </summary>
    private void Emit(string eventCode, string outcome, string? reason = null, string? analysisId = null, string? snapshotId = null)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0))
            return;

        _observer.Emit(new CompilerEvent
        {
            Event = eventCode,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            Outcome = outcome,
            Reason = reason,
            AnalysisId = analysisId,
            SnapshotId = snapshotId,
        });
    }

    private void RunOnce(BackendProject project, ProjectWorker worker)
    {
        // Disabled observers must not allocate correlation metadata. If recording
        // starts mid-analysis, a missing id is more truthful than a fabricated one.
        string? analysisId = _observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0)
            ? NextAnalysisId()
            : null;
        DiagnosticRunContext context;
        try
        {
            if (!store.TryCaptureDiagnosticRun(project, out context))
            {
                // No open documents project at this revision: an honest discard, not a silent skip.
                Emit(DiagnosticsDiscardedEvent, "discarded", DiscardReasonNoTargets, analysisId);
                return;
            }

            // The immutable context is the correlation authority for every
            // publication, including asynchronous transport completion.
            context = context with { AnalysisId = analysisId };
        }
        catch (Exception ex)
        {
            HandleFailure(project, worker, null, analysisId, ex);
            return;
        }

        BackendDiagnosticRun run;
        try
        {
            BackendDocumentHandle[] handles = context.Targets.Select(target => target.Document).ToArray();
            Emit(AnalysisStartedEvent, "started", analysisId: analysisId, snapshotId: context.SnapshotId);
            run = store.GetDiagnostics(context.CurrentProjectSnapshot, handles);
        }
        catch (Exception ex)
        {
            Emit(AnalysisCompletedEvent, "faulted", ex.GetType().Name, analysisId, context.SnapshotId);
            HandleFailure(project, worker, context, analysisId, ex);
            return;
        }

        Emit(AnalysisCompletedEvent, "success", analysisId: analysisId, snapshotId: context.SnapshotId);

        // Successful computation resets failures even when it was superseded.
        worker.ResetFailures();
        if (_unavailable.TryRemove(project, out _))
            logger.Info($"[diag] diagnostics available again for project '{project}' after a successful analysis.");
        if (_stopped || worker.IsStopped)
        {
            Emit(DiagnosticsDiscardedEvent, "discarded", DiscardReasonStaleResult, analysisId, context.SnapshotId);
            return;
        }

        try
        {
            bool committed = store.TryCommitDiagnostics(
                context,
                eligible =>
                {
                    if (!_stopped && !worker.IsStopped)
                        publisher.Publish(context with { Targets = eligible }, run);
                });
            if (committed)
            {
                Emit(DiagnosticsCommittedEvent, "committed", analysisId: analysisId, snapshotId: context.SnapshotId);
            }
            else
            {
                logger.Debug($"[diag] discarded stale diagnostics for project '{project}'.");
                Emit(DiagnosticsDiscardedEvent, "discarded", DiscardReasonStaleResult, analysisId, context.SnapshotId);
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"Publishing diagnostics for project '{project}' failed: {ex.Message}");
        }
    }

    private void HandleFailure(BackendProject project, ProjectWorker worker, DiagnosticRunContext? context, string? analysisId, Exception ex)
    {
        logger.Error($"Diagnostic analysis failed for project '{project}': {ex.Message}");
        int failures = worker.IncrementFailures();
        if (failures < FailureRecoveryThreshold || context is null || _stopped || worker.IsStopped)
            return;

        _unavailable[project] = true;
        logger.Warning($"Diagnostic analysis failed {failures} times consecutively for project '{project}'; publishing an explicit unavailable state instead of a clean result.");
        try
        {
            store.TryCommitDiagnostics(context, eligible =>
            {
                if (!_stopped && !worker.IsStopped)
                    publisher.PublishUnavailable(eligible);
            });
        }
        catch (Exception publishEx)
        {
            logger.Warning($"Clearing diagnostics for project '{project}' failed: {publishEx.Message}");
        }
    }

    private string NextAnalysisId()
    {
        string? owner = Volatile.Read(ref _analysisNamespace);
        if (owner is null)
        {
            string proposed = (_observer as CompilerObserver)?.Identity.ServerInstanceId
                ?? Guid.NewGuid().ToString("N");
            owner = Interlocked.CompareExchange(ref _analysisNamespace, proposed, null) ?? proposed;
        }

        return $"{owner}/analysis/{Interlocked.Increment(ref _globalAnalysisCounter)}";
    }

    private void LogUnexpectedWorkerFailure(BackendProject project, Exception ex)
    {
        logger.Error($"Diagnostic worker for project '{project}' failed unexpectedly: {ex.Message}");
    }

    private sealed class ProjectWorker(DiagnosticScheduler owner, BackendProject project)
    {
        private readonly Lock _lock = new();
        private bool _pending;
        private bool _running;
        private bool _stopped;
        private int _consecutiveFailures;
        private long _revision;

        public bool IsStopped
        {
            get { lock (_lock) return _stopped; }
        }

        public void Request()
        {
            bool coalesced;
            bool startWorker;
            lock (_lock)
            {
                if (_stopped || owner._stopped)
                    return;

                coalesced = _running;
                _pending = true;
                _revision++;
                startWorker = !_running;
                _running = true;
            }

            owner.Emit(DiagnosticsRequestedEvent, "requested");
            if (coalesced)
            {
                // A run is already in flight; this request only bumps the revision it will observe.
                owner.Emit(DiagnosticsCoalescedEvent, "coalesced");
            }
            else
            {
                owner.Emit(WorkerQueuedEvent, "queued");
            }

            if (startWorker)
            {
                _ = Task.Run(ProcessAsync);
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _stopped = true;
                _pending = false;
                _revision++;
            }
        }

        public void ResetFailures()
        {
            lock (_lock)
                _consecutiveFailures = 0;
        }

        public int IncrementFailures()
        {
            lock (_lock)
                return ++_consecutiveFailures;
        }

        private async Task ProcessAsync()
        {
            try
            {
                while (true)
                {
                    long revision;
                    lock (_lock)
                    {
                        if (!_pending || _stopped || owner._stopped)
                            return;
                        _pending = false;
                        revision = _revision;
                    }

                    // Task.Delay, not Thread.Sleep: no occupied worker thread while typing.
                    await Task.Delay(EditQuietPeriod).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (_stopped || owner._stopped)
                            return;
                        if (revision != _revision)
                            continue;
                    }

                    owner.Emit(WorkerStartedEvent, "started");
                    owner.RunOnce(project, this);
                }
            }
            catch (Exception ex)
            {
                owner.LogUnexpectedWorkerFailure(project, ex);
            }
            finally
            {
                // Requests made during cleanup cannot be stranded on a dead worker.
                bool restart;
                lock (_lock)
                {
                    _running = false;
                    restart = _pending && !_stopped && !owner._stopped;
                    if (restart)
                        _running = true;
                }
                if (restart)
                    _ = Task.Run(ProcessAsync);
            }
        }
    }
}
