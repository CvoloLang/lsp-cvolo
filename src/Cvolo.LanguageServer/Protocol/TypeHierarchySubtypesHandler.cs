using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Cvolo.LanguageServer.Observability;
using global::Cvolo.Observability.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// typeHierarchy/subtypes handler. Re-resolves the contract the client echoed back by its
/// compiler-provided key and returns its declared implementing contracts only.
/// </summary>
internal sealed class TypeHierarchySubtypesHandler(ILspLogger logger, Func<DocumentStore> storeAccessor, ICompilerObserver? observer = null)
{
    private readonly ICompilerObserver _observer = observer ?? NoopCompilerObserver.Instance;
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("typeHierarchy/subtypes", UseSingleObjectParameterDeserialization = true)]
    public async Task<TypeHierarchyItemPayload[]?> Subtypes(TypeHierarchySubtypesParams? parameters, CancellationToken cancellationToken)
    {
        using var trace = FeatureTrace.Start(_observer, "typeHierarchy.subtypes", cancellationToken);

        if (parameters?.Item is not { } item
            || item.Uri is not { Length: > 0 } uriText
            || item.Data is not { Length: > 0 } key)
        {
            logger.Debug("[typeHierarchy/subtypes] request without a resolvable item; ignored.");
            trace.Complete("not_found", "target_not_captured");
            return null;
        }

        if (!Uri.TryCreate(uriText, UriKind.Absolute, out Uri? uri)
            || !TextDocumentSyncHandler.TryCreateDocumentUri(uri, out DocumentUri documentUri))
        {
            logger.Debug($"[typeHierarchy/subtypes] unusable item uri '{uriText}'.");
            trace.Complete("unsupported", "unsupported_uri");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[typeHierarchy/subtypes] '{documentUri}' is not open; no result.");
            trace.Complete("not_found", "document_not_open");
            return null;
        }

        BackendTypeHierarchyResult result;
        IOperationScope backend = trace.Backend();
        try
        {
            result = await Task.Run(
                () => Store.GetSubtypes(context.CurrentProjectSnapshot, documentUri, key),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[typeHierarchy/subtypes] backend failure for '{documentUri}': {ex.GetType().Name}");
            trace.CompleteBackend(backend, "faulted", "exception");
            trace.Complete("faulted", "exception");
            return null;
        }

        trace.CompleteBackend(backend, "completed");

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[typeHierarchy/subtypes] discarded stale result for '{documentUri}'.");
            trace.Freshness(false);
            trace.Complete("stale_discarded", "document_version_stale");
            return null;
        }

        trace.Freshness(true);

        TypeHierarchyItemPayload[]? mapped = HierarchyItemMapper.MapAll(result);
        if (mapped is null)
        {
            trace.MappingRejected("invalid_target_span");
            trace.Complete("not_found", "invalid_target_span");
            return null;
        }

        trace.Complete("success");
        return mapped;
    }
}
