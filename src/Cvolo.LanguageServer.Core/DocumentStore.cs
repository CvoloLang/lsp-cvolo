using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Editing;
using Cvolo.LanguageServer.Core.Instrumentation;
using Cvolo.LanguageServer.Core.Logging;
using Cvolo.LanguageServer.Core.Scheduling;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using global::Cvolo.Observability.Abstractions;

namespace Cvolo.LanguageServer.Core;

/// <summary>
/// Thread-safe home for the open documents of one server session. Every open
/// document lives under an immutable, atomically-published entry so readers
/// never observe text without a matching backend snapshot. Mutations for the
/// same document are serialized on a per-uri gate; different documents are
/// independent.
/// </summary>
internal sealed class DocumentStore(
    ILanguageBackend backend,
    ICoreLogger? logger = null,
    ServerMetrics? metrics = null,
    ICompilerObserver? observer = null)
{
    private const string DocumentUpdateReceivedEvent = "document.update.received";
    private const string DocumentUpdateAppliedEvent = "document.update.applied";
    private const string DocumentUpdateRejectedEvent = "document.update.rejected";
    private const string SnapshotCaptureStartedEvent = "snapshot.capture.started";
    private const string SnapshotCaptureCompletedEvent = "snapshot.capture.completed";
    private const string SnapshotFreshnessCheckedEvent = "snapshot.freshness_checked";

    private const string CaptureKindRequest = "request";
    private const string CaptureKindSemanticEdit = "semantic_edit";
    private const string CaptureKindDiagnosticRun = "diagnostic_run";

    private const string CaptureOutcomeSuccess = "success";
    private const string CaptureOutcomeNotFound = "not_found";
    private const string FreshnessOutcomeFresh = "fresh";
    private const string FreshnessOutcomeStale = "stale";

    private readonly ICoreLogger _logger = logger ?? new NullCoreLogger();
    private readonly ServerMetrics? _metrics = metrics;
    private readonly ICompilerObserver _observer = observer ?? NoopCompilerObserver.Instance;
    private readonly ConcurrentDictionary<DocumentUri, DocumentEntry> _documents = new();
    private readonly ConcurrentDictionary<DocumentUri, object> _gates = new();
    private readonly SemaphoreSlim _interactiveQueryGate = new(1, 1);

    // Serializes document/project advancement against diagnostic publication so
    // that a result can only be committed while all freshness conditions still
    // hold atomically (see TryCommitDiagnostics).
    private readonly Lock _publicationGate = new();

    private void LogTiming(string operation, Stopwatch stopwatch, string detail, int thresholdMs = 50)
    {
        if (stopwatch.ElapsedMilliseconds >= thresholdMs)
            _logger.Write(CoreLogLevel.Info, $"[perf] {operation} took {stopwatch.ElapsedMilliseconds} ms: {detail}");
        else
            _logger.Write(CoreLogLevel.Debug, $"[perf] {operation} took {stopwatch.ElapsedMilliseconds} ms: {detail}");
    }

    /// <summary>
    /// Emits one document-update event when observability is enabled. Emitting only enqueues a
    /// small immutable record; serialization happens later on the writer thread, so this never
    /// performs I/O or formatting while a store lock is held.
    /// </summary>
    private void EmitUpdate(string eventCode, string outcome, string? reason, int version)
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
            DocumentVersion = version,
        });
    }

    public ICollection<DocumentUri> OpenUris => _documents.Keys;

    /// <summary>
    /// Emits the start half of a snapshot-capture span. The operation id pairs this event with the
    /// matching completion emitted by <see cref="EmitCaptureCompleted"/>.
    /// </summary>
    private IOperationScope EmitCaptureStarted(string captureKind, int version)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0))
            return NoopOperationScope.Instance;

        return _observer.BeginOperation(new OperationStart
        {
            Event = SnapshotCaptureStartedEvent,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            Attributes = new[] { new KeyValuePair<string, string>("captureKind", captureKind) },
            DocumentVersion = version,
        });
    }

    /// <summary>Emits the completion half of a snapshot-capture span with a machine outcome.</summary>
    private void EmitCaptureCompleted(IOperationScope scope, string captureKind, string outcome, DocumentVersion version)
    {
        if (ReferenceEquals(scope, NoopOperationScope.Instance))
            return;

        scope.Complete(
            outcome,
            attributes: new[]
            {
                new KeyValuePair<string, string>("captureKind", captureKind),
                new KeyValuePair<string, string>("documentVersion", version.Value.ToString(CultureInfo.InvariantCulture)),
            });
    }

    /// <summary>
    /// Emits one freshness-check event. Freshness runs on every publication path, so the event is
    /// L1 detail and never carries reasons beyond the fresh/stale outcome.
    /// </summary>
    private void EmitFreshnessChecked(string outcome, long? version)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L1))
            return;

        _observer.Emit(new CompilerEvent
        {
            Event = SnapshotFreshnessCheckedEvent,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L1,
            Priority = EventPriority.P2,
            Outcome = outcome,
            DocumentVersion = version,
        });
    }

    private IInputBarrier? _inputBarrier;

    /// <summary>
    /// Read barrier for ordered input publication (INC "LSP Request Scheduling").
    /// Assigned by the session composition; null when handlers are invoked directly
    /// (for example in unit tests without a transport).
    /// </summary>
    public IInputBarrier? InputBarrier
    {
        get => Volatile.Read(ref _inputBarrier);
        set => Volatile.Write(ref _inputBarrier, value);
    }

    /// <summary>
    /// Bounded wait for this request's earlier admitted inputs. Returns false when
    /// the barrier times out so the caller can answer with a recoverable
    /// non-success instead of guessing at semantics.
    /// </summary>
    public async Task<bool> WaitForInputsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        IInputBarrier? barrier = Volatile.Read(ref _inputBarrier);
        if (barrier is null)
        {
            return true;
        }

        IngressSequence sequence = IngressScope.Current;
        if (sequence.IsNone)
        {
            return true;
        }

        BarrierOutcome outcome = await barrier
            .WaitForInputsBeforeAsync(sequence, timeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome == BarrierOutcome.Satisfied;
    }

    /// <summary>
    /// Opens a document with the editor's authoritative text. Returns null and
    /// leaves state unchanged when the document is already open (duplicate),
    /// cannot be hosted by the backend, or the backend rejects it.
    /// </summary>
    public DocumentState? Open(DocumentUri uri, string languageId, int version, string? text)
    {
        var stopwatch = Stopwatch.StartNew();
        var gate = _gates.GetOrAdd(uri, static _ => new object());
        lock (gate)
        {
            // Short input transaction: reject duplicate opens without holding the
            // publication gate while the backend discovers the project, reads
            // files and parses (INC S1: no Tooling call or I/O in publication
            // gates). The per-uri gate serializes concurrent opens of one uri.
            lock (_publicationGate)
            {
                _metrics?.RecordPublicationGateHold(stopwatch.ElapsedTicks);
                if (_documents.ContainsKey(uri))
                {
                    _logger.Write(CoreLogLevel.Warning, $"Duplicate didOpen ignored for '{uri}'.");
                    return null;
                }
            }

            BackendProject? project = backend.OpenProject(uri);
            if (project is null)
            {
                _logger.Write(CoreLogLevel.Warning, $"No backend project for '{uri}'; didOpen suppressed.");
                return null;
            }

            if (!backend.TryResolveDocument(project, uri, out BackendDocumentHandle? handle))
            {
                _logger.Write(CoreLogLevel.Error, $"Backend could not resolve '{uri}' in its project; didOpen suppressed.");
                return null;
            }

            var effectiveText = text ?? string.Empty;
            BackendSnapshot snapshot = backend.UpdateDocument(project, handle, effectiveText);

            // Publish the opened document in a second short transaction.
            var publishWatch = Stopwatch.StartNew();
            lock (_publicationGate)
            {
                if (_documents.ContainsKey(uri))
                {
                    _logger.Write(CoreLogLevel.Warning, $"Duplicate didOpen ignored for '{uri}'.");
                    _metrics?.RecordPublicationGateHold(publishWatch.ElapsedTicks);
                    return null;
                }

                var state = new DocumentState(
                    uri,
                    DocumentSessionId.Next(),
                    new DocumentVersion(version),
                    languageId,
                    effectiveText,
                    handle,
                    snapshot);
                _documents[uri] = new DocumentEntry(state, project, handle);
                _logger.Write(CoreLogLevel.Info, $"Opened '{uri}' v{version} ({languageId}).");
                _metrics?.RecordPublicationGateHold(publishWatch.ElapsedTicks);
                LogTiming("DocumentStore.Open", stopwatch, $"uri='{uri}', version={version}, textLength={effectiveText.Length}", 100);
                _metrics?.RecordDidOpen();
                return state;
            }
        }
    }

    /// <summary>
    /// Applies a didChange payload to an open document. Returns null without
    /// touching state when the document is not open, the version is stale
    /// (not strictly greater than the current open version), or any change in
    /// the payload is invalid. The whole payload is transactional.
    /// </summary>
    public DocumentState? ApplyChanges(DocumentUri uri, int version, IReadOnlyList<DocumentChange> changes)
    {
        var stopwatch = Stopwatch.StartNew();
        EmitUpdate(DocumentUpdateReceivedEvent, "received", null, version);
        var gate = _gates.GetOrAdd(uri, static _ => new object());
        lock (gate)
        {
            // Short input transaction: validate the payload against the current
            // revision and record the base revision. The publication lock is held
            // only long enough to read the authoritative state, never across any
            // tooling, filesystem or package work (INC "Incremental Semantic
            // Snapshots", S1: no Tooling call or I/O inside a publication gate).
            DocumentState currentState;
            BackendProject project;
            BackendDocumentHandle handle;
            lock (_publicationGate)
            {
                if (!_documents.TryGetValue(uri, out DocumentEntry? current))
                {
                    _logger.Write(CoreLogLevel.Warning, $"didChange for unopened document '{uri}' ignored.");
                    EmitUpdate(DocumentUpdateRejectedEvent, "rejected", "document_not_open", version);
                    return null;
                }

                currentState = current.State;
                if (version <= currentState.Version.Value)
                {
                    _logger.Write(CoreLogLevel.Warning, $"Stale didChange v{version} for '{uri}' (current v{currentState.Version.Value}) ignored.");
                    EmitUpdate(DocumentUpdateRejectedEvent, "rejected", "stale_version", version);
                    return null;
                }

                project = current.Project;
                handle = current.Handle;
                _metrics?.RecordPublicationGateHold(stopwatch.ElapsedTicks);
            }

            if (!IncrementalTextEditor.TryApplyChanges(currentState.Text, changes, out string newText))
            {
                _logger.Write(CoreLogLevel.Warning, $"Invalid range in didChange v{version} for '{uri}'; nothing applied.");
                EmitUpdate(DocumentUpdateRejectedEvent, "rejected", "invalid_change", version);
                return null;
            }

            // The backend advance (WithDocument / dependency closure discovery) runs
            // outside the publication lock so it can never stall input publication,
            // diagnostic commits or other documents.
            BackendSnapshot snapshot = backend.UpdateDocument(project, handle, newText);

            // Publish text, version and snapshot together in a second short
            // transaction. If the revision moved (for example a concurrent
            // baseline sync for the same document), the advance is abandoned
            // rather than published over newer state.
            var publishWatch = Stopwatch.StartNew();
            lock (_publicationGate)
            {
                if (!_documents.TryGetValue(uri, out DocumentEntry? latest) || latest.State.Version != currentState.Version)
                {
                    _logger.Write(CoreLogLevel.Debug, $"Discarded didChange v{version} for '{uri}': revision advanced during advance.");
                    _metrics?.RecordPublicationGateHold(publishWatch.ElapsedTicks);
                    EmitUpdate(DocumentUpdateRejectedEvent, "rejected", "revision_advanced", version);
                    return null;
                }

                DocumentState next = currentState with
                {
                    Version = new DocumentVersion(version),
                    Text = newText,
                    BackendSnapshot = snapshot,
                };
                _documents[uri] = latest with { State = next };
                _logger.Write(CoreLogLevel.Debug, $"Changed '{uri}' v{version}: {changes.Count} change(s).");
                _metrics?.RecordDidChange();
                _metrics?.RecordSnapshotAdvance();
                _metrics?.RecordPublicationGateHold(publishWatch.ElapsedTicks);
                LogTiming("DocumentStore.ApplyChanges", stopwatch, $"uri='{uri}', version={version}, changes={changes.Count}, textLength={newText.Length}", 50);
                EmitUpdate(DocumentUpdateAppliedEvent, "applied", null, version);
                return next;
            }
        }
    }

    /// <summary>
    /// Closes a document: removes its overlay, restores the project baseline,
    /// and invalidates its session lifetime. Tolerates close-for-unopened.
    /// Returns the backend project the document belonged to, or null when it
    /// was not open.
    /// </summary>
    public BackendProject? Close(DocumentUri uri)
    {
        var stopwatch = Stopwatch.StartNew();
        var gate = _gates.GetOrAdd(uri, static _ => new object());
        lock (gate)
        {
            // Short input transaction: remove the overlay and capture the backend
            // handles, then restore the baseline outside the publication gate so
            // the backend's disk/parse work cannot stall input publication.
            BackendProject project;
            BackendDocumentHandle handle;
            lock (_publicationGate)
            {
                _metrics?.RecordPublicationGateHold(stopwatch.ElapsedTicks);
                if (!_documents.TryRemove(uri, out DocumentEntry? current))
                {
                    _logger.Write(CoreLogLevel.Warning, $"didClose for unopened document '{uri}' ignored.");
                    return null;
                }

                project = current.Project;
                handle = current.Handle;
            }

            try
            {
                backend.RestoreBaseline(project, handle);
            }
            catch (Exception ex)
            {
                _logger.Write(CoreLogLevel.Error, $"Restoring baseline for '{uri}' failed: {ex.Message}");
            }

            _logger.Write(CoreLogLevel.Info, $"Closed '{uri}'.");
            LogTiming("DocumentStore.Close", stopwatch, $"uri='{uri}'", 100);
            _metrics?.RecordDidClose();
            return project;
        }
    }

    public bool TryGet(DocumentUri uri, out DocumentState state)
    {
        if (_documents.TryGetValue(uri, out DocumentEntry? current))
        {
            state = current.State;
            return true;
        }

        state = null!;
        return false;
    }

    /// <summary>
    /// Atomically captures the coherent semantic request context (state, its
    /// backend snapshot, the owning project and the project's current snapshot)
    /// for an open document. Reads never observe text and a backend snapshot
    /// from different publications.
    /// </summary>
    public bool TryCapture(DocumentUri uri, out SemanticRequestContext context)
    {
        IOperationScope capture = EmitCaptureStarted(CaptureKindRequest, 0);
        var stopwatch = Stopwatch.StartNew();
        lock (_publicationGate)
        {
            if (_documents.TryGetValue(uri, out DocumentEntry? current))
            {
                DocumentState state = current.State;
                // Interactive requests must not scan every closed source file under the publication gate.
                // Closed-file synchronization still runs for diagnostic and semantic-edit captures.
                BackendSnapshot currentProjectSnapshot = backend.CaptureCurrentSnapshot(current.Project);
                context = new SemanticRequestContext(
                    state.Uri,
                    state.SessionId,
                    state.Version,
                    state,
                    current.Project,
                    currentProjectSnapshot);
                LogTiming("DocumentStore.TryCapture", stopwatch, $"uri='{uri}', version={state.Version}, textLength={state.Text.Length}", 50);
                EmitCaptureCompleted(capture, CaptureKindRequest, CaptureOutcomeSuccess, state.Version);
                return true;
            }

            context = default;
        }

        EmitCaptureCompleted(capture, CaptureKindRequest, CaptureOutcomeNotFound, default);
        return false;
    }

    /// <summary>
    /// Atomically captures a semantic request and every currently open document in the same backend
    /// project. Rename uses this to guarantee that all affected documents are open and versioned.
    /// </summary>
    public bool TryCaptureSemanticEdit(DocumentUri uri, out SemanticEditRequestContext context)
    {
        IOperationScope capture = EmitCaptureStarted(CaptureKindSemanticEdit, 0);
        var stopwatch = Stopwatch.StartNew();
        lock (_publicationGate)
        {
            if (!_documents.TryGetValue(uri, out DocumentEntry? current))
            {
                context = null!;
                EmitCaptureCompleted(capture, CaptureKindSemanticEdit, CaptureOutcomeNotFound, default);
                return false;
            }

            DocumentState state = current.State;
            BackendSnapshot currentProjectSnapshot = CaptureSynchronizedSnapshot(current.Project);
            var semantic = new SemanticRequestContext(
                state.Uri,
                state.SessionId,
                state.Version,
                state,
                current.Project,
                currentProjectSnapshot);

            var open = new Dictionary<DocumentUri, OpenDocumentEditState>(DocumentUriPathComparer.Instance);
            foreach (var pair in _documents)
            {
                if (!ReferenceEquals(pair.Value.Project, current.Project))
                    continue;

                var openState = pair.Value.State;
                open[openState.Uri] = new OpenDocumentEditState(
                    openState.Uri,
                    openState.SessionId,
                    openState.Version,
                    pair.Value.Handle);
            }

            context = new SemanticEditRequestContext(semantic, open);
            LogTiming("DocumentStore.TryCaptureSemanticEdit", stopwatch, $"uri='{uri}', version={state.Version}, openDocuments={open.Count}", 100);
            EmitCaptureCompleted(capture, CaptureKindSemanticEdit, CaptureOutcomeSuccess, state.Version);
            return true;
        }
    }

    private BackendSnapshot CaptureSynchronizedSnapshot(BackendProject project)
    {
        var stopwatch = Stopwatch.StartNew();
        var openHandles = _documents.Values
            .Where(entry => ReferenceEquals(entry.Project, project))
            .Select(entry => entry.Handle)
            .ToArray();
        BackendSnapshot snapshot = backend.SynchronizeClosedDocuments(project, openHandles);
        LogTiming("DocumentStore.CaptureSynchronizedSnapshot", stopwatch, $"openHandles={openHandles.Length}", 100);
        return snapshot;
    }

    /// <summary>Returns the backend project hosting an open document.</summary>
    public bool TryGetProject(DocumentUri uri, out BackendProject project)
    {
        if (_documents.TryGetValue(uri, out DocumentEntry? current))
        {
            project = current.Project;
            return true;
        }

        project = null!;
        return false;
    }

    /// <summary>
    /// Captures a diagnostic run for <paramref name="project"/>: the current
    /// snapshot plus every currently open document of that project. Returns
    /// false when the project has no open documents.
    /// </summary>
    public bool TryCaptureDiagnosticRun(BackendProject project, out DiagnosticRunContext context)
    {
        IOperationScope capture = EmitCaptureStarted(CaptureKindDiagnosticRun, 0);
        var stopwatch = Stopwatch.StartNew();
        lock (_publicationGate)
        {
            BackendSnapshot snapshot = CaptureSynchronizedSnapshot(project);
            var targets = new List<DiagnosticPublishTarget>();
            foreach (var pair in _documents)
            {
                if (!ReferenceEquals(pair.Value.Project, project))
                {
                    continue;
                }

                DocumentState state = pair.Value.State;
                targets.Add(new DiagnosticPublishTarget(state.Uri, state.SessionId, state.Version, pair.Value.Handle));
            }

            context = new DiagnosticRunContext(project, snapshot, targets);
            LogTiming("DocumentStore.TryCaptureDiagnosticRun", stopwatch, $"targets={targets.Count}", 100);
            bool captured = targets.Count > 0;
            EmitCaptureCompleted(
                capture,
                CaptureKindDiagnosticRun,
                captured ? CaptureOutcomeSuccess : CaptureOutcomeNotFound,
                default);
            return captured;
        }
    }

    /// <summary>Whether <paramref name="project"/> still has any open document.</summary>
    public bool HasOpenDocuments(BackendProject project)
    {
        foreach (var pair in _documents)
        {
            if (ReferenceEquals(pair.Value.Project, project))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Freshness check for a single publication target: the URI must still be
    /// open, in the same session lifetime and version, under the same project.
    /// </summary>
    public bool IsCurrent(DocumentUri uri, DocumentSessionId sessionId, DocumentVersion version, BackendProject project)
    {
        bool current = IsCurrentCore(uri, sessionId, version, project);
        EmitFreshnessChecked(current ? FreshnessOutcomeFresh : FreshnessOutcomeStale, version.Value);
        return current;    }

    private bool IsCurrentCore(DocumentUri uri, DocumentSessionId sessionId, DocumentVersion version, BackendProject project)
    {
        return _documents.TryGetValue(uri, out DocumentEntry? entry)
            && entry.State.SessionId == sessionId
            && entry.State.Version == version
            && ReferenceEquals(entry.Project, project);
    }

    /// <summary>Delegates the project-generation check to the backend.</summary>
    public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
    {
        bool current = backend.IsCurrentSnapshot(project, snapshot);
        EmitFreshnessChecked(current ? FreshnessOutcomeFresh : FreshnessOutcomeStale, null);
        return current;
    }

    /// <summary>
    /// Whole-context freshness for a captured semantic request: the document
    /// must still be open in the same session, version and project, and the
    /// captured project snapshot must still be the backend's current snapshot.
    /// A result computed from a context that fails this check must be discarded
    /// (§10, §16.6, §30).
    /// </summary>
    public bool IsCurrent(SemanticRequestContext context)
    {
        // Uses the non-emitting core so one whole-context check emits exactly one
        // freshness event instead of one per condition.
        bool current = IsCurrentCore(context.Uri, context.SessionId, context.Version, context.Project)
            && backend.IsCurrentSnapshot(context.Project, context.CurrentProjectSnapshot);
        EmitFreshnessChecked(current ? FreshnessOutcomeFresh : FreshnessOutcomeStale, context.Version.Value);
        return current;    }

    /// <summary>
    /// Runs one expensive interactive compiler query at a time and drops stale requests before they
    /// enter tooling. VS Code can issue completion, hover and semantic-token requests concurrently;
    /// letting every stale request compute makes typing latency much worse than one slow query.
    /// </summary>
    public async Task<T?> RunInteractiveQueryAsync<T>(SemanticRequestContext context, string operation, Func<T> query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var stopwatch = Stopwatch.StartNew();
        await _interactiveQueryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LogTiming("DocumentStore.InteractiveQuery(wait)", stopwatch, $"operation={operation}, uri='{context.Uri}', version={context.Version}", 50);
            if (!IsCurrent(context))
            {
                _logger.Write(CoreLogLevel.Debug, $"[perf] skipped stale {operation} before backend query: uri='{context.Uri}', version={context.Version}");
                _metrics?.RecordInteractiveQuerySkippedStale();
                return default;
            }

            if (_metrics is not null && !backend.IsAnalysisReady(context.Project))
            {
                _metrics.RecordInteractiveQueryOnPendingGeneration();
            }

            stopwatch.Restart();
            T result = await Task.Run(query, cancellationToken).ConfigureAwait(false);
            LogTiming("DocumentStore.InteractiveQuery(run)", stopwatch, $"operation={operation}, uri='{context.Uri}', version={context.Version}", 50);
            _metrics?.RecordInteractiveQueryRun();
            return result;
        }
        finally
        {
            _interactiveQueryGate.Release();
        }
    }

    /// <summary>
    /// Atomically commits a captured diagnostic run. Under the store's
    /// publication gate the captured project snapshot is re-validated as current
    /// and the targets are filtered to those still fresh (same URI, session,
    /// version and project). <paramref name="commit"/> is invoked with the
    /// still-eligible targets while the gate is held, so document/project
    /// advancement cannot interleave between validation and publication.
    /// Returns false when the captured snapshot is no longer current, in which
    /// case the whole result must be discarded and nothing is committed.
    /// </summary>
    public bool TryCommitDiagnostics(DiagnosticRunContext context, Action<IReadOnlyList<DiagnosticPublishTarget>> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var stopwatch = Stopwatch.StartNew();
        lock (_publicationGate)
        {
            if (!backend.IsCurrentSnapshot(context.Project, context.CurrentProjectSnapshot))
            {
                _metrics?.RecordDiagnosticCommitDiscarded();
                return false;
            }

            var eligible = new List<DiagnosticPublishTarget>(context.Targets.Count);
            foreach (DiagnosticPublishTarget target in context.Targets)
            {
                if (IsCurrent(target.Uri, target.SessionId, target.Version, context.Project))
                {
                    eligible.Add(target);
                }
            }

            commit(eligible);
            LogTiming("DocumentStore.TryCommitDiagnostics", stopwatch, $"targets={context.Targets.Count}, eligible={eligible.Count}", 50);
            _metrics?.RecordDiagnosticCommit();
            return true;
        }
    }

    /// <summary>Computes diagnostics for a captured run from one snapshot.</summary>
    public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
    {
        return backend.GetDiagnostics(snapshot, targets);
    }

    /// <summary>
    /// Computes backend-neutral completion candidates for the document captured
    /// in <paramref name="context"/> at <paramref name="position"/> (an absolute
    /// UTF-16 code-unit offset) from the document's own coherent snapshot, so
    /// the replacement span is always valid over the exact text the position
    /// refers to (§17, §30). Callers must discard the result unless
    /// <see cref="IsCurrent(SemanticRequestContext)"/> still holds.
    /// </summary>
    public BackendCompletionResult GetCompletions(SemanticRequestContext context, int position)
    {
        return backend.GetCompletions(context.CurrentProjectSnapshot, context.Document.BackendDocument, position);
    }

    /// <summary>Returns signature help from the coherent project snapshot captured for the request.</summary>
    public BackendSignatureHelpResult? GetSignatureHelp(SemanticRequestContext context, int position)
    {
        return backend.GetSignatureHelp(context.CurrentProjectSnapshot, context.Document.BackendDocument, position);
    }

    /// <summary>
    /// Resolves the lazily-requested fields of one completion item against the exact captured
    /// project snapshot. A handle from a foreign snapshot yields null. Callers must verify the
    /// context is still current and the store entry is still valid before and after resolving.
    /// </summary>
    public BackendCompletionResolvedInfo? ResolveCompletion(SemanticRequestContext context, BackendCompletionResolveHandle handle)
    {
        return backend.ResolveCompletion(context.CurrentProjectSnapshot, handle);
    }

    /// <summary>
    /// Resolves the symbol at <paramref name="position"/> from the document's own
    /// coherent snapshot captured in <paramref name="context"/>. Callers must
    /// discard the result unless <see cref="IsCurrent(SemanticRequestContext)"/>
    /// still holds.
    /// </summary>
    public BackendSymbolInfo? GetSymbolAtPosition(SemanticRequestContext context, int position)
    {
        return backend.GetSymbolAtPosition(context.CurrentProjectSnapshot, context.Document.BackendDocument, position);
    }

    /// <summary>
    /// Resolves the source declarations of a symbol handle against the captured
    /// project snapshot. A handle from a foreign snapshot yields an empty result.
    /// </summary>
    public BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol)
    {
        return backend.GetDefinitions(snapshot, symbol);
    }

    public BackendReferenceResult GetReferences(BackendSnapshot snapshot, BackendSymbolHandle symbol, bool includeDeclaration)
    {
        return backend.GetReferences(snapshot, symbol, includeDeclaration);
    }

    /// <summary>
    /// Returns the concrete places that satisfy a contract symbol against the captured project
    /// snapshot. A handle from a foreign snapshot or a symbol that is not a contract yields no target.
    /// </summary>
    public BackendDefinitionResult? GetImplementations(BackendSnapshot snapshot, BackendSymbolHandle symbol)
    {
        return backend.GetImplementations(snapshot, symbol);
    }

    /// <summary>
    /// Returns the contract hierarchy item (interface or protocol) declared at
    /// <paramref name="position"/> in the document captured in <paramref name="context"/>. The result
    /// is snapshot-scoped; callers must honor <see cref="IsCurrent(SemanticRequestContext)"/>.
    /// </summary>
    public BackendTypeHierarchyResult? PrepareTypeHierarchy(SemanticRequestContext context, int position)
    {
        return backend.PrepareTypeHierarchy(context.CurrentProjectSnapshot, context.Document.BackendDocument, position);
    }

    /// <summary>
    /// Returns the declared base contracts of the contract named by <paramref name="key"/> against the
    /// snapshot. The anchor document only establishes which snapshot to read; the key carries the
    /// contract identity, so a stale snapshot-scoped handle never has to survive a generation change.
    /// </summary>
    public BackendTypeHierarchyResult GetSupertypes(BackendSnapshot snapshot, DocumentUri document, string key)
    {
        return backend.GetSupertypes(snapshot, document, key);
    }

    /// <summary>
    /// Returns the directly derived contracts of the contract named by <paramref name="key"/> against
    /// the snapshot. Declared contract inheritance only; structural conformers are not children.
    /// </summary>
    public BackendTypeHierarchyResult GetSubtypes(BackendSnapshot snapshot, DocumentUri document, string key)
    {
        return backend.GetSubtypes(snapshot, document, key);
    }

    public BackendRenamePreparation? PrepareRename(SemanticRequestContext context, int position)
    {
        return backend.PrepareRename(context.CurrentProjectSnapshot, context.Document.BackendDocument, position);
    }

    public BackendRenameResult RenameSymbol(BackendSnapshot snapshot, BackendSymbolHandle symbol, string newName)
    {
        return backend.RenameSymbol(snapshot, symbol, newName);
    }

    /// <summary>
    /// Returns the declaration outline of the document captured in
    /// <paramref name="context"/> from its own coherent snapshot. Callers must
    /// discard the result unless <see cref="IsCurrent(SemanticRequestContext)"/>
    /// still holds.
    /// </summary>
    public IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(SemanticRequestContext context)
    {
        return backend.GetDocumentSymbols(context.CurrentProjectSnapshot, context.Document.BackendDocument);
    }

    /// <summary>
    /// Returns the semantic tokens of the document captured in <paramref name="context"/> from its
    /// own coherent snapshot. Callers must discard the result unless
    /// <see cref="IsCurrent(SemanticRequestContext)"/> still holds.
    /// </summary>
    public BackendSemanticTokenResult GetSemanticTokens(SemanticRequestContext context)
    {
        return backend.GetSemanticTokens(context.CurrentProjectSnapshot, context.Document.BackendDocument);
    }

    /// <summary>
    /// Computes code fixes for the capture's project snapshot. Callers must verify the capture is
    /// still current before and after the call.
    /// </summary>
    public BackendCodeFixResult GetCodeFixes(SemanticEditRequestContext context, TextSpan range)
    {
        return backend.GetCodeFixes(context.Semantic.CurrentProjectSnapshot, context.Semantic.Document.BackendDocument, range);
    }

    /// <summary>
    /// Resolves a code fix handle against the capture's project snapshot. Callers must verify the
    /// capture is still current before and after the call.
    /// </summary>
    public BackendCodeFixResolution ResolveCodeFix(SemanticEditRequestContext context, BackendCodeFixHandle fix)
    {
        return backend.ResolveCodeFix(context.Semantic.CurrentProjectSnapshot, fix);
    }

    /// <summary>
    /// Returns the whole-document annotation batch from the project snapshot captured for the
    /// request. Callers must discard the result unless <see cref="IsCurrent(SemanticRequestContext)"/>
    /// still holds.
    /// </summary>
    public IReadOnlyList<BackendCodeLensInfo> GetCodeLenses(SemanticRequestContext context, BackendCodeLensOptions? options = null)
    {
        return backend.GetCodeLenses(context.CurrentProjectSnapshot, context.Document.BackendDocument, options);
    }

    /// <summary>
    /// Returns the inline annotations inside <paramref name="requestedRange"/> from the project
    /// snapshot captured for the request. Callers must discard the result unless
    /// <see cref="IsCurrent(SemanticRequestContext)"/> still holds.
    /// </summary>
    public IReadOnlyList<BackendInlayHint> GetInlayHints(SemanticRequestContext context, TextSpan requestedRange, BackendInlayHintOptions? options = null)
    {
        return backend.GetInlayHints(context.CurrentProjectSnapshot, context.Document.BackendDocument, requestedRange, options);
    }

    /// <summary>
    /// Returns the semantic occurrences of the binding at <paramref name="position"/> from the
    /// project snapshot captured for the request. Callers must discard the result unless
    /// <see cref="IsCurrent(SemanticRequestContext)"/> still holds.
    /// </summary>
    public IReadOnlyList<BackendDocumentHighlight> GetDocumentHighlights(SemanticRequestContext context, int position)
    {
        return backend.GetDocumentHighlights(context.CurrentProjectSnapshot, context.Document.BackendDocument, position);
    }

    /// <summary>
    /// Returns the foldable regions from the project snapshot captured for the request. Callers must
    /// discard the result unless <see cref="IsCurrent(SemanticRequestContext)"/> still holds.
    /// </summary>
    public IReadOnlyList<BackendFoldingRange> GetFoldingRanges(SemanticRequestContext context)
    {
        return backend.GetFoldingRanges(context.CurrentProjectSnapshot, context.Document.BackendDocument);
    }

    /// <summary>
    /// Returns one smart-selection chain per requested position from the project snapshot captured
    /// for the request. Callers must discard the result unless
    /// <see cref="IsCurrent(SemanticRequestContext)"/> still holds.
    /// </summary>
    public IReadOnlyList<BackendSelectionRange?> GetSelectionRanges(SemanticRequestContext context, IReadOnlyList<int> positions)
    {
        return backend.GetSelectionRanges(context.CurrentProjectSnapshot, context.Document.BackendDocument, positions);
    }

    /// <summary>
    /// Returns the object layout of the type at <paramref name="position"/> from the project snapshot
    /// captured for the request. Callers must discard the result unless
    /// <see cref="IsCurrent(SemanticRequestContext)"/> still holds.
    /// </summary>
    public BackendTypeLayoutInspection? GetTypeLayoutAtPosition(SemanticRequestContext context, int position)
    {
        return backend.GetTypeLayoutAtPosition(context.CurrentProjectSnapshot, context.Document.BackendDocument, position);
    }

    /// <summary>
    /// Returns the object layout of the type named by <paramref name="subject"/>, re-resolved against
    /// the project snapshot captured for the request, or null when the subject no longer names a type.
    /// Callers must discard the result unless <see cref="IsCurrent(SemanticRequestContext)"/> still holds.
    /// </summary>
    public BackendTypeLayoutInspection? GetTypeLayoutBySubject(SemanticRequestContext context, string subject)
    {
        return backend.GetTypeLayoutBySubject(context.CurrentProjectSnapshot, context.Document.BackendDocument, subject);
    }

    /// <summary>
    /// Returns the source declarations of the semantic type of the value or expression at
    /// <paramref name="position"/> from the project snapshot captured for the request. Callers must
    /// discard the result unless <see cref="IsCurrent(SemanticRequestContext)"/> still holds.
    /// </summary>
    public BackendDefinitionResult? GetTypeDefinitions(SemanticRequestContext context, int position)
    {
        return backend.GetTypeDefinitions(context.CurrentProjectSnapshot, context.Document.BackendDocument, position);
    }

    /// <summary>Ties together the published state and the backend tokens that produced it.</summary>
    private sealed record DocumentEntry(DocumentState State, BackendProject Project, BackendDocumentHandle Handle);
}
