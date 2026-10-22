using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Instrumentation;
using Cvolo.LanguageServer.Core.Scheduling;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Logging;
using Cvolo.LanguageServer.Observability;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using global::Cvolo.Observability.Abstractions;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// JSON-RPC target for the language server lifecycle. An exception thrown by
/// one handler is contained by StreamJsonRpc (converted to a JSON-RPC error
/// response) and never kills the process when the protocol state is valid.
/// </summary>
internal sealed class LanguageServer(
    TerminationRequest termination,
    ILspLogger logger,
    IClientProcessWatcher clientWatcher,
    Func<DocumentStore>? storeFactory = null,
    ICompilerObserver? observer = null,
    LspObservabilitySession? recordingSession = null) : IDisposable
{
    private const string QuickFixKind = "quickfix";

    /// <summary>
    /// Session-scoped observer. Defaults to the shared no-op observer so standalone hosts and
    /// unit tests keep working without any telemetry runtime (§29.4, §30).
    /// </summary>
    internal ICompilerObserver Observer { get; } = observer ?? NoopCompilerObserver.Instance;

    private const string LspStage = "lifecycle";

    private void EmitLsp(string eventCode, string outcome, string? reason = null)
    {
        if (!Observer.IsEnabled(CompilerEventKind.Lifecycle, DiagnosticDetail.L0))
            return;

        Observer.Emit(new CompilerEvent
        {
            Event = eventCode,
            Stage = LspStage,
            Kind = CompilerEventKind.Lifecycle,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            Outcome = outcome,
            Reason = reason,
        });
    }

    private readonly SessionState _state = new();
    private RecordingControlPlane? _recordingControl;
    private RecordingControlPlane RecordingControl => GetOrCreate(ref _recordingControl, () => new RecordingControlPlane(Observer, recordingSession));
    private readonly DiagnosticSink _diagnostics = new(logger);
    private readonly CancellationTokenSource _sessionCancellation = new();

    /// <summary>
    /// The ingress boundary for this session (INC "LSP Request Scheduling"). The
    /// transport composition sets it before the endpoint starts listening; request
    /// handlers read it to wait at the input read barrier. Null in unit contexts
    /// that drive handlers directly.
    /// </summary>
    private IngressPipeline? _ingress;

    /// <summary>
    /// S0 instrumentation for the input/analysis pipeline (INC "Incremental Semantic Snapshots").
    /// Measurement only; it never influences scheduling or publication.
    /// </summary>
    private readonly ServerMetrics _serverMetrics = new();

    /// <summary>
    /// Guards publication of every session-scoped member below. Notifications and requests are
    /// dispatched on their own threads, so two callers can reach a member for the first time at the
    /// same moment; without this gate each would build and keep its own instance.
    /// </summary>
    private readonly object _sessionGate = new();

    private DocumentStore? _store;
    private CompletionResolveStore? _resolveStore;
    private TextDocumentSyncHandler? _sync;    private CompletionHandler? _completion;
    private SignatureHelpHandler? _signatureHelp;
    private HoverHandler? _hover;
    private DefinitionHandler? _definition;
    private TypeDefinitionHandler? _typeDefinition;
    private ImplementationHandler? _implementation;
    private PrepareTypeHierarchyHandler? _prepareTypeHierarchy;
    private TypeHierarchySupertypesHandler? _typeHierarchySupertypes;
    private TypeHierarchySubtypesHandler? _typeHierarchySubtypes;
    private DocumentSymbolHandler? _documentSymbols;
    private SemanticTokensHandler? _semanticTokens;
    private ReferencesHandler? _references;
    private RenameHandler? _rename;
    private CodeActionResolveStore? _codeActionResolveStore;
    private CodeActionHandler? _codeActions;
    private CodeLensHandler? _codeLenses;
    private InlayHintHandler? _inlayHints;
    private DocumentHighlightHandler? _documentHighlights;
    private FoldingRangeHandler? _foldingRanges;
    private SelectionRangeHandler? _selectionRanges;
    private TypeLayoutHandler? _typeLayout;
    private ConfigurationHandler? _configuration;
    private EditorIntelligenceRefreshCoordinator? _editorRefresh;
    private SemanticTokensRefreshCoordinator? _refresh;
    private AnchoredLensHandler? _anchoredLens;
    private readonly AnchoredActionRegistry _anchoredActions = new();
    // The process cannot exit until the invalid initialize response is written
    // to the RPC transport. A wall-clock timer races the response on busy CI.
    private int _exitAfterInitializeErrorResponse;

    /// <summary>
    /// Server-to-client diagnostic notification channel. The transport attaches
    /// the JSON-RPC endpoint once it exists.
    /// </summary>
    internal DiagnosticSink Diagnostics => _diagnostics;

    /// <summary>
    /// Session ingress boundary. Assigned by the transport composition root before
    /// the JSON-RPC endpoint starts listening.
    /// </summary>
    internal IngressPipeline? Ingress
    {
        get => Volatile.Read(ref _ingress);
        set => Volatile.Write(ref _ingress, value);
    }

    /// <summary>
    /// Publishes exactly one instance of a session-scoped member. The fast path is a volatile read;
    /// the slow path re-checks under the gate and returns the field rather than a caller's own
    /// temporary, so every caller ends up with the same instance. <c>??=</c> cannot be used here
    /// because it returns the value the calling thread just built, which silently forks the member
    /// when two threads are first at once.
    /// </summary>
    private T GetOrCreate<T>(ref T? slot, Func<T> factory) where T : class
    {
        T? published = Volatile.Read(ref slot);
        if (published is not null)
        {
            return published;
        }

        lock (_sessionGate)
        {
            return slot ??= factory();
        }
    }

    /// <summary>
    /// Session-scoped document store shared by every semantic handler. The
    /// workspace folders and fallback root from initialize are fixed for the
    /// session, so the store is built once on first use.
    /// </summary>
    internal DocumentStore Store => GetOrCreate(ref _store, () =>
    {
        DocumentStore store = CreateStore();
        store.InputBarrier = Ingress?.Inputs;
        return store;
    });

    /// <summary>
    /// Text document synchronization (didOpen/didChange/didClose) handler. Ordinary edits do NOT
    /// trigger a global client refresh: each editor request is answered from the current snapshot at
    /// request time, and proactive refresh is reserved for a committed readiness transition
    /// (Pending→Ready) so a typing burst never produces one global refresh per keystroke.
    /// </summary>
    internal TextDocumentSyncHandler Sync => GetOrCreate(
        ref _sync,
        () => new TextDocumentSyncHandler(
            logger,
            _diagnostics,
            () => Store,
            null,
            () => Ingress?.Inputs,
            Observer,
            (uri, version, text, isDidOpen) =>
                RecordingControl.CaptureCommittedDocument(uri, version, text, isDidOpen)));

    /// <summary>
    /// Session-scoped, bounded store of completion-resolve entries guarding opaque data tokens.
    /// </summary>
    internal CompletionResolveStore ResolveStore => GetOrCreate(ref _resolveStore, () => new CompletionResolveStore());

    /// <summary>
    /// textDocument/completion and completionItem/resolve handler.
    /// </summary>
    internal CompletionHandler Completion => GetOrCreate(
        ref _completion,
        () => new CompletionHandler(
            logger,
            () => Store,
            () => _state.CompletionSnippetSupport,
            () => _state.CompletionResolveSupportProperties!,
            () => _state.CompletionDocumentationMarkdown,
            () => ResolveStore,
            () => _state.EditorIntelligence.InsertArgumentPlaceholders,
            Observer));

    /// <summary>textDocument/signatureHelp handler.</summary>
    internal SignatureHelpHandler SignatureHelp => GetOrCreate(
        ref _signatureHelp,
        () => new SignatureHelpHandler(
            logger,
            () => Store,
            () => _state.SignatureHelpLabelOffsetSupport,
            () => _state.SignatureHelpActiveParameterSupport,
            Observer));

    /// <summary>
    /// textDocument/hover handler.
    /// </summary>
    internal HoverHandler Hover => GetOrCreate(ref _hover, () => new HoverHandler(logger, () => Store, () => _state.HoverPrefersMarkdown, Observer));

    /// <summary>
    /// textDocument/definition handler.
    /// </summary>
    internal DefinitionHandler Definition => GetOrCreate(ref _definition, () => new DefinitionHandler(logger, () => Store, Observer));

    internal TypeDefinitionHandler TypeDefinition => GetOrCreate(ref _typeDefinition, () => new TypeDefinitionHandler(logger, () => Store));

    internal ImplementationHandler Implementation => GetOrCreate(ref _implementation, () => new ImplementationHandler(logger, () => Store));

    internal PrepareTypeHierarchyHandler PrepareTypeHierarchy => GetOrCreate(ref _prepareTypeHierarchy, () => new PrepareTypeHierarchyHandler(logger, () => Store));

    internal TypeHierarchySupertypesHandler TypeHierarchySupertypes => GetOrCreate(ref _typeHierarchySupertypes, () => new TypeHierarchySupertypesHandler(logger, () => Store));

    internal TypeHierarchySubtypesHandler TypeHierarchySubtypes => GetOrCreate(ref _typeHierarchySubtypes, () => new TypeHierarchySubtypesHandler(logger, () => Store));

    /// <summary>
    /// textDocument/documentSymbol handler.
    /// </summary>
    internal DocumentSymbolHandler DocumentSymbols => GetOrCreate(ref _documentSymbols, () => new DocumentSymbolHandler(logger, () => Store, () => _state.HierarchicalDocumentSymbols));

    /// <summary>
    /// textDocument/semanticTokens/full handler.
    /// </summary>
    internal SemanticTokensHandler SemanticTokens => GetOrCreate(ref _semanticTokens, () => new SemanticTokensHandler(logger, () => Store, () => _state.SemanticTokenTypes, () => _state.SemanticTokenModifiers, () => _state.SemanticTokens.MaxInteractiveTextLength, Observer));

    /// <summary>textDocument/references handler.</summary>
    internal ReferencesHandler References => GetOrCreate(ref _references, () => new ReferencesHandler(logger, () => Store, Observer));

    /// <summary>textDocument/prepareRename and textDocument/rename handler.</summary>
    internal RenameHandler Rename => GetOrCreate(ref _rename, () => new RenameHandler(logger, () => Store, () => _state.DocumentChangesSupported, Observer));

    /// <summary>Session-scoped, bounded store of lazy code-action resolve entries.</summary>
    internal CodeActionResolveStore CodeActionResolveStore => GetOrCreate(ref _codeActionResolveStore, () => new CodeActionResolveStore());

    /// <summary>Session-scoped, bounded store of opaque anchored-lens tokens.</summary>
    internal AnchoredActionRegistry AnchoredActions => _anchoredActions;

    /// <summary>textDocument/codeAction and codeAction/resolve handler.</summary>
    internal CodeActionHandler CodeActions => GetOrCreate(
        ref _codeActions,
        () => new CodeActionHandler(
            logger,
            () => Store,
            () => _state.DocumentChangesSupported,
            () => _state.LazyCodeActionEditResolveSupported,
            () => CodeActionResolveStore));

    /// <summary>
    /// Server-to-client semantic-token refresh coordinator.
    /// </summary>
    internal SemanticTokensRefreshCoordinator Refresh => GetOrCreate(
        ref _refresh,
        () => new SemanticTokensRefreshCoordinator(
            () => _state.SemanticTokensEnabled && _state.RefreshSupported,
            logger));

    /// <summary>textDocument/codeLens handler.</summary>
    internal CodeLensHandler CodeLenses => GetOrCreate(
        ref _codeLenses,
        () => new CodeLensHandler(
            logger,
            () => Store,
            () => _state.EditorIntelligence,
            _anchoredActions,
            () => _state.AnchoredLensSupported));

    /// <summary>textDocument/inlayHint handler.</summary>
    internal InlayHintHandler InlayHints => GetOrCreate(ref _inlayHints, () => new InlayHintHandler(logger, () => Store, () => _state.EditorIntelligence, Observer));

    /// <summary>textDocument/documentHighlight handler.</summary>
    internal DocumentHighlightHandler DocumentHighlights => GetOrCreate(ref _documentHighlights, () => new DocumentHighlightHandler(logger, () => Store));

    /// <summary>textDocument/foldingRange handler.</summary>
    internal FoldingRangeHandler FoldingRanges => GetOrCreate(ref _foldingRanges, () => new FoldingRangeHandler(logger, () => Store));

    /// <summary>textDocument/selectionRange handler.</summary>
    internal SelectionRangeHandler SelectionRanges => GetOrCreate(ref _selectionRanges, () => new SelectionRangeHandler(logger, () => Store));

    /// <summary>The <c>cvolo/typeLayout</c> request behind the editor's read-only layout view.</summary>
    internal TypeLayoutHandler TypeLayout => GetOrCreate(ref _typeLayout, () => new TypeLayoutHandler(logger, () => Store));

    /// <summary>
    /// workspace/executeCommand for anchored CodeLens actions and the cvolo/validateAnchoredAction.v3
    /// step that precedes performing one.
    /// </summary>
    internal AnchoredLensHandler AnchoredLens => GetOrCreate(
        ref _anchoredLens,
        () => new AnchoredLensHandler(logger, () => Store, _anchoredActions));

    /// <summary>workspace/didChangeConfiguration handler for the editor intelligence settings.</summary>
    internal ConfigurationHandler Configuration => GetOrCreate(
        ref _configuration,
        () => new ConfigurationHandler(
            logger,
            settings => _state.TryApplyConfiguration(settings),
            () => EditorIntelligenceRefresh.RequestRefresh(),
            Observer));

    /// <summary>
    /// Server-to-client CodeLens and inlay-hint refresh coordinator. A refresh follows a concrete
    /// event, such as a settings change; it is never scheduled on a timer and never polls.
    /// </summary>
    internal EditorIntelligenceRefreshCoordinator EditorIntelligenceRefresh => GetOrCreate(
        ref _editorRefresh,
        () => new EditorIntelligenceRefreshCoordinator(
            () => _state.CodeLensRefreshSupported,
            () => _state.InlayHintRefreshSupported,
            logger));

    public InitializeResponse Initialize(InitializeRequestParams? initializeParams)
    {
        if (initializeParams is null)
        {
            throw new LocalRpcException("Invalid initialize parameters.")
            {
                ErrorCode = (int)JsonRpcErrorCode.InvalidParams,
            };
        }

        if (_state.InitializeReceived)
        {
            throw new LocalRpcException("The server is already initialized.")
            {
                ErrorCode = (int)JsonRpcErrorCode.InvalidRequest,
            };
        }

        if (initializeParams.ProcessId is { } clientPid)
        {
            try
            {
                if (!clientWatcher.IsProcessAlive(clientPid))
                {
                    logger.Error($"Client process {clientPid} is not alive; refusing the session.");
                    Volatile.Write(ref _exitAfterInitializeErrorResponse, 1);
                    throw new LocalRpcException($"Client process {clientPid} is not alive.")
                    {
                        ErrorCode = (int)JsonRpcErrorCode.InvalidParams,
                    };
                }

                if (clientWatcher.TryWatch(clientPid, _sessionCancellation.Token, OnClientTerminated))
                {
                    logger.Info($"Watching client process {clientPid}.");
                }
                else
                {
                    logger.Info($"Could not watch client process {clientPid}; continuing best-effort.");
                }
            }
            catch (LocalRpcException)
            {
                // Confirmed client death at initialization.
                throw;
            }
            catch (Exception ex)
            {
                // Watcher/probe INFRASTRUCTURE failure is not confirmed client
                // death: degrade the session (no watching, keep it alive) rather
                // than terminating it.
                logger.Error($"Client process watcher unavailable for PID {clientPid}; continuing without watching: {ex.Message}");
            }
        }

        _state.MarkInitializeReceived(initializeParams);
        logger.Info($"Semantic tokens: enabled={_state.SemanticTokensEnabled}, refresh={_state.RefreshSupported}, tokenTypes={_state.SemanticTokenTypes.Length}, tokenModifiers={_state.SemanticTokenModifiers.Length}.");
        logger.Debug($"[perf] semantic tokens legend: types=[{string.Join(",", _state.SemanticTokenTypes)}], modifiers=[{string.Join(",", _state.SemanticTokenModifiers)}].");
        logger.Debug($"[perf] editor intelligence: codeLensRefresh={_state.CodeLensRefreshSupported}, inlayHintRefresh={_state.InlayHintRefreshSupported}.");
        logger.Info($"Anchored lenses: supported={_state.AnchoredLensSupported}.");
        _diagnostics.SetRelatedInformationSupported(
            initializeParams.Capabilities?.TextDocument?.PublishDiagnostics?.RelatedInformation == true);
        _diagnostics.SetVersionSupported(
            initializeParams.Capabilities?.TextDocument?.PublishDiagnostics?.VersionSupport == true);
        logger.Info("Client initialized.");
        EmitLsp("capabilities.negotiated", "negotiated");

        return new InitializeResponse(
            new ServerCapabilities
            {
                TextDocumentSync = new TextDocumentSyncOptions
                {
                    OpenClose = true,
                    Change = TextDocumentSyncKind.Incremental,
                },
                CompletionProvider = new CompletionOptions
                {
                    ResolveProvider = true,
                    // `.`/`~` are the original member/destructor triggers. `{` and `,` are added so a
                    // struct initializer surfaces its fields as the user types (`Window {`, after a
                    // completed field). Field completion still only activates inside a recognized
                    // aggregate literal; unrelated braces/commas return the ordinary expression list.
                    TriggerCharacters = [".", "~", "{", ","],
                },
                SignatureHelpProvider = new SignatureHelpOptions
                {
                    TriggerCharacters = ["(", ","],
                },
                HoverProvider = true,
                DefinitionProvider = true,
                TypeDefinitionProvider = true,
                ImplementationProvider = true,
                TypeHierarchyProvider = true,
                ReferencesProvider = true,
                RenameProvider = _state.PrepareRenameSupported ? (object)new RenameOptionsPayload(true) : true,
                DocumentSymbolProvider = true,
                CodeActionProvider = _state.CodeActionLiteralSupported
                    ? new CodeActionOptions
                    {
                        CodeActionKinds = [QuickFixKind],
                        ResolveProvider = _state.LazyCodeActionEditResolveSupported,
                    }
                    : null,
                SemanticTokensProvider = _state.SemanticTokensEnabled
                    ? new SemanticTokensOptions
                    {
                        Legend = new SemanticTokensLegend
                        {
                            TokenTypes = _state.SemanticTokenTypes,
                            TokenModifiers = _state.SemanticTokenModifiers,
                        },
                        Full = true,
                        Range = false,
                    }
                    : null,
                CodeLensProvider = new CodeLensOptions
                {
                    ResolveProvider = false,
                },
                InlayHintProvider = true,
                DocumentHighlightProvider = true,
                FoldingRangeProvider = true,
                SelectionRangeProvider = true,
                ExecuteCommandProvider = new ExecuteCommandOptions([AnchoredLensHandler.ResolveCommand]),
                Experimental = new
                {
                    cvoloAnchoredLens = new AnchoredLensCapability(AnchoredLensHandler.SchemaVersion, ValidateAction: true),
                    cvoloObservability = RecordingControl.Supported ? new
                    {
                        version = RecordingControlPlane.Version,
                        setRecording = RecordingControlPlane.SetMethod,
                        getStatus = RecordingControlPlane.StatusMethod,
                        getCheckpoint = RecordingControlPlane.CheckpointMethod,
                        export = RecordingControlPlane.ExportMethod,
                        addMarker = RecordingControlPlane.MarkerMethod,
                        grantCaptureConsent = RecordingControlPlane.GrantConsentMethod,
                        revokeCaptureConsent = RecordingControlPlane.RevokeConsentMethod,
                        getCaptureConsentStatus = RecordingControlPlane.ConsentStatusMethod,
                        armCaptureJournal = RecordingControlPlane.ArmJournalMethod,
                        verifyCaptureJournal = RecordingControlPlane.VerifyJournalMethod,
                        inMemoryCaptureJournalSupported = true,
                        sourceExportSupported = false,
                        capturePipelineSupported = false,
                        supportedModes = new[] { "off", "basic", "detailed" },
                        captureSourcesSupported = false,
                        exportSupported = recordingSession?.SupportsBundleExport == true,
                    } : null,
                },
            },
            new ServerInfo(ServerMetadata.ServerName, ServerMetadata.ServerVersion));
    }

    public object? Shutdown(object? _ = null)
    {
        if (!_state.InitializeReceived)
        {
            throw ServerNotInitialized("The server has not been initialized.");
        }

        _state.MarkShutdownReceived();
        _anchoredActions.Clear();
        DisarmClientWatcher();
        logger.Info("Shutdown received; awaiting exit. Watcher disarmed.");
        return null;
    }

    public void Exit()
    {
        DisarmClientWatcher();
        if (_state.ShutdownReceived)
        {
            termination.TryRequestNormal();
            logger.Info("Exit after shutdown; exiting cleanly.");
        }
        else
        {
            termination.TryRequestAbnormal();
            logger.Info("Exit without shutdown; exiting abnormally.");
        }
    }

    public void Initialized(InitializedParams? _)
    {
        if (!_state.InitializeReceived)
        {
            // An 'initialized' before 'initialize' must not mutate SessionState:
            // ignore it and keep the session usable.
            logger.Error("initialized received before initialize; ignoring (session stays usable, state not mutated).");
            return;
        }

        _state.MarkInitialized();
        logger.Info("initialized notification received.");
        EmitLsp("client.initialized", "initialized");
    }

    /// <summary>Returns effective state; no mode change or implied persistence.</summary>
    [JsonRpcMethod(RecordingControlPlane.StatusMethod)]
    public RecordingPolicyStatus GetRecordingStatus() => RecordingControl.GetStatus();

    /// <summary>Read-only checkpoint: no file capture and no export side effect.</summary>
    [JsonRpcMethod(RecordingControlPlane.CheckpointMethod)]
    public RecordingCheckpoint GetRecordingCheckpoint() => RecordingControl.GetCheckpoint();

    /// <summary>Export only the closed recording tied to the requested epoch.</summary>
    [JsonRpcMethod(RecordingControlPlane.ExportMethod, UseSingleObjectParameterDeserialization = true)]
    public RecordingExportAck ExportRecording(RecordingExportRequest? request)
        => RecordingControl.Export(request, _state.InitializeReceived);

    /// <summary>Queue an explicit P0 incident marker with no private payload.</summary>
    [JsonRpcMethod(RecordingControlPlane.MarkerMethod, UseSingleObjectParameterDeserialization = true)]
    public RecordingMarkerAck AddDiagnosticMarker(RecordingMarkerRequest? request)
        => RecordingControl.AddMarker(request, _state.InitializeReceived);

    /// <summary>Register scoped, time-limited consent intent (no capture enabled).</summary>
    [JsonRpcMethod(RecordingControlPlane.GrantConsentMethod, UseSingleObjectParameterDeserialization = true)]
    public CaptureConsentAck GrantCaptureConsent(CaptureConsentGrant? request)
        => RecordingControl.GrantCaptureConsent(request, _state.InitializeReceived);

    /// <summary>Clear any in-memory consent scope immediately.</summary>
    [JsonRpcMethod(RecordingControlPlane.RevokeConsentMethod, UseSingleObjectParameterDeserialization = true)]
    public CaptureConsentAck RevokeCaptureConsent(CaptureConsentRevoke? request)
        => RecordingControl.RevokeCaptureConsent(request, _state.InitializeReceived);

    [JsonRpcMethod(RecordingControlPlane.ConsentStatusMethod)]
    public CaptureConsentStatus GetCaptureConsentStatus()
        => RecordingControl.GetCaptureConsentStatus();

    /// <summary>Arm the explicitly consented, bounded, RAM-only replay journal.</summary>
    [JsonRpcMethod(RecordingControlPlane.ArmJournalMethod, UseSingleObjectParameterDeserialization = true)]
    public CaptureJournalAck ArmCaptureJournal(CaptureJournalArm? request)
        => RecordingControl.ArmCaptureJournal(request, _state.InitializeReceived);

    [JsonRpcMethod(RecordingControlPlane.VerifyJournalMethod)]
    public CaptureReplayEvidence VerifyCaptureJournal()
        => RecordingControl.VerifyCaptureJournal();

    /// <summary>
    /// Applies an explicit mode request and returns the server-owned epoch.
    /// An RPC timeout must never be interpreted by the client as an ACK.
    /// </summary>
    [JsonRpcMethod(RecordingControlPlane.SetMethod, UseSingleObjectParameterDeserialization = true)]
    public RecordingPolicyAck SetRecordingPolicy(RecordingPolicyRequest? request)
        => RecordingControl.Apply(request, _state.InitializeReceived);

    [JsonRpcMethod("$/setTrace", UseSingleObjectParameterDeserialization = true)]
    public void HandleSetTrace(SetTraceParams? parameters)
    {
        logger.Verbose($"$/setTrace: {parameters?.Value}");
    }

    private DocumentStore CreateStore()
    {
        // Test seam: when supplied, the session uses the provided store instead of
        // building the production Cvolo-backed one.
        if (storeFactory is not null)
        {
            return storeFactory();
        }

        IReadOnlyList<string> folders = _state.WorkspaceFolders;
        var root = _state.WorkspaceRoot;
        IReadOnlyList<string> libraryPaths = _state.LibraryPaths;

        if (folders.Count > 0)
        {
            logger.Info($"Document synchronization active; {folders.Count} workspace folder(s).");
        }
        else if (root is not null)
        {
            logger.Info($"Document synchronization active; workspace root '{root}'.");
        }
        else
        {
            logger.Info("Document synchronization active; no workspace root (project discovery is unbounded).");
        }

        if (libraryPaths.Count > 0)
            logger.Info($"Cvolo explicit library paths: {libraryPaths.Count}.");

        CoreLoggerBridge coreLogger = new(logger);
        return new DocumentStore(
            new CvoloLanguageBackend(folders, root, coreLogger, libraryPaths, _serverMetrics, Observer),
            coreLogger,
            _serverMetrics,
            Observer);
    }

    /// <summary>
    /// A process exit for invalid initialize must be delayed until the
    /// JSON-RPC error frame is flushed by the actual message handler.
    /// ResponseSent alone precedes that transport boundary.
    /// </summary>
    internal bool HasPendingRejectedInitializeResponse =>
        Volatile.Read(ref _exitAfterInitializeErrorResponse) == 1;

    internal void OnRejectedInitializeResponseFlushed()
    {
        if (Interlocked.Exchange(ref _exitAfterInitializeErrorResponse, 0) == 1)
            termination.TryRequestAbnormal();
    }

    private void OnClientTerminated()
    {
        termination.TryRequestAbnormal();
        logger.Error("Client process terminated unexpectedly.");
    }

    private void DisarmClientWatcher()
    {
        try
        {
            _sessionCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Test-only read accessor: whether the 'initialized' notification has been
    /// accepted. Exposed through the test-host path; does not affect production
    /// behaviour.
    /// </summary>
    internal bool InitializedReceived => _state.InitializedReceived;

    /// <summary>Test-and-operations accessor for the S0 pipeline counters.</summary>
    internal ServerMetrics Metrics => _serverMetrics;

    public void Dispose()
    {
        DisarmClientWatcher();
        _refresh?.Stop();
        _editorRefresh?.Stop();
        _sync?.Dispose();
        _codeActionResolveStore?.Clear();
        try
        {
            logger.Info(_serverMetrics.FormatReport());
        }
        catch
        {
        }

        _sessionCancellation.Dispose();
    }

    private static LocalRpcException ServerNotInitialized(string message)
    {
        return new(message) { ErrorCode = ProtocolErrorCodes.ServerNotInitialized };
    }
}