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
/// textDocument/documentHighlight handler. Returns the semantic occurrences of the binding at the
/// requested position, all of them inside the one document. Highlighting is semantic: a same-spelled
/// identifier that resolves to another binding is not an occurrence and is never returned (§78, §85).
/// </summary>
internal sealed class DocumentHighlightHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/documentHighlight", UseSingleObjectParameterDeserialization = true)]
    public async Task<DocumentHighlight[]?> Highlights(DocumentHighlightParamsPayload? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument || parameters.Position is not { } position)
        {
            logger.Debug("[documentHighlight] request without a textDocument or position; ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[documentHighlight] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[documentHighlight] '{documentUri}' is not open; no highlights.");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            logger.Debug($"[documentHighlight] the position does not map to the captured text of '{documentUri}'.");
            return null;
        }

        IReadOnlyList<BackendDocumentHighlight> highlights;
        try
        {
            highlights = await Task.Run(() => Store.GetDocumentHighlights(context, offset), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[documentHighlight] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[documentHighlight] discarded stale result for '{documentUri}'.");
            return null;
        }

        string text = context.Document.Text;
        int textLength = text.Length;
        var mapped = new List<DocumentHighlight>(highlights.Count);
        foreach (BackendDocumentHighlight highlight in highlights)
        {
            if (SpanMapper.TryMapSingleLineRange(text, index, textLength, highlight.Range, out LspRange range))
            {
                mapped.Add(new DocumentHighlight
                {
                    Range = range,
                    Kind = MapKind(highlight.AccessKind),
                });
            }
        }

        return [.. mapped];
    }

    /// <summary>
    /// The protocol can express a read and a write separately but not a read that is also a write.
    /// A read-write occurrence and a declaration are therefore reported as plain text rather than
    /// dressed up in a colour the compiler did not describe (§79).
    /// </summary>
    private static DocumentHighlightKind MapKind(BackendReferenceAccessKind kind) => kind switch
    {
        BackendReferenceAccessKind.Read => DocumentHighlightKind.Read,
        BackendReferenceAccessKind.Write => DocumentHighlightKind.Write,
        BackendReferenceAccessKind.ReadWrite => DocumentHighlightKind.Text,
        _ => DocumentHighlightKind.Text,
    };
}
