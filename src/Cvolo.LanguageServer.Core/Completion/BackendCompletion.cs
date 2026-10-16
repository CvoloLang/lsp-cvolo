using Cvolo.LanguageServer.Core.Diagnostics;

namespace Cvolo.LanguageServer.Core.Completion;

/// <summary>
/// Backend-neutral completion classification. The adapter maps these to the
/// protocol layer's wire kinds (§12.1); the core and the protocol layer never
/// see each other's enums.
/// </summary>
internal enum BackendCompletionKind
{
    Keyword,
    Local,
    Parameter,
    Global,
    Constant,
    Function,
    Method,
    ExtensionMethod,
    Constructor,
    Field,
    Property,
    Struct,
    Union,
    Enum,
    EnumMember,
    Interface,
    Protocol,
    Module,
    TypeAlias,
    OtherType,
    UnionVariant,
    Type,
    Namespace,
    StructField,
    EnumMetadata,
    ArrayLength,
}

/// <summary>
/// Fields a completion candidate can lazily resolve (§29, §31). The intersection of the
/// backend mask and the client's resolved-able properties decides whether an item carries
/// resolve data; a bit here only means "this may be asked for", never "this was filled".
/// </summary>
[Flags]
internal enum BackendCompletionResolvableFields
{
    None = 0,
    Detail = 1 << 0,
    Documentation = 1 << 1,
}

/// <summary>
/// Opaque handle to one completion candidate's resolvable identity inside one immutable
/// backend snapshot. Never inspected here; only passed back to the backend that minted it.
/// A handle minted against another snapshot resolves to no result.
/// </summary>
internal abstract class BackendCompletionResolveHandle
{
}

/// <summary>One compiler-owned piece of a callable insertion template (no snippet syntax).</summary>
internal abstract record BackendCompletionInsertSegment;

/// <summary>Literal source text in the insertion template.</summary>
internal sealed record BackendCompletionLiteral(string Text) : BackendCompletionInsertSegment;

/// <summary>A spot the editor should offer for user input, with an optional default value.</summary>
internal sealed record BackendCompletionPlaceholder(string DefaultText) : BackendCompletionInsertSegment;

/// <summary>Where the editor cursor should end up after insertion.</summary>
internal sealed record BackendCompletionFinalCursor() : BackendCompletionInsertSegment;

/// <summary>
/// Structured callable insertion template: the exact source to produce, in compiler-owned
/// semantic pieces with no snippet syntax. The protocol layer snippet-encodes it only when
/// the client negotiated snippet support (§23, §24). Segments compare by value in order.
/// </summary>
internal sealed record BackendCompletionInsertionPlan(IReadOnlyList<BackendCompletionInsertSegment> SnippetSegments)
{
    public bool Equals(BackendCompletionInsertionPlan? other)
        => other is not null && SnippetSegments.SequenceEqual(other.SnippetSegments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in SnippetSegments)
            hash.Add(segment);
        return hash.ToHashCode();
    }
}

/// <summary>
/// One completion candidate: the canonical plain identifier to insert when the client does
/// not support snippets, the label shown, its classification, optional initial detail, an
/// optional structured insertion template, and an optional resolvable identity.
/// </summary>
internal sealed record BackendCompletionItem(
    string Label,
    string PlainInsertText,
    BackendCompletionKind Kind,
    string? Detail,
    BackendCompletionInsertionPlan? InsertionPlan,
    BackendCompletionResolveHandle? ResolveHandle,
    BackendCompletionResolvableFields ResolvableFields,
    string? FilterText = null);

/// <summary>The lazily-resolved fields of one completion item.</summary>
internal sealed record BackendCompletionResolvedInfo(string? Detail, string? Documentation);

/// <summary>
/// The complete result of one completion query. All items share one
/// <see cref="ReplacementSpan"/> (absolute UTF-16 span over the captured
/// snapshot text); the span is never fabricated and always contains the request
/// offset (§4.5).
/// </summary>
internal sealed record BackendCompletionResult(TextSpan ReplacementSpan, IReadOnlyList<BackendCompletionItem> Items);