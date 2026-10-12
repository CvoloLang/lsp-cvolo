using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using StreamJsonRpc;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/inlayHint handler. Returns the inline annotations of the requested range of the open
/// document, taken from its captured snapshot. The requested range is honoured exactly: the server
/// never widens it to the document and never produces hints for the whole project (§55, §62, §84).
/// </summary>
internal sealed class InlayHintHandler(
    ILspLogger logger,
    Func<DocumentStore> storeAccessor,
    Func<EditorIntelligenceSettings> settingsAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/inlayHint", UseSingleObjectParameterDeserialization = true)]
    public async Task<InlayHintPayload[]?> InlayHints(InlayHintParamsPayload? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[inlayHint] request without a textDocument; ignored.");
            return null;
        }

        if (parameters.Range is not { } range)
        {
            logger.Debug("[inlayHint] request without a range; ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[inlayHint] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[inlayHint] '{documentUri}' is not open; no hints.");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        if (!TryMapRequestedRange(index, range, out int start, out int end))
        {
            logger.Debug($"[inlayHint] the requested range does not map to the captured text of '{documentUri}'.");
            return null;
        }

        BackendInlayHintOptions options = settingsAccessor().InlayHints;
        BackendOffsetFormat offsetFormat = settingsAccessor().Layout.OffsetFormat;
        IReadOnlyList<BackendInlayHint> hints;
        try
        {
            hints = await Task.Run(
                () => Store.GetInlayHints(context, new TextSpan(start, end - start), options),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[inlayHint] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[inlayHint] discarded stale result for '{documentUri}'.");
            return null;
        }

        var mapped = new List<InlayHintPayload>(hints.Count);
        foreach (BackendInlayHint hint in hints)
        {
            if (TryMapHint(hint, index, offsetFormat, out InlayHintPayload payload))
            {
                mapped.Add(payload);
            }
        }

        mapped.Sort(CompareHints);
        return [.. mapped];
    }

    private static bool TryMapRequestedRange(LineIndex index, LspRange range, out int start, out int end)
    {
        if (index.TryGetOffset(new TextPosition(range.Start.Line, range.Start.Character), out start)
            && index.TryGetOffset(new TextPosition(range.End.Line, range.End.Character), out end)
            && start <= end)
        {
            return true;
        }

        start = 0;
        end = 0;
        return false;
    }

    /// <summary>
    /// Maps one hint onto its absolute position. A position that no longer exists in the captured
    /// text is dropped rather than clamped: an annotation in the wrong place is a false fact about
    /// the source, not a cosmetic error (§84).
    /// </summary>
    private bool TryMapHint(BackendInlayHint hint, LineIndex index, BackendOffsetFormat offsetFormat, out InlayHintPayload payload)
    {
        if (!index.TryGetPosition(hint.Position, out TextPosition mapped))
        {
            logger.Debug($"[inlayHint] dropped a '{hint.Kind}' hint at position {hint.Position}, which does not map to the captured text.");
            payload = null!;
            return false;
        }

        payload = new InlayHintPayload
        {
            Position = SpanMapper.ToPosition(mapped),
            Label = PresentLabel(hint, offsetFormat),
            PaddingLeft = hint.PaddingLeft,
            PaddingRight = hint.PaddingRight,
            Kind = MapKind(hint.Kind),
        };
        return true;
    }

    /// <summary>
    /// The text a reader sees. It is the compiler's own presentation, except for a field layout hint,
    /// which is written again from the compiler's structured numbers so the offset notation matches
    /// the one the field CodeLens and the detailed view use for the same field (§17, §9).
    /// </summary>
    private static string PresentLabel(BackendInlayHint hint, BackendOffsetFormat offsetFormat) =>
        hint.FieldLayout is { } field
            ? LayoutTextFormatter.FieldLayoutHint(field, offsetFormat)
            : hint.Label;

    /// <summary>
    /// The compiler-derived category travels with the hint so a client can present or filter it
    /// without parsing the label.
    /// </summary>
    private static string MapKind(BackendInlayHintKind kind) => kind switch
    {
        BackendInlayHintKind.Type => "type",
        BackendInlayHintKind.Parameter => "parameter",
        BackendInlayHintKind.ReceiverMutability => "receiverMutability",
        BackendInlayHintKind.Layout => "layout",
        BackendInlayHintKind.EnumValue => "enumValue",
        BackendInlayHintKind.GenericArgument => "genericArgument",
        _ => "other",
    };

    private static int CompareHints(InlayHintPayload left, InlayHintPayload right)
    {
        int byLine = left.Position.Line.CompareTo(right.Position.Line);
        return byLine != 0
            ? byLine
            : left.Position.Character.CompareTo(right.Position.Character);
    }
}
