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
/// textDocument/foldingRange handler. The foldable regions come from the compiler's own syntax
/// information; this handler never counts braces, so a region is a region the language defined and
/// not a guess about where a closing delimiter might be (§43, §56).
/// </summary>
internal sealed class FoldingRangeHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/foldingRange", UseSingleObjectParameterDeserialization = true)]
    public async Task<FoldingRange[]?> FoldingRanges(FoldingRangeParamsPayload? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[foldingRange] request without a textDocument; ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[foldingRange] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[foldingRange] '{documentUri}' is not open; no regions.");
            return null;
        }

        IReadOnlyList<BackendFoldingRange> ranges;
        try
        {
            ranges = await Task.Run(() => Store.GetFoldingRanges(context), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[foldingRange] backend failure for '{documentUri}': {ex}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[foldingRange] discarded stale result for '{documentUri}'.");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        int textLength = context.Document.Text.Length;

        var mapped = new List<FoldingRange>(ranges.Count);
        foreach (BackendFoldingRange range in ranges)
        {
            if (TryMapRange(range, index, textLength, out FoldingRange foldingRange))
            {
                mapped.Add(foldingRange);
            }
        }

        mapped.Sort(CompareRanges);
        return [.. mapped];
    }

    /// <summary>
    /// Maps one region to the lines a client folds. The backend range covers the whole region
    /// including its delimiters, so the interior between the first and last line is what disappears
    /// and the delimiters stay visible. A region that ends at the start of a line ends on the
    /// previous line, and a region that occupies a single line is not foldable at all; both are
    /// presentation decisions the protocol makes on ranges the compiler already accepted (§44).
    /// </summary>
    private bool TryMapRange(BackendFoldingRange range, LineIndex index, int textLength, out FoldingRange foldingRange)
    {
        if (!SpanMapper.TryMapRange(index, textLength, range.Range, out LspRange mapped))
        {
            logger.Debug($"[foldingRange] dropped a region whose range does not map to the captured text.");
            foldingRange = null!;
            return false;
        }

        int endLine = mapped.End.Line;
        if (endLine > mapped.Start.Line && mapped.End.Character == 0)
        {
            endLine--;
        }

        if (endLine <= mapped.Start.Line)
        {
            foldingRange = null!;
            return false;
        }

        foldingRange = new FoldingRange
        {
            StartLine = mapped.Start.Line,
            StartCharacter = mapped.Start.Character,
            EndLine = endLine,
            EndCharacter = null,
            Kind = MapKind(range.Kind),
        };
        return true;
    }

    /// <summary>
    /// A documentation block is a comment as far as the protocol is concerned: LSP has no separate
    /// documentation kind, and inventing one would be a colour the compiler never described.
    /// </summary>
    private static FoldingRangeKind? MapKind(BackendFoldingRangeKind kind) => kind switch
    {
        BackendFoldingRangeKind.Comment => FoldingRangeKind.Comment,
        BackendFoldingRangeKind.Documentation => FoldingRangeKind.Comment,
        _ => null,
    };

    private static int CompareRanges(FoldingRange left, FoldingRange right)
    {
        int byStart = left.StartLine.CompareTo(right.StartLine);
        return byStart != 0 ? byStart : left.EndLine.CompareTo(right.EndLine);
    }
}
