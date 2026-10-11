using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

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
    Func<DocumentStore>? storeFactory = null) : IDisposable
{
    private const string QuickFixKind = "quickfix";

    private readonly SessionState _state = new();
    private readonly DiagnosticSink _diagnostics = new(logger);
    private readonly CancellationTokenSource _sessionCancellation = new();

    /// <summary>
    /// Guards publication of every session-scoped member below. Notifications and requests are
    /// dispatched on their own threads, so two callers can reach a member for the first time at the
    /// same moment; without this gate each would build and keep its own instance.
    /// </summary>
    private readonly object _sessionGate = new();

    private DocumentStore? _store;
    private CompletionResolveStore? _resolveStore;
    private TextDocumentSyncHandler? _sync;
    private CompletionHandler? _completion;
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
    private Timer? _deadClientExitTimer;

    /// <summary>
    /// Server-to-client diagnostic notification channel. The transport attaches
    /// the JSON-RPC endpoint once it exists.
    /// </summary>
    internal DiagnosticSink Diagnostics => _diagnostics;

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
    internal DocumentStore Store => GetOrCreate(ref _store, CreateStore);

    /// <summary>
    /// Text document synchronization (didOpen/didChange/didClose) handler. A project change is
    /// broadcast to every client-refreshable surface so editor state stays live while typing:
    /// semantic tokens and (when the client can receive them) code lenses and inlay hints.
    /// </summary>
    internal TextDocumentSyncHandler Sync => GetOrCreate(
        ref _sync,
        () => new TextDocumentSyncHandler(
            logger,
            _diagnostics,
            () => Store,
            () =>
            {
                Refresh.RequestRefresh();
                EditorIntelligenceRefresh.RequestRefresh();
            }));

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
            () => ResolveStore));

    /// <summary>textDocument/signatureHelp handler.</summary>
    internal SignatureHelpHandler SignatureHelp => GetOrCreate(
        ref _signatureHelp,
        () => new SignatureHelpHandler(
            logger,
            () => Store,
            () => _state.SignatureHelpLabelOffsetSupport,
            () => _state.SignatureHelpActiveParameterSupport));

    /// <summary>
    /// textDocument/hover handler.
    /// </summary>
    internal HoverHandler Hover => GetOrCreate(ref _hover, () => new HoverHandler(logger, () => Store, () => _state.HoverPrefersMarkdown));

    /// <summary>
    /// textDocument/definition handler.
    /// </summary>
    internal DefinitionHandler Definition => GetOrCreate(ref _definition, () => new DefinitionHandler(logger, () => Store));

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
    internal SemanticTokensHandler SemanticTokens => GetOrCreate(ref _semanticTokens, () => new SemanticTokensHandler(logger, () => Store, () => _state.SemanticTokenTypes, () => _state.SemanticTokenModifiers));

    /// <summary>textDocument/references handler.</summary>
    internal ReferencesHandler References => GetOrCreate(ref _references, () => new ReferencesHandler(logger, () => Store));

    /// <summary>textDocument/prepareRename and textDocument/rename handler.</summary>
    internal RenameHandler Rename => GetOrCreate(ref _rename, () => new RenameHandler(logger, () => Store, () => _state.DocumentChangesSupported));

    /// <summary>Session-scoped, bounded store of lazy code-action resolve entries.</summary>
    internal CodeActionResolveStore CodeActionResolveStore => GetOrCreate(ref _codeActionResolveStore, () => new CodeActionResolveStore());

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
    internal CodeLensHandler CodeLenses => GetOrCreate(ref _codeLenses, () => new CodeLensHandler(logger, () => Store, () => _state.EditorIntelligence));

    /// <summary>textDocument/inlayHint handler.</summary>
    internal InlayHintHandler InlayHints => GetOrCreate(ref _inlayHints, () => new InlayHintHandler(logger, () => Store, () => _state.EditorIntelligence));

    /// <summary>textDocument/documentHighlight handler.</summary>
    internal DocumentHighlightHandler DocumentHighlights => GetOrCreate(ref _documentHighlights, () => new DocumentHighlightHandler(logger, () => Store));

    /// <summary>textDocument/foldingRange handler.</summary>
    internal FoldingRangeHandler FoldingRanges => GetOrCreate(ref _foldingRanges, () => new FoldingRangeHandler(logger, () => Store));

    /// <summary>textDocument/selectionRange handler.</summary>
    internal SelectionRangeHandler SelectionRanges => GetOrCreate(ref _selectionRanges, () => new SelectionRangeHandler(logger, () => Store));

    /// <summary>The <c>cvolo/typeLayout</c> request behind the editor's read-only layout view.</summary>
    internal TypeLayoutHandler TypeLayout => GetOrCreate(ref _typeLayout, () => new TypeLayoutHandler(logger, () => Store));

    /// <summary>workspace/didChangeConfiguration handler for the editor intelligence settings.</summary>
    internal ConfigurationHandler Configuration => GetOrCreate(
        ref _configuration,
        () => new ConfigurationHandler(
            logger,
            settings => _state.TryApplyConfiguration(settings),
            () => EditorIntelligenceRefresh.RequestRefresh()));

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
                    _deadClientExitTimer = new Timer(
                        _ => termination.TryRequestAbnormal(),
                        null,
                        TimeSpan.FromMilliseconds(150),
                        Timeout.InfiniteTimeSpan);
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
        logger.Warning($"[perf] semantic tokens legend: types=[{string.Join(",", _state.SemanticTokenTypes)}], modifiers=[{string.Join(",", _state.SemanticTokenModifiers)}].");
        logger.Warning($"[perf] editor intelligence: codeLensRefresh={_state.CodeLensRefreshSupported}, inlayHintRefresh={_state.InlayHintRefreshSupported}.");
        _diagnostics.SetRelatedInformationSupported(
            initializeParams.Capabilities?.TextDocument?.PublishDiagnostics?.RelatedInformation == true);
        logger.Info("Client initialized.");

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
                    TriggerCharacters = [".", "~"],
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
    }

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
            new CvoloLanguageBackend(folders, root, coreLogger, libraryPaths),
            coreLogger);
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

    public void Dispose()
    {
        DisarmClientWatcher();
        _refresh?.Stop();
        _editorRefresh?.Stop();
        _sync?.Dispose();
        _codeActionResolveStore?.Clear();
        _deadClientExitTimer?.Dispose();
        _deadClientExitTimer = null;
        _sessionCancellation.Dispose();
    }

    private static LocalRpcException ServerNotInitialized(string message)
    {
        return new(message) { ErrorCode = ProtocolErrorCodes.ServerNotInitialized };
    }
}