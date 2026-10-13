using Microsoft.VisualStudio.LanguageServer.Protocol;
using Cvolo.LanguageServer.Diagnostics;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Wire type for the <c>initialize</c> response. The protocol library's
/// <c>InitializeResult</c> has no <c>serverInfo</c> property, so the server
/// defines its own result shape per LSP 3.17.
/// </summary>
internal sealed record InitializeResponse(ServerCapabilities Capabilities, ServerInfo ServerInfo);

/// <summary>
/// LSP 3.17 server capabilities advertised by this server. Defined locally
/// because the protocol library marks <c>CompletionOptions.ResolveProvider</c>
/// with <c>EmitDefaultValue = false</c>, which silently drops an explicit
/// <c>resolveProvider: false</c> from the wire; LSP-3 requires it advertised
/// (§6.1).
/// </summary>
internal sealed record ServerCapabilities
{
    public TextDocumentSyncOptions? TextDocumentSync { get; init; }

    public CompletionOptions? CompletionProvider { get; init; }

    public SignatureHelpOptions? SignatureHelpProvider { get; init; }

    public bool? HoverProvider { get; init; }

    public bool? DefinitionProvider { get; init; }

    public bool? TypeDefinitionProvider { get; init; }

    public bool? ImplementationProvider { get; init; }

    public bool? TypeHierarchyProvider { get; init; }

    public bool? ReferencesProvider { get; init; }

    public object? RenameProvider { get; init; }

    public bool? DocumentSymbolProvider { get; init; }

    public SemanticTokensOptions? SemanticTokensProvider { get; init; }

    public object? CodeActionProvider { get; init; }

    public CodeLensOptions? CodeLensProvider { get; init; }

    public bool? InlayHintProvider { get; init; }

    public bool? DocumentHighlightProvider { get; init; }

    public bool? FoldingRangeProvider { get; init; }

    public bool? SelectionRangeProvider { get; init; }

    /// <summary>LSP <c>executeCommandProvider</c>: the workspace command names the server accepts.</summary>
    public ExecuteCommandOptions? ExecuteCommandProvider { get; init; }

    /// <summary>LSP <c>experimental</c> capability bag advertised during initialization.</summary>
    public object? Experimental { get; init; }
}

/// <summary>
/// LSP 3.17 <c>codeLensProvider</c> options. A local shape so an explicit
/// <c>resolveProvider: false</c> is always serialized: the server answers every
/// lens in one request and never resolves a single declaration lazily (§4).
/// </summary>
internal sealed record CodeLensOptions
{
    public bool ResolveProvider { get; init; }
}

/// <summary>
/// LSP 3.17 <c>codeActionProvider</c> options. A local shape so the negotiated
/// quickfix-only kind list and an explicit <c>resolveProvider</c> are always serialized.
/// </summary>
internal sealed record CodeActionOptions
{
    public string[]? CodeActionKinds { get; init; }

    public bool ResolveProvider { get; init; }
}

/// <summary>
/// LSP 3.17 <c>semanticTokensProvider</c> options. Defined locally so the pinned protocol model's
/// shape cannot drop the negotiated legend or the <c>range: false</c> flag.
/// </summary>
internal sealed record SemanticTokensOptions
{
    public SemanticTokensLegend? Legend { get; init; }

    public bool? Full { get; init; }

    public bool? Range { get; init; }
}

internal sealed record SemanticTokensLegend
{
    public string[]? TokenTypes { get; init; }

    public string[]? TokenModifiers { get; init; }
}

/// <summary>
/// LSP 3.17 <c>completionProvider</c> options. A local shape so
/// <c>resolveProvider</c> is always serialized, including when it is false.
/// </summary>
internal sealed record CompletionOptions
{
    public bool ResolveProvider { get; init; }

    public string[]? TriggerCharacters { get; init; }
}

/// <summary>LSP 3.17 <c>signatureHelpProvider</c> options.</summary>
internal sealed record SignatureHelpOptions
{
    public string[]? TriggerCharacters { get; init; }

    public string[]? RetriggerCharacters { get; init; }
}

/// <summary>Params for <c>textDocument/signatureHelp</c>.</summary>
internal sealed class SignatureHelpParams
{
    public TextDocumentIdentifier? TextDocument { get; init; }

    public Position? Position { get; init; }

    public SignatureHelpContext? Context { get; init; }
}

internal sealed record SignatureHelpResponse(
    SignatureInformationPayload[] Signatures,
    int ActiveSignature,
    int? ActiveParameter);

internal sealed record SignatureInformationPayload(
    string Label,
    ParameterInformationPayload[] Parameters,
    string? Documentation = null,
    int? ActiveParameter = null);

/// <summary>
/// One parameter's label. Either the exact display text (when the client does not negotiate
/// label offset support) or a two-element array <c>[start, end]</c> of UTF-16 offsets into the
/// signature label (LSP 3.17 <c>parameterInformation.label</c>).
/// </summary>
internal sealed record ParameterInformationPayload(
    object Label,
    string? Documentation = null);

/// <summary>
/// LSP 3.17 <c>ServerInfo</c>.
/// </summary>
internal sealed record ServerInfo(string Name, string Version);

/// <summary>
/// Params for <c>textDocument/semanticTokens/full</c>.
/// </summary>
internal sealed class SemanticTokensParams
{
    public TextDocumentIdentifier? TextDocument { get; init; }
}

/// <summary>
/// Result for <c>textDocument/semanticTokens/full</c>: the relative-encoded token stream.
/// </summary>
internal sealed record SemanticTokensResponse(int[] Data);

/// <summary>
/// Params for the <c>$/setTrace</c> notification.
/// </summary>
internal sealed record SetTraceParams(string? Value);

/// <summary>
/// Server-side shape of the <c>initialize</c> request params. The protocol
/// library's <c>InitializeParams</c> predates <c>workspaceFolders</c>, so the
/// server deserializes the payload into its own superset view.
/// </summary>
internal sealed class InitializeRequestParams
{
    public int? ProcessId { get; init; }
    public string? RootPath { get; init; }
    public Uri? RootUri { get; init; }
    public WorkspaceFolderItem[]? WorkspaceFolders { get; init; }
    public ClientCapabilitiesPayload? Capabilities { get; init; }
    public CvoloInitializationOptions? InitializationOptions { get; init; }
}

/// <summary>Cvolo-specific initialize options supplied by editor clients.</summary>
internal sealed class CvoloInitializationOptions
{
    /// <summary>Explicit .cvlib files or directories containing .cvlib files.</summary>
    public string[]? LibraryPaths { get; init; }
}

/// <summary>
/// Server-side shape of the subset of <c>ClientCapabilities</c> the server
/// reads. The protocol library's model does not surface
/// <c>textDocument.publishDiagnostics.relatedInformation</c>, so the server
/// deserializes its own minimal view.
/// </summary>
internal sealed class ClientCapabilitiesPayload
{
    public TextDocumentClientCapabilitiesPayload? TextDocument { get; init; }

    public WorkspaceClientCapabilitiesPayload? Workspace { get; init; }

    /// <summary>
    /// The open <c>experimental</c> capability bag. It is read raw because its shape is negotiated
    /// out of band: the server only looks for the keys it defined (§21).
    /// </summary>
    public Newtonsoft.Json.Linq.JObject? Experimental { get; init; }
}

internal sealed class WorkspaceClientCapabilitiesPayload
{
    public SemanticTokensWorkspaceClientCapabilitiesPayload? SemanticTokens { get; init; }

    public WorkspaceEditClientCapabilitiesPayload? WorkspaceEdit { get; init; }

    public CodeLensWorkspaceClientCapabilitiesPayload? CodeLens { get; init; }

    public InlayHintWorkspaceClientCapabilitiesPayload? InlayHint { get; init; }
}

/// <summary><c>workspace.codeLens.refreshSupport</c>: whether the client accepts a server-requested lens refresh.</summary>
internal sealed class CodeLensWorkspaceClientCapabilitiesPayload
{
    public bool? RefreshSupport { get; init; }
}

/// <summary><c>workspace.inlayHint.refreshSupport</c>: whether the client accepts a server-requested hint refresh.</summary>
internal sealed class InlayHintWorkspaceClientCapabilitiesPayload
{
    public bool? RefreshSupport { get; init; }
}

internal sealed class WorkspaceEditClientCapabilitiesPayload
{
    public bool? DocumentChanges { get; init; }
}

internal sealed class SemanticTokensWorkspaceClientCapabilitiesPayload
{
    public bool? RefreshSupport { get; init; }
}

internal sealed class TextDocumentClientCapabilitiesPayload
{
    public PublishDiagnosticsClientCapabilitiesPayload? PublishDiagnostics { get; init; }

    public HoverClientCapabilitiesPayload? Hover { get; init; }

    public DocumentSymbolClientCapabilitiesPayload? DocumentSymbol { get; init; }

    public SemanticTokensClientCapabilitiesPayload? SemanticTokens { get; init; }

    public RenameClientCapabilitiesPayload? Rename { get; init; }

    public CompletionClientCapabilitiesPayload? Completion { get; init; }

    public SignatureHelpClientCapabilitiesPayload? SignatureHelp { get; init; }

    public CodeActionClientCapabilitiesPayload? CodeAction { get; init; }
}

internal sealed class CodeActionClientCapabilitiesPayload
{
    public CodeActionLiteralSupportPayload? CodeActionLiteralSupport { get; init; }

    public bool? DataSupport { get; init; }

    public CodeActionResolveSupportPayload? ResolveSupport { get; init; }
}

internal sealed class CodeActionLiteralSupportPayload
{
    public CodeActionKindPayload? CodeActionKind { get; init; }
}

internal sealed class CodeActionKindPayload
{
    public string[]? ValueSet { get; init; }
}

internal sealed class CodeActionResolveSupportPayload
{
    public string[]? Properties { get; init; }
}

internal sealed class RenameClientCapabilitiesPayload
{
    public bool? PrepareSupport { get; init; }
}

/// <summary>
/// Server-side shape of <c>textDocument.semanticTokens</c> client capabilities (§7.1, §7.1.1).
/// </summary>
internal sealed class SemanticTokensClientCapabilitiesPayload
{
    public SemanticTokensRequestsPayload? Requests { get; init; }

    public string[]? TokenTypes { get; init; }

    public string[]? TokenModifiers { get; init; }

    public string[]? Formats { get; init; }

    public bool? AugmentsSyntaxTokens { get; init; }
}

/// <summary>
/// <c>requests.full</c> is either a boolean or an object (<c>{ "delta": true }</c>), so it is
/// captured as a raw value and interpreted by the session.
/// </summary>
internal sealed class SemanticTokensRequestsPayload
{
    public object? Full { get; init; }
}

internal sealed class PublishDiagnosticsClientCapabilitiesPayload
{
    public bool? RelatedInformation { get; init; }

    public bool? VersionSupport { get; init; }
}

/// <summary>
/// Server-side shape of <c>textDocument.hover</c> client capabilities. The
/// server reads the ordered content formats to decide between markdown and
/// plaintext hover content (§7.2).
/// </summary>
internal sealed class HoverClientCapabilitiesPayload
{
    public string[]? ContentFormat { get; init; }
}

/// <summary>
/// Server-side shape of <c>textDocument.completion</c> client capabilities. The pinned protocol
/// model surfaces snippet support but not <c>completionItem.resolveSupport.properties</c>, so the
/// server reads its own superset view of the payload (??25, ??37).
/// </summary>
internal sealed class CompletionClientCapabilitiesPayload
{
    public CompletionItemClientCapabilitiesPayload? CompletionItem { get; init; }

    public bool? ContextSupport { get; init; }
}

internal sealed class CompletionItemClientCapabilitiesPayload
{
    public bool? SnippetSupport { get; init; }

    public string[]? DocumentationFormat { get; init; }

    public CompletionResolveSupportClientCapabilitiesPayload? ResolveSupport { get; init; }
}

/// <summary>
/// <c>completionItem.resolveSupport.properties</c>: the client-declared item properties it will
/// resolve through <c>completionItem/resolve</c>. Absence means the historical default
/// <c>{ "detail", "documentation" }</c>; an explicitly empty array means nothing is resolvable.
/// </summary>
internal sealed class CompletionResolveSupportClientCapabilitiesPayload
{
    public string[]? Properties { get; init; }
}

/// <summary>
/// Server-side shape of <c>textDocument.signatureHelp</c> client capabilities. The pinned protocol
/// model lacks <c>signatureInformation.activeParameterSupport</c>, so the server reads its own
/// superset view of the payload (??14, ??11.5).
/// </summary>
internal sealed class SignatureHelpClientCapabilitiesPayload
{
    public SignatureInformationClientCapabilitiesPayload? SignatureInformation { get; init; }

    public bool? ContextSupport { get; init; }
}

internal sealed class SignatureInformationClientCapabilitiesPayload
{
    public bool? ActiveParameterSupport { get; init; }

    public SignatureParameterInformationClientCapabilitiesPayload? ParameterInformation { get; init; }
}

internal sealed class SignatureParameterInformationClientCapabilitiesPayload
{
    public bool? LabelOffsetSupport { get; init; }
}

/// <summary>
/// Server-side shape of <c>textDocument.documentSymbol</c> client capabilities.
/// </summary>
internal sealed class DocumentSymbolClientCapabilitiesPayload
{
    public bool? HierarchicalDocumentSymbolSupport { get; init; }
}

/// <summary>
/// LSP 3.17 <c>WorkspaceFolder</c> wire shape (uri carried as a string).
/// </summary>
internal sealed record WorkspaceFolderItem(string? Uri, string? Name);

/// <summary>LSP rename provider options used when prepare-rename is supported.</summary>
internal sealed record RenameOptionsPayload(bool PrepareProvider);

internal sealed class ReferenceParamsPayload
{
    public TextDocumentIdentifier? TextDocument { get; init; }
    public Position? Position { get; init; }
    public ReferenceContextPayload? Context { get; init; }
}

internal sealed class ReferenceContextPayload
{
    public bool IncludeDeclaration { get; init; }
}

internal sealed class PrepareRenameParamsPayload
{
    public TextDocumentIdentifier? TextDocument { get; init; }
    public Position? Position { get; init; }
}

internal sealed class RenameParamsPayload
{
    public TextDocumentIdentifier? TextDocument { get; init; }
    public Position? Position { get; init; }
    public string? NewName { get; init; }
}

internal sealed record PrepareRenameResultPayload(Microsoft.VisualStudio.LanguageServer.Protocol.Range Range, string Placeholder);

internal sealed record TextEditPayload(Microsoft.VisualStudio.LanguageServer.Protocol.Range Range, string NewText);

internal sealed record OptionalVersionedTextDocumentIdentifierPayload(Uri Uri, int? Version);

internal sealed record TextDocumentEditPayload(OptionalVersionedTextDocumentIdentifierPayload TextDocument, TextEditPayload[] Edits);

internal sealed record WorkspaceEditPayload
{
    public IReadOnlyDictionary<string, TextEditPayload[]>? Changes { get; init; }
    public TextDocumentEditPayload[]? DocumentChanges { get; init; }
}

/// <summary>
/// Server-side shape of <c>textDocument/codeAction</c> params. <c>context.only</c> is carried as an
/// open string set and <c>context.diagnostics</c> is accepted raw; neither is semantic authority.
/// </summary>
internal sealed class CodeActionParamsPayload
{
    public TextDocumentIdentifier? TextDocument { get; init; }
    public Microsoft.VisualStudio.LanguageServer.Protocol.Range? Range { get; init; }
    public CodeActionContextPayload? Context { get; init; }
    public object? WorkDoneToken { get; init; }
    public object? PartialResultToken { get; init; }
}

internal sealed class CodeActionContextPayload
{
    public object[]? Diagnostics { get; init; }
    public string[]? Only { get; init; }
    public int? TriggerKind { get; init; }
}

internal sealed record CodeActionPayload
{
    public string? Title { get; init; }
    public string? Kind { get; init; }
    public DiagnosticPayload[]? Diagnostics { get; init; }
    public WorkspaceEditPayload? Edit { get; init; }
    public object? Data { get; init; }
}

/// <summary>Server-side shape of <c>textDocument/codeLens</c> params.</summary>
internal sealed class CodeLensParamsPayload
{
    public TextDocumentIdentifier? TextDocument { get; init; }
}

/// <summary>
/// Server-side shape of the <c>workspace/executeCommand</c> params. The command is an open string and
/// the arguments are carried raw: the server decides what a named command means and never infers a
/// command from an argument shape (§4, §39).
/// </summary>
internal sealed class ExecuteCommandParamsPayload
{
    public string? Command { get; init; }

    public Newtonsoft.Json.Linq.JArray? Arguments { get; init; }
}

/// <summary>LSP <c>executeCommandProvider</c> options: the command names the server advertises.</summary>
internal sealed record ExecuteCommandOptions(string[] Commands);

/// <summary>
/// The v3 anchored CodeLens handshake the server advertises under
/// <c>experimental.cvoloAnchoredLens</c>. The version pins the payload shape and <c>validateAction</c>
/// states that the client performs the two-step resolve/validate exchange before it acts (§21, §39).
/// </summary>
internal sealed record AnchoredLensCapability(int Version, bool ValidateAction);

/// <summary>
/// A clickable anchored lens. It names the opaque, session-bound lens token the server minted and the
/// document URI the client understands; it carries no position, because a position is a claim the
/// client could not verify (§21, §39.1).
/// </summary>
internal sealed record AnchoredLensRequestV3(int SchemaVersion, string LensToken, string WireUri);

/// <summary>The client's request to validate an anchored action before it performs it.</summary>
internal sealed record AnchoredActionValidationV3(string ActionToken);

/// <summary>
/// The discriminated result of resolving an anchored lens. <c>kind</c> is one of
/// <c>ShowReferences</c>, <c>ShowLayout</c> or <c>Unavailable</c>. The fields a kind needs are present
/// only for that kind, and <c>reason</c> explains an <c>Unavailable</c> without leaking source or
/// exception text (§21, §39).
/// </summary>
internal sealed record AnchoredActionResultV3(
    int SchemaVersion,
    string Kind,
    string? ActionToken = null,
    string? WireUri = null,
    Position? Anchor = null,
    Location[]? Locations = null,
    TypeLayoutResponse? Layout = null,
    string? Reason = null);

/// <summary>The answer to a validate request: whether the action is still valid, with a reason if not.</summary>
internal sealed record AnchoredValidationResultV3(bool Valid, string? Reason = null);

/// <summary>
/// Server-side shape of <c>textDocument/documentHighlight</c> params. A work-done or partial-result
/// token may accompany the request; neither is semantic authority, so both are ignored.
/// </summary>
internal sealed class DocumentHighlightParamsPayload
{
    public TextDocumentIdentifier? TextDocument { get; init; }

    public Position? Position { get; init; }
}

/// <summary>
/// Server-side shape of <c>textDocument/inlayHint</c> params. The requested range is carried
/// verbatim: the server maps it onto the captured text and never widens it to the document.
/// </summary>
internal sealed class InlayHintParamsPayload
{
    public TextDocumentIdentifier? TextDocument { get; init; }

    public Microsoft.VisualStudio.LanguageServer.Protocol.Range? Range { get; init; }
}

/// <summary>
/// Server-side shape of the <c>InlayHint</c> result. The pinned protocol model predates inlay
/// hints, so the server defines the shape it needs: the label is presented verbatim and the
/// padding flags plus the compiler-derived category are the only decoration a client requires.
/// </summary>
internal sealed record InlayHintPayload
{
    public Position Position { get; init; } = null!;

    public string Label { get; init; } = string.Empty;

    public bool? PaddingLeft { get; init; }

    public bool? PaddingRight { get; init; }

    public string? Kind { get; init; }
}

/// <summary>Server-side shape of <c>textDocument/foldingRange</c> params.</summary>
internal sealed class FoldingRangeParamsPayload
{
    public TextDocumentIdentifier? TextDocument { get; init; }
}

/// <summary>Server-side shape of <c>textDocument/selectionRange</c> params.</summary>
internal sealed class SelectionRangeParamsPayload
{
    public TextDocumentIdentifier? TextDocument { get; init; }

    public Position[]? Positions { get; init; }
}

/// <summary>
/// One level of a smart-selection chain. <see cref="Parent"/> is the next larger region, so a
/// reader starts at the innermost level and walks outwards, exactly as the backend described it.
/// </summary>
internal sealed record SelectionRangePayload
{
    public Microsoft.VisualStudio.LanguageServer.Protocol.Range Range { get; init; } = null!;

    public SelectionRangePayload? Parent { get; init; }
}

/// <summary>
/// Server-side shape of the <c>cvolo/typeLayout</c> request params. The client asks for the
/// detailed layout of the type at one position, or asks again for a subject it stored earlier when
/// refreshing an open view; the extension renders the response and never derives an offset, a size,
/// an alignment or a padding run itself (§22, §42, §57).
/// </summary>
internal sealed class TypeLayoutParamsPayload
{
    public TextDocumentIdentifier? TextDocument { get; init; }

    public Position? Position { get; init; }

    public string? Subject { get; init; }
}

/// <summary>
/// The complete object layout of one type for one target, exactly as the compiler computed it.
/// Every number is a compiler fact; the client may only format them (§19, §22).
/// </summary>
internal sealed record TypeLayoutResponse(
    string TypeDisplay,
    string TargetDisplay,
    long Size,
    long Alignment,
    long PayloadSize,
    long PaddingSize,
    long? Stride,
    long? ElementCount,
    long? ElementSize,
    long? ElementAlignment,
    TypeLayoutMemberResponse[] Members,
    TypeLayoutPaddingResponse[] Padding,
    Location? Definition = null,
    string? Subject = null);

/// <summary>One member's placement inside its type. A union's members share one offset.</summary>
internal sealed record TypeLayoutMemberResponse(
    string Name,
    string TypeDisplay,
    long Offset,
    long Size,
    long Alignment,
    TypeLayoutMemberNavigationResponse? Navigation = null);

/// <summary>
/// What one member row may navigate to, resolved by the compiler. Every location is a source
/// declaration; a client follows the resolved target and never resolves a name itself (§19).
/// </summary>
internal sealed record TypeLayoutMemberNavigationResponse(
    string Signature,
    string? Documentation,
    Location? Definition,
    Location? TypeDefinition,
    Location? NestedLayout);

/// <summary>One run of bytes that carry no member, reported with the compiler's own classification.</summary>
internal sealed record TypeLayoutPaddingResponse(long Offset, long Size, string Kind);

/// <summary>Server-side shape of the <c>workspace/didChangeConfiguration</c> notification.</summary>
internal sealed class DidChangeConfigurationParams
{
    public Newtonsoft.Json.Linq.JObject? Settings { get; init; }
}

/// <summary>
/// One contract in the declared type hierarchy sent to the client. The pinned protocol model has no
/// <c>TypeHierarchyItem</c>, so the server defines the LSP 3.17 shape locally. <see cref="Data"/> is
/// the server-owned re-resolution key (the contract name): follow-up requests echo it back so the
/// hierarchy re-resolves against whatever snapshot is current rather than a stale symbol handle (§14,
/// §15, §42).
/// </summary>
internal sealed class TypeHierarchyItemPayload
{
    public string? Name { get; init; }

    public SymbolKind Kind { get; init; }

    public string? Detail { get; init; }

    public string? Uri { get; init; }

    public Microsoft.VisualStudio.LanguageServer.Protocol.Range? Range { get; init; }

    public Microsoft.VisualStudio.LanguageServer.Protocol.Range? SelectionRange { get; init; }

    public string? Data { get; init; }
}

/// <summary>Server-side shape of the <c>textDocument/prepareTypeHierarchy</c> request params.</summary>
internal sealed class TypeHierarchyPrepareParams
{
    public TextDocumentIdentifier? TextDocument { get; init; }

    public Position? Position { get; init; }
}

/// <summary>Server-side shape of the <c>typeHierarchy/supertypes</c> request params.</summary>
internal sealed class TypeHierarchySupertypesParams
{
    public TypeHierarchyItemPayload? Item { get; init; }
}

/// <summary>Server-side shape of the <c>typeHierarchy/subtypes</c> request params.</summary>
internal sealed class TypeHierarchySubtypesParams
{
    public TypeHierarchyItemPayload? Item { get; init; }
}
