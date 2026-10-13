using System.Diagnostics.CodeAnalysis;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Minimal, instance-scoped session state. Each server instance owns exactly
/// one <see cref="SessionState"/>; no process-wide statics.
/// </summary>
internal sealed class SessionState
{
    private volatile bool _initializeReceived;
    private volatile bool _initializedReceived;
    private volatile bool _shutdownReceived;
    private InitializeRequestParams? _initializeParams;

    public bool InitializeReceived => _initializeReceived;

    public bool InitializedReceived => _initializedReceived;

    public bool ShutdownReceived => _shutdownReceived;

    /// <summary>
    /// Workspace folders from initialize (most specific containing folder
    /// becomes the per-document discovery boundary). Empty when the client did
    /// not send them.
    /// </summary>
    public IReadOnlyList<string> WorkspaceFolders
    {
        get
        {
            var folders = _initializeParams?.WorkspaceFolders;
            if (folders is null || folders.Length == 0)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>(folders.Length);
            foreach (var folder in folders)
            {
                if (folder is null || !TryGetLocalPath(folder.Uri, out string? localPath))
                {
                    continue;
                }

                result.Add(localPath);
            }

            return result;
        }
    }

    public IReadOnlyList<string> LibraryPaths
    {
        get
        {
            var paths = _initializeParams?.InitializationOptions?.LibraryPaths;
            if (paths is null || paths.Length == 0)
                return Array.Empty<string>();

            return paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim())
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToArray();
        }
    }

    /// <summary>
    /// Workspace root from initialize: rootUri, falling back to rootPath.
    /// </summary>
    public string? WorkspaceRoot
    {
        get
        {
            if (_initializeParams?.RootUri is { } rootUri && TryGetLocalPath(rootUri, out string? localPath))
            {
                return localPath;
            }

            return _initializeParams?.RootPath;
        }
    }

    public void MarkInitializeReceived(InitializeRequestParams? initializeParams)
    {
        _initializeParams = initializeParams;
        HoverPrefersMarkdown = ComputeHoverMarkdown(initializeParams);
        HierarchicalDocumentSymbols =
            initializeParams?.Capabilities?.TextDocument?.DocumentSymbol?.HierarchicalDocumentSymbolSupport == true;
        ComputeSemanticTokens(initializeParams);
        PrepareRenameSupported = initializeParams?.Capabilities?.TextDocument?.Rename?.PrepareSupport == true;
        DocumentChangesSupported = initializeParams?.Capabilities?.Workspace?.WorkspaceEdit?.DocumentChanges == true;
        ComputeCodeActions(initializeParams);
        CompletionSnippetSupport = initializeParams?.Capabilities?.TextDocument?.Completion?.CompletionItem?.SnippetSupport == true;
        CompletionResolveSupportProperties = initializeParams?.Capabilities?.TextDocument?.Completion?.CompletionItem?.ResolveSupport?.Properties;
        CompletionDocumentationMarkdown = ComputeCompletionDocumentationMarkdown(initializeParams);
        SignatureHelpLabelOffsetSupport =
            initializeParams?.Capabilities?.TextDocument?.SignatureHelp?.SignatureInformation?.ParameterInformation?.LabelOffsetSupport == true;
        SignatureHelpActiveParameterSupport =
            initializeParams?.Capabilities?.TextDocument?.SignatureHelp?.SignatureInformation?.ActiveParameterSupport == true;
        CodeLensRefreshSupported = initializeParams?.Capabilities?.Workspace?.CodeLens?.RefreshSupport == true;
        InlayHintRefreshSupported = initializeParams?.Capabilities?.Workspace?.InlayHint?.RefreshSupport == true;
        AnchoredLensSupported = ComputeAnchoredLensSupport(initializeParams);
        _initializeReceived = true;
    }


    /// <summary>Whether the client supports textDocument/prepareRename.</summary>
    public bool PrepareRenameSupported { get; private set; }

    /// <summary>Whether WorkspaceEdit.documentChanges is supported.</summary>
    public bool DocumentChangesSupported { get; private set; }

    /// <summary>
    /// Whether the client advertises <c>codeAction.codeActionLiteralSupport</c>. Only then does the
    /// server advertise a <c>codeActionProvider</c> (quickfix literals only, no command fallback).
    /// </summary>
    public bool CodeActionLiteralSupported { get; private set; }

    /// <summary>The client-declared code action kind value set (open strings, captured verbatim).</summary>
    public string[] CodeActionLiteralKinds { get; private set; } = [];

    /// <summary>Whether the client accepts opaque <c>CodeAction.data</c>.</summary>
    public bool CodeActionDataSupport { get; private set; }

    /// <summary><c>codeAction.resolveSupport.properties</c> as sent by the client, or null when absent.</summary>
    public string[]? CodeActionResolveSupportProperties { get; private set; }

    /// <summary>
    /// Whether lazy code action edit resolution is safe: literal support, data support, and the client
    /// will resolve the <c>edit</c> property through <c>codeAction/resolve</c>.
    /// </summary>
    public bool LazyCodeActionEditResolveSupported { get; private set; }

    private void ComputeCodeActions(InitializeRequestParams? initializeParams)
    {
        var capabilities = initializeParams?.Capabilities?.TextDocument?.CodeAction;
        CodeActionLiteralSupported = capabilities?.CodeActionLiteralSupport is not null;
        CodeActionLiteralKinds = capabilities?.CodeActionLiteralSupport?.CodeActionKind?.ValueSet ?? [];
        CodeActionDataSupport = capabilities?.DataSupport == true;
        CodeActionResolveSupportProperties = capabilities?.ResolveSupport?.Properties;
        LazyCodeActionEditResolveSupported = CodeActionLiteralSupported
            && CodeActionDataSupport
            && CodeActionResolveSupportProperties?.Contains("edit", StringComparer.Ordinal) == true;
    }

    /// <summary>Whether the client supports <c>workspace/codeLens/refresh</c>.</summary>
    public bool CodeLensRefreshSupported { get; private set; }

    /// <summary>Whether the client supports <c>workspace/inlayHint/refresh</c>.</summary>
    public bool InlayHintRefreshSupported { get; private set; }

    /// <summary>
    /// Whether the client took part in the v3 anchored-lens handshake by advertising
    /// <c>experimental.cvoloAnchoredLens = { version: 3, validateAction: true }</c>. Only such a client
    /// is ever handed a clickable lens: the server presents a lens as inert text to any client that
    /// cannot revalidate the click (§21, §39).
    /// </summary>
    public bool AnchoredLensSupported { get; private set; }

    private static bool ComputeAnchoredLensSupport(InitializeRequestParams? initializeParams)
    {
        var anchored = initializeParams?.Capabilities?.Experimental?["cvoloAnchoredLens"];
        return anchored is not null
            && anchored.Value<int?>("version") == 3
            && anchored.Value<bool?>("validateAction") == true;
    }

    private EditorIntelligenceSettings _editorIntelligence = EditorIntelligenceSettings.Default;

    /// <summary>
    /// The editor intelligence settings currently in force (§64). Read on every semantic request,
    /// so a change takes effect for the next request without a restart (§65).
    /// </summary>
    public EditorIntelligenceSettings EditorIntelligence => Volatile.Read(ref _editorIntelligence);

    /// <summary>
    /// Adopts the settings from a configuration notification. Returns whether anything actually
    /// changed, so a notification that repeats the current values does not trigger a needless
    /// refresh. A notification without a settings payload is ignored entirely.
    /// </summary>
    public bool TryApplyConfiguration(Newtonsoft.Json.Linq.JObject? settings)
    {
        if (settings is null)
        {
            return false;
        }

        EditorIntelligenceSettings current = EditorIntelligence;
        EditorIntelligenceSettings updated = EditorIntelligenceSettings.FromConfiguration(settings, current);
        if (updated == current)
        {
            return false;
        }

        Volatile.Write(ref _editorIntelligence, updated);
        return true;
    }

    private static readonly string[] CanonicalTokenTypes =
    [
        "namespace", "type", "struct", "enum", "interface", "typeParameter",
        "parameter", "variable", "property", "enumMember", "function", "method", "operator",
        "keyword",
    ];

    private static readonly string[] CanonicalTokenModifiers = ["declaration", "readonly", "static"];

    /// <summary>
    /// Whether full semantic tokens are enabled for the session (all compatibility gates passed
    /// and the effective token-type legend is non-empty).
    /// </summary>
    public bool SemanticTokensEnabled { get; private set; }

    /// <summary>Whether the client supports <c>workspace/semanticTokens/refresh</c>.</summary>
    public bool RefreshSupported { get; private set; }


    /// <summary>The negotiated effective token-type legend (server canonical order).</summary>
    public string[] SemanticTokenTypes { get; private set; } = [];

    /// <summary>The negotiated effective token-modifier legend (server canonical order).</summary>
    public string[] SemanticTokenModifiers { get; private set; } = [];

    private void ComputeSemanticTokens(InitializeRequestParams? initializeParams)
    {
        var semanticTokens = initializeParams?.Capabilities?.TextDocument?.SemanticTokens;
        RefreshSupported = initializeParams?.Capabilities?.Workspace?.SemanticTokens?.RefreshSupport == true;

        var clientTypes = semanticTokens?.TokenTypes ?? [];
        SemanticTokenTypes = [.. CanonicalTokenTypes.Where(clientTypes.Contains)];

        var clientModifiers = semanticTokens?.TokenModifiers ?? [];
        SemanticTokenModifiers = [.. CanonicalTokenModifiers.Where(clientModifiers.Contains)];

        var fullRequested = IsFullRequested(semanticTokens?.Requests?.Full);
        var relativeFormat = semanticTokens?.Formats?.Contains("relative", StringComparer.Ordinal) == true;
        // LSP-5 is augmentation-only: absence is not consent to mix semantic and lexical streams.
        var augments = semanticTokens?.AugmentsSyntaxTokens == true;

        SemanticTokensEnabled = fullRequested && relativeFormat && augments && SemanticTokenTypes.Length > 0;
    }

    private static bool IsFullRequested(object? full) => full switch
    {
        null => false,
        bool value => value,
        _ => true,
    };

    /// <summary>
    /// Whether hover content should be returned as markdown. Prefers markdown
    /// when the client lists it before plaintext; otherwise plaintext (§7.2).
    /// </summary>
    public bool HoverPrefersMarkdown { get; private set; }

    /// <summary>
    /// Whether the client supports hierarchical document symbols. When false,
    /// document symbols are flattened to <c>SymbolInformation[]</c> (§7.3).
    /// </summary>
    public bool HierarchicalDocumentSymbols { get; private set; }

    private static bool ComputeHoverMarkdown(InitializeRequestParams? initializeParams)
    {
        string[]? formats = initializeParams?.Capabilities?.TextDocument?.Hover?.ContentFormat;
        if (formats is null || formats.Length == 0)
        {
            return false;
        }

        var markdown = Array.IndexOf(formats, "markdown");
        if (markdown < 0)
        {
            return false;
        }

        var plaintext = Array.IndexOf(formats, "plaintext");
        return plaintext < 0 || markdown < plaintext;
    }

    /// <summary>
    /// Whether the client negotiated snippet-capable completion: <c>completionItem.snippetSupport = true</c>.
    /// Only then may callable insertion templates be snippet-encoded (§23, §37).
    /// </summary>
    public bool CompletionSnippetSupport { get; private set; }

    /// <summary>
    /// <c>completionItem.resolveSupport.properties</c> as sent by the client. Null means the
    /// client sent no resolveSupport (historical default: <c>{ "detail", "documentation" }</c>);
    /// an empty array means nothing is resolvable (§25, §37).
    /// </summary>
    public string[]? CompletionResolveSupportProperties { get; private set; }

    /// <summary>Whether lazy completion documentation should be emitted as markdown (§14).</summary>
    public bool CompletionDocumentationMarkdown { get; private set; }

    /// <summary>
    /// Whether the client accepts <c>[start, end]</c> parameter-label offsets into the signature
    /// label (LSP 3.17 <c>signatureInformation.parameterInformation.labelOffsetSupport</c>). When
    /// false, parameter labels are sent as exact substrings of the signature label (§11, §14).
    /// </summary>
    public bool SignatureHelpLabelOffsetSupport { get; private set; }

    /// <summary>
    /// Whether the client advertises per-signature <c>activeParameter</c>
    /// (<c>signatureInformation.activeParameterSupport</c>). Only then are per-candidate active
    /// parameters emitted; the top-level activeParameter is always derived from the active
    /// signature's compiler value (§11, §11.5).
    /// </summary>
    public bool SignatureHelpActiveParameterSupport { get; private set; }

    private static bool ComputeCompletionDocumentationMarkdown(InitializeRequestParams? initializeParams)
    {
        string[]? formats = initializeParams?.Capabilities?.TextDocument?.Completion?.CompletionItem?.DocumentationFormat;
        if (formats is null || formats.Length == 0)
        {
            return false;
        }

        var markdown = Array.IndexOf(formats, "markdown");
        if (markdown < 0)
        {
            return false;
        }

        var plaintext = Array.IndexOf(formats, "plaintext");
        return plaintext < 0 || markdown < plaintext;
    }

    public void MarkInitialized()
    {
        _initializedReceived = true;
    }

    public void MarkShutdownReceived()
    {
        _shutdownReceived = true;
    }

    private static bool TryGetLocalPath(string? uriText, [NotNullWhen(true)] out string? localPath)
    {
        if (uriText is not null
            && Uri.TryCreate(uriText, UriKind.Absolute, out Uri? parsed)
            && TryGetLocalPath(parsed, out localPath))
        {
            return true;
        }

        localPath = null;
        return false;
    }

    private static bool TryGetLocalPath(Uri uri, [NotNullWhen(true)] out string? localPath)
    {
        if (uri.IsAbsoluteUri && string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            localPath = uri.LocalPath;
            return true;
        }

        if (!uri.IsAbsoluteUri && Path.IsPathRooted(uri.OriginalString))
        {
            localPath = Path.GetFullPath(uri.OriginalString);
            return true;
        }

        localPath = null;
        return false;
    }
}