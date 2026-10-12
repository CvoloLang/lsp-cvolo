using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// <c>cvolo/typeLayout</c> request. Serves the complete object layout of one type so the editor can
/// open its read-only layout view. Every number is the compiler's own: the client may format the
/// table, but it may not derive an offset, a size, an alignment or a padding run, and the compiler
/// has no second layout table for the editor to disagree with (§19, §20, §22, §57).
/// </summary>
internal sealed class TypeLayoutHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("cvolo/typeLayout", UseSingleObjectParameterDeserialization = true)]
    public async Task<TypeLayoutResponse?> TypeLayout(TypeLayoutParamsPayload? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[typeLayout] request without a textDocument; ignored.");
            return null;
        }

        // A request names the type either by a source position (the first look) or by the subject the
        // previous answer returned (a refresh). Either way the text document anchors the project whose
        // snapshot the compiler resolves against (§42).
        string? subject = string.IsNullOrWhiteSpace(parameters.Subject) ? null : parameters.Subject;
        Position? position = parameters.Position;
        if (subject is null && position is null)
        {
            logger.Debug("[typeLayout] request without a position or subject; ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[typeLayout] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[typeLayout] '{documentUri}' is not open; no layout.");
            return null;
        }

        BackendTypeLayoutInspection? layout;
        try
        {
            if (subject is not null)
            {
                layout = await Task.Run(() => Store.GetTypeLayoutBySubject(context, subject), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var index = new LineIndex(context.Document.Text);
                if (!index.TryGetOffset(new TextPosition(position!.Line, position.Character), out int offset))
                {
                    logger.Debug($"[typeLayout] the position does not map to the captured text of '{documentUri}'.");
                    return null;
                }

                layout = await Task.Run(() => Store.GetTypeLayoutAtPosition(context, offset), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[typeLayout] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[typeLayout] discarded stale result for '{documentUri}'.");
            return null;
        }

        return layout is null ? null : Map(layout);
    }

    private static TypeLayoutResponse Map(BackendTypeLayoutInspection layout)
    {
        IReadOnlyDictionary<DocumentUri, string> texts = layout.DocumentTexts ?? EmptyTexts;
        var indexes = new Dictionary<DocumentUri, LineIndex>();

        var members = new TypeLayoutMemberResponse[layout.Members.Count];
        for (int i = 0; i < members.Length; i++)
        {
            BackendTypeLayoutMember member = layout.Members[i];
            members[i] = new TypeLayoutMemberResponse(
                member.Name,
                member.TypeDisplay,
                member.Offset,
                member.Size,
                member.Alignment,
                MapNavigation(member.Navigation, texts, indexes));
        }

        var padding = new TypeLayoutPaddingResponse[layout.Padding.Count];
        for (int i = 0; i < padding.Length; i++)
        {
            BackendTypeLayoutPadding region = layout.Padding[i];
            padding[i] = new TypeLayoutPaddingResponse(region.Offset, region.Size, MapPaddingKind(region.Kind));
        }

        return new TypeLayoutResponse(
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
            MapLocation(layout.Definition, texts, indexes),
            layout.Subject);
    }

    private static readonly IReadOnlyDictionary<DocumentUri, string> EmptyTexts =
        new Dictionary<DocumentUri, string>();

    /// <summary>
    /// Maps one member row's compiler-resolved targets. A target whose document text was not captured
    /// with the layout, or whose span cannot exist in that text, is dropped rather than clamped, so a
    /// client never receives a link that opens the wrong place (§19, §39.3).
    /// </summary>
    private static TypeLayoutMemberNavigationResponse? MapNavigation(
        BackendTypeLayoutMemberNavigation? navigation,
        IReadOnlyDictionary<DocumentUri, string> texts,
        Dictionary<DocumentUri, LineIndex> indexes)
    {
        if (navigation is null)
        {
            return null;
        }

        return new TypeLayoutMemberNavigationResponse(
            navigation.Signature,
            navigation.Documentation,
            MapLocation(navigation.Definition, texts, indexes),
            MapLocation(navigation.TypeDefinition, texts, indexes),
            MapLocation(navigation.NestedLayout, texts, indexes));
    }

    private static Location? MapLocation(
        BackendDefinitionTarget? target,
        IReadOnlyDictionary<DocumentUri, string> texts,
        Dictionary<DocumentUri, LineIndex> indexes)
    {
        if (target is null || !texts.TryGetValue(target.Document, out string? text))
        {
            return null;
        }

        TextSpan span = target.SelectionSpan;
        if (span.Start < 0 || span.Length < 0 || span.End > text.Length)
        {
            return null;
        }

        if (!indexes.TryGetValue(target.Document, out LineIndex? index))
        {
            index = new LineIndex(text);
            indexes[target.Document] = index;
        }

        if (!index.TryGetRange(span, out TextRange range))
        {
            return null;
        }

        return new Location
        {
            Uri = new Uri(target.Document.LocalPath),
            Range = new LspRange
            {
                Start = new Position(range.Start.Line, range.Start.Character),
                End = new Position(range.End.Line, range.End.Character),
            },
        };
    }

    /// <summary>
    /// Whether a padding run sits between members or after the last one, carried through verbatim so
    /// the client labels the run with the compiler's own classification.
    /// </summary>
    private static string MapPaddingKind(BackendTypeLayoutPaddingKind kind) => kind switch
    {
        BackendTypeLayoutPaddingKind.Tail => "tail",
        _ => "internal",
    };
}
