using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// The server side of the v3 anchored CodeLens bridge. A clickable lens hands the client an opaque
/// token, never a position. When the client clicks, it first asks
/// <c>workspace/executeCommand</c> to resolve the token; the server revalidates the captured snapshot
/// against the live document store, re-resolves the fact it kept (references or a layout) from the
/// anchored offset, and answers with validated data plus a fresh action token. The client then asks
/// <c>cvolo/validateAnchoredAction.v3</c> for that action token immediately before it acts, and the
/// server answers only while the snapshot is still current. Every failure degrades to an
/// <c>Unavailable</c> result with a reason that names no source text or exception (§21, §39).
/// </summary>
internal sealed class AnchoredLensHandler(
    ILspLogger logger,
    Func<DocumentStore> storeAccessor,
    AnchoredActionRegistry registry)
{
    internal const string ResolveCommand = "cvolo.resolveAnchoredLens.v3";
    internal const string ValidateMethod = "cvolo/validateAnchoredAction.v3";
    internal const string KindShowReferences = "ShowReferences";
    internal const string KindShowLayout = "ShowLayout";
    internal const string KindUnavailable = "Unavailable";
    internal const int SchemaVersion = 3;

    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("workspace/executeCommand", UseSingleObjectParameterDeserialization = true)]
    public async Task<AnchoredActionResultV3> ExecuteCommand(ExecuteCommandParamsPayload? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.Command is not ResolveCommand)
        {
            return Unavailable("Unknown command.");
        }

        AnchoredLensRequestV3? request = ReadRequest(parameters);
        if (request is null || request.SchemaVersion != SchemaVersion || string.IsNullOrEmpty(request.LensToken))
        {
            return Unavailable("Malformed anchored lens request.");
        }

        if (!registry.TryGet(request.LensToken, out AnchoredActionRecord record) || record.IsAction)
        {
            return Unavailable("The anchored lens is no longer available.");
        }

        if (!Store.IsCurrent(record.Context))
        {
            return Unavailable("The declaration changed after the lens was shown.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return record.Kind switch
            {
                BackendCodeLensKind.References => await ResolveReferencesAsync(record, request.WireUri, cancellationToken).ConfigureAwait(false),
                BackendCodeLensKind.Layout => await ResolveLayoutAsync(record, request.WireUri, cancellationToken).ConfigureAwait(false),
                _ => Unavailable("The lens has no anchored action."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[anchored] resolve failed for '{record.Uri}': {ex.GetType().Name}");
            return Unavailable("The action could not be resolved.");
        }
    }

    [JsonRpcMethod("cvolo/validateAnchoredAction.v3", UseSingleObjectParameterDeserialization = true)]
    public AnchoredValidationResultV3 Validate(AnchoredActionValidationV3? parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (parameters is null || string.IsNullOrEmpty(parameters.ActionToken))
        {
            return new AnchoredValidationResultV3(false, "Missing action token.");
        }

        // The action token is single use: a validated action cannot be replayed from a stale frame.
        if (!registry.TryConsume(parameters.ActionToken, out AnchoredActionRecord record) || !record.IsAction)
        {
            return new AnchoredValidationResultV3(false, "The action is no longer available.");
        }

        bool current = Store.IsCurrent(record.Context);
        return new AnchoredValidationResultV3(
            current,
            current ? null : "The document changed after the action was resolved.");
    }

    private async Task<AnchoredActionResultV3> ResolveReferencesAsync(
        AnchoredActionRecord record,
        string wireUri,
        CancellationToken cancellationToken)
    {
        BackendReferenceResult? references = await Task.Run(() =>
        {
            BackendSymbolInfo? symbol = Store.GetSymbolAtPosition(record.Context, record.AnchorOffset);
            return symbol is null
                ? null
                : Store.GetReferences(record.Context.CurrentProjectSnapshot, symbol.Symbol, includeDeclaration: true);
        }, cancellationToken).ConfigureAwait(false);

        if (references is null)
        {
            return Unavailable("The declaration is no longer resolvable.");
        }

        // The answer is transported only if the very snapshot it was computed from is still current.
        if (!Store.IsCurrent(record.Context))
        {
            return Unavailable("The declaration changed while the action was resolved.");
        }

        Location[] locations = ReferencesHandler.Map(references);
        Position? anchor = ToPosition(record.Context.Document.Text, record.AnchorOffset);
        string actionToken = registry.Mint(record with { IsAction = true });

        return new AnchoredActionResultV3(
            SchemaVersion,
            KindShowReferences,
            actionToken,
            wireUri,
            anchor,
            locations,
            Layout: null,
            Reason: null);
    }

    private async Task<AnchoredActionResultV3> ResolveLayoutAsync(
        AnchoredActionRecord record,
        string wireUri,
        CancellationToken cancellationToken)
    {
        BackendTypeLayoutInspection? layout = await Task.Run(
            () => Store.GetTypeLayoutAtPosition(record.Context, record.AnchorOffset),
            cancellationToken).ConfigureAwait(false);

        if (layout is null)
        {
            return Unavailable("The layout is no longer available.");
        }

        if (!Store.IsCurrent(record.Context))
        {
            return Unavailable("The declaration changed while the action was resolved.");
        }

        TypeLayoutResponse mapped = TypeLayoutHandler.Map(layout);
        string actionToken = registry.Mint(record with { IsAction = true });

        return new AnchoredActionResultV3(
            SchemaVersion,
            KindShowLayout,
            actionToken,
            wireUri,
            Anchor: null,
            Locations: null,
            mapped,
            Reason: null);
    }

    private static AnchoredLensRequestV3? ReadRequest(ExecuteCommandParamsPayload parameters)
    {
        if (parameters.Arguments is not { Count: > 0 } arguments)
        {
            return null;
        }

        try
        {
            return arguments[0].ToObject<AnchoredLensRequestV3>();
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private static Position? ToPosition(string text, int offset)
    {
        var index = new LineIndex(text);
        return index.TryGetPosition(offset, out TextPosition mapped)
            ? new Position(mapped.Line, mapped.Character)
            : null;
    }

    private static AnchoredActionResultV3 Unavailable(string reason) =>
        new(SchemaVersion, KindUnavailable, Reason: reason);
}
