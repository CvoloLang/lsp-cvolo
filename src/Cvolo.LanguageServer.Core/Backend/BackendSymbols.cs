using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>
/// Opaque handle to a semantic symbol resolved by a backend. Never inspected by
/// the core; only passed back to the backend that minted it, together with the
/// snapshot it was resolved from.
/// </summary>
internal abstract class BackendSymbolHandle
{
}

/// <summary>
/// Backend-neutral semantic symbol classification. The adapter maps these to the
/// protocol layer's wire kinds; the core and the protocol layer never see each
/// other's enums.
/// </summary>
internal enum BackendSymbolKind
{
    Namespace,
    Module,
    Struct,
    Union,
    Enum,
    EnumMember,
    Interface,
    Protocol,
    Delegate,
    TypeAlias,
    TypeParameter,
    Function,
    Method,
    ExtensionMethod,
    AssociatedFunction,
    Constructor,
    Destructor,
    Field,
    Property,
    Parameter,
    Local,
    Global,
    Constant,
    Operator,
    OtherType,
    Keyword,
    Unknown,
}

/// <summary>
/// The symbol resolved at a source position: its opaque handle, the occurrence
/// span, its name and classification, and a backend-owned display string.
/// </summary>
internal enum BackendNativeInteropKind
{
    NativeDelegate,
    RawUnion,
    ForeignGlobal,
}

/// <summary>Structured native interop details transported without reparsing Cvolo attributes.</summary>
internal sealed record BackendNativeInteropMetadata(
    BackendNativeInteropKind Kind,
    string? CallingConvention = null,
    string? ImportName = null,
    string? LibraryName = null,
    string? WinPath = null,
    string? LinuxPath = null,
    string? MacPath = null);

internal sealed record BackendSymbolInfo(
    BackendSymbolHandle Symbol,
    TextSpan SubjectSpan,
    string Name,
    BackendSymbolKind Kind,
    string DisplayText,
    string? Documentation = null,
    BackendNativeInteropMetadata? NativeInterop = null);

/// <summary>
/// One source declaration of a symbol, expressed in the backend's own spans.
/// </summary>
internal sealed record BackendDefinitionTarget(
    DocumentUri Document,
    TextSpan Range,
    TextSpan SelectionSpan);

/// <summary>
/// The definition locations of a symbol, together with the exact text of every
/// referenced document from the same captured snapshot. <see cref="DocumentTexts"/>
/// is required because definition targets may be closed editor documents that do
/// not exist in the document store.
/// </summary>
internal sealed record BackendDefinitionResult(
    IReadOnlyDictionary<DocumentUri, string> DocumentTexts,
    IReadOnlyList<BackendDefinitionTarget> Targets);

/// <summary>
/// One node of a document's declaration outline, in deterministic source order.
/// </summary>
internal sealed record BackendDocumentSymbol(
    string Name,
    string? Detail,
    BackendSymbolKind Kind,
    TextSpan Range,
    TextSpan SelectionSpan,
    IReadOnlyList<BackendDocumentSymbol> Children);

/// <summary>
/// Backend-neutral semantic-token modifier flags (LSP-5 §10). The protocol layer maps these to
/// the negotiated LSP modifier bitset.
/// </summary>
[Flags]
internal enum BackendSemanticTokenModifiers
{
    None = 0,
    Declaration = 1 << 0,
    Readonly = 1 << 1,
    Static = 1 << 2,

    /// <summary>Compiler-proven compile-time constant (immutable global); see INC 3.1 CONST.</summary>
    Constant = 1 << 3,

    /// <summary>Imported extern function declaration (no Cvolo body); see INC 3.1 EXT.</summary>
    ExternFunction = 1 << 4,
}

/// <summary>
/// One semantic source occurrence: exact span, classification and modifier flags.
/// </summary>
internal sealed record BackendSemanticToken(
    TextSpan Span,
    BackendSymbolKind Kind,
    BackendSemanticTokenModifiers Modifiers);

/// <summary>
/// The semantic tokens for one document, from a single captured snapshot.
/// </summary>
internal sealed record BackendSemanticTokenResult(
    IReadOnlyList<BackendSemanticToken> Tokens);