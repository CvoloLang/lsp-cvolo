using Cvolo.Compiler.Tooling;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Logging;
using CoreTextSpan = Cvolo.LanguageServer.Core.Diagnostics.TextSpan;
using ToolingTextSpan = Cvolo.Compiler.Tooling.TextSpan;

namespace Cvolo.LanguageServer.Cvolo;

/// <summary>
/// Maps the compiler's editor-intelligence queries (CodeLens, inlay hints, document highlights,
/// folding, smart selection and type layout) onto the backend-neutral core models. Every fact is
/// taken verbatim from the captured snapshot: the adapter never re-counts references, re-derives a
/// layout, re-scans braces or re-resolves a symbol.
/// </summary>
internal sealed partial class CvoloLanguageBackend
{
    /// <summary>Client command that opens the editor's references UI for the lens' declaration.</summary>
    private const string ShowReferencesCommand = "cvolo.showReferences";

    /// <summary>Client command that opens the read-only type layout view for the lens' type.</summary>
    private const string ShowTypeLayoutCommand = "cvolo.showTypeLayout";

    /// <summary>
    /// Resolves the document a request targets against the captured snapshot. A document that is not
    /// part of the snapshot is not an error: the request is simply stale relative to the snapshot the
    /// caller captured (a closed file, or a session that was rebuilt), so the caller answers empty
    /// rather than failing.
    /// </summary>
    private static DocumentSnapshot? FindEditorDocument(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        return toolingSnapshot.TryGetDocument(((CvoloDocumentHandle)document).DocumentId, out DocumentSnapshot? toolingDocument)
            ? toolingDocument
            : null;
    }

    private static int RequirePosition(DocumentSnapshot document, int position, string feature)
    {
        if (position < 0 || position > document.Text.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                $"The {feature} position is outside the captured document text.");
        }

        return position;
    }

    /// <summary>
    /// Rejects a Tooling span that cannot exist in the captured text. Malformed backend output is
    /// dropped rather than repaired, so an impossible range never becomes an unsafe editor range.
    /// </summary>
    private bool TryAcceptSpan(ToolingTextSpan span, int textLength, string feature, out CoreTextSpan mapped)
    {
        if (span.Start >= 0 && span.Length >= 0 && span.Start + span.Length <= textLength)
        {
            mapped = new CoreTextSpan(span.Start, span.Length);
            return true;
        }

        _logger.Write(
            CoreLogLevel.Debug,
            $"Dropped a {feature} result with an out-of-text span (start={span.Start}, length={span.Length}) over {textLength} code unit(s).");
        mapped = default;
        return false;
    }

    public IReadOnlyList<BackendCodeLensInfo> GetCodeLenses(BackendSnapshot snapshot, BackendDocumentHandle document, BackendCodeLensOptions? options = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        if (FindEditorDocument(snapshot, document) is not { } toolingDocument)
        {
            LogTiming("GetCodeLenses", stopwatch, "document-not-in-snapshot, lenses=0", 0);
            return [];
        }

        int textLength = toolingDocument.Text.Length;

        IReadOnlyList<ToolingCodeLensInfo> lenses = toolingDocument.GetCodeLenses(MapCodeLensOptions(options));
        var mapped = new List<BackendCodeLensInfo>(lenses.Count);
        foreach (ToolingCodeLensInfo lens in lenses)
        {
            if (!TryAcceptSpan(lens.Range, textLength, "code-lens", out CoreTextSpan range))
            {
                continue;
            }

            BackendCodeLensKind? kind = lens.Kind switch
            {
                ToolingCodeLensKind.References => BackendCodeLensKind.References,
                ToolingCodeLensKind.Layout => BackendCodeLensKind.Layout,
                ToolingCodeLensKind.NativeInterop => BackendCodeLensKind.NativeInterop,
                _ => null,
            };

            if (kind is null)
            {
                _logger.Write(CoreLogLevel.Debug, $"Dropped a code lens with unknown kind '{lens.Kind}'.");
                continue;
            }

            // The lens range is the declared name itself, so its start is a position the client's own
            // references/layout query can resolve without the adapter resolving anything (§11, §21).
            // A field layout lens asks for the layout of the type that stores the field, which the
            // server resolves from that same position, so no field identity is put in the arguments.
            var command = kind switch
            {
                BackendCodeLensKind.References => new BackendCodeLensCommand(
                    ShowReferencesCommand,
                    [new BackendCodeLensPositionArgument(lens.Range.Start)]),
                BackendCodeLensKind.Layout => new BackendCodeLensCommand(
                    ShowTypeLayoutCommand,
                    [new BackendCodeLensPositionArgument(lens.Range.Start)]),
                _ => null,
            };

            BackendFieldLayoutInfo? fieldLayout = null;
            if (lens.FieldLayout is { } field)
            {
                // A lens is an invitation to click, so one that cannot present its own numbers is
                // dropped rather than shown in a notation the reader did not ask for.
                fieldLayout = MapFieldLayout(field);
                if (fieldLayout is null)
                {
                    continue;
                }
            }

            mapped.Add(new BackendCodeLensInfo(range, kind.Value, lens.Title, command, fieldLayout));
        }

        LogTiming("GetCodeLenses", stopwatch, $"file='{toolingDocument.FilePath}', rawLenses={lenses.Count}, lenses={mapped.Count}, textLength={textLength}", 0);
        return mapped;
    }

    /// <summary>
    /// Copies the compiler's field storage facts, or returns nothing when the compiler reported a
    /// value no table could show. The two callers differ on purpose: a lens disappears, while a hint
    /// keeps the compiler's own text and only loses the payload.
    /// </summary>
    private BackendFieldLayoutInfo? MapFieldLayout(ToolingFieldLayoutInfo field)
    {
        if (field.Offset < 0 || field.Size < 0 || field.Alignment <= 0 || field.PaddingBefore < 0)
        {
            _logger.Write(
                CoreLogLevel.Debug,
                $"Dropped the field layout payload for '{field.FieldName}': the compiler reported an impossible offset, size, alignment or padding.");
            return null;
        }

        return new BackendFieldLayoutInfo(
            field.ContainingTypeDisplay,
            field.FieldName,
            field.Offset,
            field.Size,
            field.Alignment,
            field.PaddingBefore);
    }

    /// <summary>
    /// Projects the core's category selection onto the tooling's own query options, so a disabled
    /// category is never computed instead of being filtered out afterwards.
    /// </summary>
    private static ToolingCodeLensOptions MapCodeLensOptions(BackendCodeLensOptions? options)
    {
        BackendCodeLensOptions effective = options ?? BackendCodeLensOptions.Default;
        return new ToolingCodeLensOptions
        {
            References = effective.References,
            Layout = effective.Layout,
            Members = effective.Members,
            FieldLayout = effective.FieldLayout,
            NativeInterop = effective.NativeInterop,
        };
    }

    private static ToolingInlayHintOptions MapInlayHintOptions(BackendInlayHintOptions? options)
    {
        BackendInlayHintOptions effective = options ?? BackendInlayHintOptions.Default;
        return new ToolingInlayHintOptions
        {
            Types = effective.Types,
            Parameters = effective.Parameters,
            ReceiverMutability = effective.ReceiverMutability,
            Layout = effective.Layout,
            EnumValues = effective.EnumValues,
            GenericArguments = effective.GenericArguments,
        };
    }

    public IReadOnlyList<BackendInlayHint> GetInlayHints(BackendSnapshot snapshot, BackendDocumentHandle document, CoreTextSpan requestedRange, BackendInlayHintOptions? options = null)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (FindEditorDocument(snapshot, document) is not { } toolingDocument)
            return [];

        int textLength = toolingDocument.Text.Length;

        if (requestedRange.Start < 0
            || requestedRange.Length < 0
            || requestedRange.Start + requestedRange.Length > textLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedRange),
                requestedRange,
                $"The inlay-hint range is outside the captured document text of {textLength} code unit(s).");
        }

        IReadOnlyList<ToolingInlayHint> hints = toolingDocument.GetInlayHints(
            requestedRange.Start,
            requestedRange.Length,
            MapInlayHintOptions(options));
        var mapped = new List<BackendInlayHint>(hints.Count);
        foreach (ToolingInlayHint hint in hints)
        {
            if (hint.Position < 0 || hint.Position > textLength)
            {
                _logger.Write(CoreLogLevel.Debug, $"Dropped an inlay hint at out-of-text position {hint.Position}.");
                continue;
            }

            BackendInlayHintKind? kind = hint.Kind switch
            {
                ToolingInlayHintKind.Type => BackendInlayHintKind.Type,
                ToolingInlayHintKind.Parameter => BackendInlayHintKind.Parameter,
                ToolingInlayHintKind.ReceiverMutability => BackendInlayHintKind.ReceiverMutability,
                ToolingInlayHintKind.Layout => BackendInlayHintKind.Layout,
                ToolingInlayHintKind.EnumValue => BackendInlayHintKind.EnumValue,
                ToolingInlayHintKind.GenericArgument => BackendInlayHintKind.GenericArgument,
                _ => null,
            };

            if (kind is null)
            {
                _logger.Write(CoreLogLevel.Debug, $"Dropped an inlay hint with unknown kind '{hint.Kind}'.");
                continue;
            }

            mapped.Add(new BackendInlayHint(
                hint.Position,
                kind.Value,
                hint.Label,
                hint.PaddingLeft,
                hint.PaddingRight,
                hint.RelatedSymbol is { } related ? new CvoloSymbolHandle(toolingSnapshot, related) : null,
                // A hint is text, not an invitation, so a payload that cannot be presented is simply
                // left out and the compiler's own label still shows.
                hint.FieldLayout is { } field ? MapFieldLayout(field) : null));
        }

        return mapped;
    }

    public IReadOnlyList<BackendDocumentHighlight> GetDocumentHighlights(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        if (FindEditorDocument(snapshot, document) is not { } toolingDocument)
            return [];

        RequirePosition(toolingDocument, position, "document-highlight");
        int textLength = toolingDocument.Text.Length;

        IReadOnlyList<ToolingDocumentHighlight> highlights = toolingDocument.GetDocumentHighlights(position);
        var mapped = new List<BackendDocumentHighlight>(highlights.Count);
        foreach (ToolingDocumentHighlight highlight in highlights)
        {
            if (!TryAcceptSpan(highlight.Range, textLength, "document-highlight", out CoreTextSpan range))
            {
                continue;
            }

            BackendReferenceAccessKind kind = highlight.AccessKind switch
            {
                ReferenceAccessKind.Read => BackendReferenceAccessKind.Read,
                ReferenceAccessKind.Write => BackendReferenceAccessKind.Write,
                ReferenceAccessKind.ReadWrite => BackendReferenceAccessKind.ReadWrite,
                ReferenceAccessKind.Declaration => BackendReferenceAccessKind.Declaration,
                _ => BackendReferenceAccessKind.Read,
            };

            mapped.Add(new BackendDocumentHighlight(range, kind));
        }

        return mapped;
    }

    public IReadOnlyList<BackendFoldingRange> GetFoldingRanges(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        if (FindEditorDocument(snapshot, document) is not { } toolingDocument)
            return [];

        int textLength = toolingDocument.Text.Length;

        IReadOnlyList<ToolingFoldingRange> ranges = toolingDocument.GetFoldingRanges();
        var mapped = new List<BackendFoldingRange>(ranges.Count);
        foreach (ToolingFoldingRange range in ranges)
        {
            if (!TryAcceptSpan(range.Range, textLength, "folding", out CoreTextSpan span))
            {
                continue;
            }

            BackendFoldingRangeKind kind = range.Kind switch
            {
                ToolingFoldingKind.None => BackendFoldingRangeKind.None,
                ToolingFoldingKind.Comment => BackendFoldingRangeKind.Comment,
                ToolingFoldingKind.Documentation => BackendFoldingRangeKind.Documentation,
                _ => BackendFoldingRangeKind.None,
            };

            mapped.Add(new BackendFoldingRange(span, kind));
        }

        return mapped;
    }

    public IReadOnlyList<BackendSelectionRange?> GetSelectionRanges(BackendSnapshot snapshot, BackendDocumentHandle document, IReadOnlyList<int> positions)
    {
        if (FindEditorDocument(snapshot, document) is not { } toolingDocument)
            return positions.Select(static _ => (BackendSelectionRange?)null).ToArray();

        int textLength = toolingDocument.Text.Length;
        foreach (int position in positions)
        {
            RequirePosition(toolingDocument, position, "selection-range");
        }

        IReadOnlyList<ToolingSelectionRange?> chains = toolingDocument.GetSelectionRanges(positions);
        var mapped = new BackendSelectionRange?[positions.Count];
        for (int i = 0; i < chains.Count && i < mapped.Length; i++)
        {
            mapped[i] = MapSelectionChain(chains[i], textLength);
        }

        return mapped;
    }

    /// <summary>
    /// Rebuilds one chain level by level. A level whose span cannot exist in the captured text drops
    /// the whole chain rather than emitting a partially valid one, because a reader walks the chain
    /// outwards and must never meet a level that is not real.
    /// </summary>
    private BackendSelectionRange? MapSelectionChain(ToolingSelectionRange? chain, int textLength)
    {
        if (chain is null)
        {
            return null;
        }

        var levels = new List<CoreTextSpan>();
        for (ToolingSelectionRange? level = chain; level is not null; level = level.Parent)
        {
            if (!TryAcceptSpan(level.Range, textLength, "selection-range", out CoreTextSpan span))
            {
                return null;
            }

            levels.Add(span);
        }

        BackendSelectionRange? parent = null;
        for (int i = levels.Count - 1; i >= 0; i--)
        {
            parent = new BackendSelectionRange(levels[i], parent);
        }

        return parent;
    }

    public BackendTypeLayoutInspection? GetTypeLayoutAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        if (FindEditorDocument(snapshot, document) is not { } toolingDocument)
            return null;

        RequirePosition(toolingDocument, position, "type-layout");

        TypeLayoutInspection? layout = toolingDocument.GetTypeLayoutAtPosition(position);
        return layout is null ? null : MapTypeLayout((ToolingBackendSnapshot)snapshot, layout);
    }

    public BackendTypeLayoutInspection? GetTypeLayoutBySubject(BackendSnapshot snapshot, BackendDocumentHandle document, string subject)
    {
        if (FindEditorDocument(snapshot, document) is not { } toolingDocument)
            return null;

        TypeLayoutInspection? layout = toolingDocument.GetTypeLayoutBySubject(subject);
        return layout is null ? null : MapTypeLayout((ToolingBackendSnapshot)snapshot, layout);
    }

    public BackendDefinitionResult? GetTypeDefinitions(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        if (FindEditorDocument(snapshot, document) is not { } toolingDocument)
            return null;

        RequirePosition(toolingDocument, position, "type-definition");

        IReadOnlyList<SymbolDefinition> definitions = toolingDocument.GetTypeDefinitions(position);
        if (definitions.Count == 0)
        {
            return null;
        }

        var texts = new Dictionary<DocumentUri, string>();
        var targets = new List<BackendDefinitionTarget>(definitions.Count);
        foreach (SymbolDefinition definition in definitions)
        {
            if (MapDefinitionTarget(snapshot, definition, texts) is { } target)
            {
                targets.Add(target);
            }
        }

        return targets.Count == 0 ? null : new BackendDefinitionResult(texts, targets);
    }

    public BackendDefinitionResult? GetImplementations(BackendSnapshot snapshot, BackendSymbolHandle symbol)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (symbol is not CvoloSymbolHandle handle || !ReferenceEquals(handle.Snapshot, toolingSnapshot))
        {
            // A foreign or mismatched symbol handle must never resolve against the
            // wrong snapshot; return no result rather than an unrelated contract (§15).
            return null;
        }

        IReadOnlyList<ToolingImplementation> implementations = toolingSnapshot.GetImplementations(handle.SymbolId);
        if (implementations.Count == 0)
        {
            return null;
        }

        var texts = new Dictionary<DocumentUri, string>();
        var targets = new List<BackendDefinitionTarget>(implementations.Count);
        foreach (ToolingImplementation implementation in implementations)
        {
            if (MapDefinitionTarget(snapshot, implementation.Definition, texts) is { } target)
            {
                targets.Add(target);
            }
        }

        return targets.Count == 0 ? null : new BackendDefinitionResult(texts, targets);
    }

    public BackendTypeHierarchyResult? PrepareTypeHierarchy(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        if (FindEditorDocument(snapshot, document) is not { } toolingDocument)
            return null;

        RequirePosition(toolingDocument, position, "type-hierarchy");

        ToolingHierarchyItem? item = toolingDocument.PrepareTypeHierarchy(position);
        if (item is null)
        {
            return null;
        }

        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var texts = new Dictionary<DocumentUri, string>();
        if (MapHierarchyItem(snapshot, item, toolingSnapshot, texts) is not { } mapped)
        {
            return null;
        }

        return new BackendTypeHierarchyResult(texts, [mapped]);
    }

    public BackendTypeHierarchyResult GetSupertypes(BackendSnapshot snapshot, DocumentUri document, string key)
    {
        return HierarchyByKey(snapshot, document, key, supertypes: true);
    }

    public BackendTypeHierarchyResult GetSubtypes(BackendSnapshot snapshot, DocumentUri document, string key)
    {
        return HierarchyByKey(snapshot, document, key, supertypes: false);
    }

    private BackendTypeHierarchyResult HierarchyByKey(BackendSnapshot snapshot, DocumentUri document, string key, bool supertypes)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (FindDocument(toolingSnapshot, document) is not { } anchor)
        {
            return EmptyHierarchy;
        }

        // The follow-up request carries a server-owned contract name rather than a snapshot-scoped
        // symbol handle, so it re-resolves against whatever snapshot is current (§42). A contract that
        // was renamed or removed stops resolving instead of resurrecting a stale hierarchy.
        ToolingHierarchyItem? item = toolingSnapshot.GetTypeHierarchyByKey(anchor.Id, key);
        if (item is null)
        {
            return EmptyHierarchy;
        }

        IReadOnlyList<ToolingHierarchyItem> items = supertypes
            ? toolingSnapshot.GetSupertypes(item.SymbolId)
            : toolingSnapshot.GetSubtypes(item.SymbolId);
        if (items.Count == 0)
        {
            return EmptyHierarchy;
        }

        var texts = new Dictionary<DocumentUri, string>();
        var mapped = new List<BackendHierarchyItem>(items.Count);
        foreach (ToolingHierarchyItem related in items)
        {
            if (MapHierarchyItem(snapshot, related, toolingSnapshot, texts) is { } mappedItem)
            {
                mapped.Add(mappedItem);
            }
        }

        return new BackendTypeHierarchyResult(texts, mapped);
    }

    private BackendHierarchyItem? MapHierarchyItem(
        BackendSnapshot snapshot,
        ToolingHierarchyItem item,
        ProjectSnapshot toolingSnapshot,
        Dictionary<DocumentUri, string>? texts = null)
    {
        if (MapDefinitionTarget(snapshot, item.Definition, texts) is not { } target)
        {
            return null;
        }

        // The key is the contract name, which the server can resolve again on the next request.
        return new BackendHierarchyItem(item.Name, MapSymbolKind(item.Kind), target.Document, target.Range, target.SelectionSpan, item.Name);
    }

    private static DocumentSnapshot? FindDocument(ProjectSnapshot toolingSnapshot, DocumentUri document)
    {
        foreach (DocumentSnapshot candidate in toolingSnapshot.Documents.Values)
        {
            if (ToDocumentUri(candidate.FilePath) == document)
            {
                return candidate;
            }
        }

        return null;
    }

    private static readonly BackendTypeHierarchyResult EmptyHierarchy =
        new(new Dictionary<DocumentUri, string>(), Array.Empty<BackendHierarchyItem>());


    private BackendTypeLayoutInspection? MapTypeLayout(BackendSnapshot snapshot, TypeLayoutInspection layout)
    {
        var texts = new Dictionary<DocumentUri, string>();
        var members = new List<BackendTypeLayoutMember>(layout.Members.Count);
        foreach (TypeLayoutMemberInspection member in layout.Members)
        {
            members.Add(new BackendTypeLayoutMember(
                member.Name,
                member.TypeDisplay,
                member.Offset,
                member.Size,
                member.Alignment,
                MapMemberNavigation(snapshot, member.Navigation, texts)));
        }

        var padding = new List<BackendTypeLayoutPadding>(layout.Padding.Count);
        foreach (TypeLayoutPaddingInspection region in layout.Padding)
        {
            BackendTypeLayoutPaddingKind kind = region.Kind switch
            {
                ToolingPaddingKind.Internal => BackendTypeLayoutPaddingKind.Internal,
                ToolingPaddingKind.Tail => BackendTypeLayoutPaddingKind.Tail,
                _ => BackendTypeLayoutPaddingKind.Internal,
            };

            padding.Add(new BackendTypeLayoutPadding(region.Offset, region.Size, kind));
        }

        if (layout.Size < 0 || layout.Alignment <= 0 || layout.PayloadSize < 0 || layout.PaddingSize < 0)
        {
            _logger.Write(CoreLogLevel.Debug, $"Dropped the layout of '{layout.TypeDisplay}': the compiler reported negative or zero size/alignment values.");
            return null;
        }

        // Every member and padding run must fit inside the reported object size, otherwise the numbers
        // cannot be rendered as a consistent table.
        foreach (BackendTypeLayoutMember member in members)
        {
            if (member.Offset < 0 || member.Size < 0 || member.Offset + member.Size > layout.Size)
            {
                _logger.Write(CoreLogLevel.Debug, $"Dropped the layout of '{layout.TypeDisplay}': member '{member.Name}' does not fit inside the reported size.");
                return null;
            }
        }

        foreach (BackendTypeLayoutPadding region in padding)
        {
            if (region.Offset < 0 || region.Size < 0 || region.Offset + region.Size > layout.Size)
            {
                _logger.Write(CoreLogLevel.Debug, $"Dropped the layout of '{layout.TypeDisplay}': padding at offset {region.Offset} does not fit inside the reported size.");
                return null;
            }
        }

        return new BackendTypeLayoutInspection(
            layout.TypeDisplay,
            layout.TargetDisplay,
            layout.Size,
            layout.Alignment,
            layout.PayloadSize,
            layout.PaddingSize,
            layout.Stride,
            layout.ElementCount,
            layout.ElementSize,
            layout.ElementAlignment,
            members,
            padding,
            MapDefinitionTarget(snapshot, layout.Definition, texts),
            texts,
            layout.Subject);
    }

    /// <summary>
    /// Maps the compiler-resolved navigation facts of one member row. A target whose declaration is not
    /// in the captured snapshot, or whose span cannot exist in that document's text, is dropped rather
    /// than repaired, so the view never offers a link that opens nothing.
    /// </summary>
    private BackendTypeLayoutMemberNavigation? MapMemberNavigation(
        BackendSnapshot snapshot,
        TypeLayoutMemberNavigation? navigation,
        Dictionary<DocumentUri, string> texts)
    {
        if (navigation is null || string.IsNullOrWhiteSpace(navigation.Signature))
        {
            return null;
        }

        return new BackendTypeLayoutMemberNavigation(
            navigation.Signature,
            navigation.Documentation,
            MapDefinitionTarget(snapshot, navigation.Definition, texts),
            MapDefinitionTarget(snapshot, navigation.TypeDefinition, texts),
            MapDefinitionTarget(snapshot, navigation.NestedLayout, texts));
    }

    private BackendDefinitionTarget? MapDefinitionTarget(
        BackendSnapshot snapshot,
        SymbolDefinition? definition,
        Dictionary<DocumentUri, string>? texts = null)
    {
        if (definition is null)
        {
            return null;
        }

        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (!toolingSnapshot.TryGetDocument(definition.DocumentId, out DocumentSnapshot? document))
        {
            return null;
        }

        DocumentUri uri = ToDocumentUri(document.FilePath);
        texts?.TryAdd(uri, document.Text.ToString());
        return new BackendDefinitionTarget(
            uri,
            new CoreTextSpan(definition.Range.Start, definition.Range.Length),
            new CoreTextSpan(definition.SelectionSpan.Start, definition.SelectionSpan.Length));
    }
}
