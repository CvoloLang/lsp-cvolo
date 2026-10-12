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
/// textDocument/selectionRange handler. Returns one chain per requested cursor position, innermost
/// level first, with each level linked to the next larger region. The chain is produced by the
/// compiler; this handler neither re-parses the source nor repairs a chain it cannot vouch for
/// (§45, §56, §81).
/// </summary>
internal sealed class SelectionRangeHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/selectionRange", UseSingleObjectParameterDeserialization = true)]
    public async Task<SelectionRangePayload[]?> SelectionRanges(SelectionRangeParamsPayload? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[selectionRange] request without a textDocument; ignored.");
            return null;
        }

        Position[]? requested = parameters.Positions;
        if (requested is null)
        {
            logger.Debug("[selectionRange] request without positions; ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[selectionRange] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[selectionRange] '{documentUri}' is not open; no chains.");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        var offsets = new int[requested.Length];
        for (int i = 0; i < requested.Length; i++)
        {
            if (!index.TryGetOffset(new TextPosition(requested[i].Line, requested[i].Character), out offsets[i]))
            {
                logger.Debug($"[selectionRange] cursor position {i} does not map to the captured text of '{documentUri}'.");
                return null;
            }
        }

        IReadOnlyList<BackendSelectionRange?> chains;
        try
        {
            chains = await Task.Run(() => Store.GetSelectionRanges(context, offsets), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[selectionRange] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[selectionRange] discarded stale result for '{documentUri}'.");
            return null;
        }

        int textLength = context.Document.Text.Length;
        var mapped = new SelectionRangePayload[requested.Length];
        for (int i = 0; i < mapped.Length; i++)
        {
            if (i >= chains.Count)
            {
                break;
            }

            if (TryMapChain(chains[i], index, textLength, out SelectionRangePayload chain))
            {
                mapped[i] = chain;
            }
        }

        return mapped;
    }

    /// <summary>
    /// Rebuilds one chain from the innermost level outwards. A level that does not exist in the
    /// captured text, or a parent that does not strictly contain its child, means the chain cannot be
    /// walked: the whole chain is dropped, because a reader who meets one impossible level would
    /// select a region that does not exist (§81, §85).
    /// </summary>
    private bool TryMapChain(
        BackendSelectionRange? chain,
        LineIndex index,
        int textLength,
        out SelectionRangePayload payload)
    {
        if (chain is null)
        {
            payload = null!;
            return false;
        }

        var levels = new List<LspRange>();
        for (BackendSelectionRange? level = chain; level is not null; level = level.Parent)
        {
            if (!SpanMapper.TryMapRange(index, textLength, level.Range, out LspRange mapped))
            {
                logger.Debug("[selectionRange] dropped a chain whose level does not map to the captured text.");
                payload = null!;
                return false;
            }

            levels.Add(mapped);
        }

        SelectionRangePayload? parent = null;
        for (int i = levels.Count - 1; i > 0; i--)
        {
            if (!StrictlyContains(levels[i], levels[i - 1]))
            {
                logger.Debug("[selectionRange] dropped a chain whose parent does not strictly contain the level inside it.");
                payload = null!;
                return false;
            }

            parent = new SelectionRangePayload { Range = levels[i], Parent = parent };
        }

        payload = new SelectionRangePayload { Range = levels[0], Parent = parent };
        return true;
    }

    /// <summary>
    /// Whether <paramref name="outer"/> strictly contains <paramref name="inner"/>: it starts no later
    /// and ends no earlier, and the two are not the same region. Two equal levels would make the
    /// chain a no-op step, which is never a meaningful selection boundary.
    /// </summary>
    private static bool StrictlyContains(LspRange outer, LspRange inner)
    {
        int start = Compare(outer.Start, inner.Start);
        if (start > 0)
        {
            // The parent starts after the level it should contain.
            return false;
        }

        int end = Compare(outer.End, inner.End);
        if (end < 0)
        {
            // The parent ends before the level it should contain.
            return false;
        }

        return start < 0 || end > 0;
    }

    private static int Compare(Position left, Position right)
    {
        int byLine = left.Line.CompareTo(right.Line);
        return byLine != 0 ? byLine : left.Character.CompareTo(right.Character);
    }
}
