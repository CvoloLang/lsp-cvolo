using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using System.Collections.Concurrent;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// One diagnostic worker per backend project. Requests made during a burst of edits
/// are coalesced; the newest snapshot wins. Stale snapshots are never published.
/// Background work never blocks the JSON-RPC receive loop.
/// </summary>
internal sealed class DiagnosticScheduler(DocumentStore store, IDiagnosticPublisher publisher, ILspLogger logger) : IDisposable
{
    private const int FailureRecoveryThreshold = 3;
    // Do not parse/resolve the entire project for every intermediate keystroke.
    private static readonly TimeSpan EditQuietPeriod = TimeSpan.FromMilliseconds(350);

    private readonly ConcurrentDictionary<BackendProject, ProjectWorker> _workers = new();
    private readonly ConcurrentDictionary<BackendProject, bool> _unavailable = new();
    private volatile bool _stopped;

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

    private void RunOnce(BackendProject project, ProjectWorker worker)
    {
        DiagnosticRunContext context;
        try
        {
            if (!store.TryCaptureDiagnosticRun(project, out context))
                return;
        }
        catch (Exception ex)
        {
            HandleFailure(project, worker, null, ex);
            return;
        }

        BackendDiagnosticRun run;
        try
        {
            BackendDocumentHandle[] handles = context.Targets.Select(target => target.Document).ToArray();
            run = store.GetDiagnostics(context.CurrentProjectSnapshot, handles);
        }
        catch (Exception ex)
        {
            HandleFailure(project, worker, context, ex);
            return;
        }

        // Successful computation resets failures even when it was superseded.
        worker.ResetFailures();
        if (_unavailable.TryRemove(project, out _))
            logger.Info($"[diag] diagnostics available again for project '{project}' after a successful analysis.");
        if (_stopped || worker.IsStopped)
            return;

        try
        {
            bool committed = store.TryCommitDiagnostics(
                context,
                eligible =>
                {
                    if (!_stopped && !worker.IsStopped)
                        publisher.Publish(context with { Targets = eligible }, run);
                });
            if (!committed)
                logger.Debug($"[diag] discarded stale diagnostics for project '{project}'.");
        }
        catch (Exception ex)
        {
            logger.Warning($"Publishing diagnostics for project '{project}' failed: {ex.Message}");
        }
    }

    private void HandleFailure(BackendProject project, ProjectWorker worker, DiagnosticRunContext? context, Exception ex)
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
            lock (_lock)
            {
                if (_stopped || owner._stopped)
                    return;

                _pending = true;
                _revision++;
                if (_running)
                    return;
                _running = true;
            }
            _ = Task.Run(ProcessAsync);
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
