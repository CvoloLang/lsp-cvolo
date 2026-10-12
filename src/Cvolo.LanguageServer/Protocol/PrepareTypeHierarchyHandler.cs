using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/prepareTypeHierarchy handler. Returns the declared interface or protocol at the
/// requested position, or null for a concrete type: structural conformance is reported by go to
/// implementation, not by the type hierarchy (§12, §13).
/// </summary>
internal sealed class PrepareTypeHierarchyHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/prepareTypeHierarchy", UseSingleObjectParameterDeserialization = true)]
    public async Task<TypeHierarchyItemPayload[]?> Prepare(TypeHierarchyPrepareParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument || parameters.Position is not { } position)
        {
            logger.Debug("[prepareTypeHierarchy] request without a textDocument or position; ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[prepareTypeHierarchy] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[prepareTypeHierarchy] '{documentUri}' is not open; no item.");
            return null;
        }

        if (!new LineIndex(context.Document.Text).TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            logger.Debug($"[prepareTypeHierarchy] invalid request position {position.Line}:{position.Character} for '{documentUri}'.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        BackendTypeHierarchyResult? result;
        try
        {
            result = await Task.Run(
                () => Store.PrepareTypeHierarchy(context, offset),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[prepareTypeHierarchy] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context) || result is null)
        {
            logger.Debug($"[prepareTypeHierarchy] discarded stale or empty result for '{documentUri}'.");
            return null;
        }

        return HierarchyItemMapper.MapAll(result);
    }
}
