using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// typeHierarchy/subtypes handler. Re-resolves the contract the client echoed back by its
/// compiler-provided key and returns the contracts that declare it as a direct base; a concrete
/// type that merely conforms is never a subtype (§13, §14, §42).
/// </summary>
internal sealed class TypeHierarchySubtypesHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("typeHierarchy/subtypes", UseSingleObjectParameterDeserialization = true)]
    public async Task<TypeHierarchyItemPayload[]?> Subtypes(TypeHierarchySubtypesParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.Item is not { } item
            || item.Uri is not { Length: > 0 } uriText
            || item.Data is not { Length: > 0 } key)
        {
            logger.Debug("[typeHierarchy/subtypes] request without a resolvable item; ignored.");
            return null;
        }

        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri)
            || !TextDocumentSyncHandler.TryCreateDocumentUri(uri, out DocumentUri documentUri))
        {
            logger.Debug($"[typeHierarchy/subtypes] unusable item uri '{uriText}'.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[typeHierarchy/subtypes] '{documentUri}' is not open; no result.");
            return null;
        }

        BackendTypeHierarchyResult result;
        try
        {
            result = await Task.Run(
                () => Store.GetSubtypes(context.CurrentProjectSnapshot, documentUri, key),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[typeHierarchy/subtypes] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[typeHierarchy/subtypes] discarded stale result for '{documentUri}'.");
            return null;
        }

        return HierarchyItemMapper.MapAll(result);
    }
}
