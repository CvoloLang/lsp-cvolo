using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Cvolo.LanguageServer.Observability;
using global::Cvolo.Observability.Abstractions;
using StreamJsonRpc;
using System.Diagnostics;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/signatureHelp handler. All callable resolution stays compiler-owned; this layer only
/// maps positions, enforces snapshot freshness, validates the compiler result, and translates
/// protocol-neutral DTOs to LSP shapes. Capabilities (label offsets, per-signature active
/// parameter) are negotiated once at initialize and read through the session (??11, ??14).
/// </summary>
internal sealed class SignatureHelpHandler(
    ILspLogger logger,
    Func<DocumentStore> storeAccessor,
    Func<bool> labelOffsetSupport,
    Func<bool> activeParameterSupport,
    ICompilerObserver? observer = null)
{
    private readonly ICompilerObserver _observer = observer ?? NoopCompilerObserver.Instance;
    private DocumentStore? _store;
    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
    public async Task<SignatureHelpResponse?> SignatureHelp(SignatureHelpParams? parameters, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        using var trace = FeatureTrace.Start(_observer, "signatureHelp", cancellationToken);

        if (parameters?.TextDocument is not { } textDocument || parameters.Position is not { } position)
        {
            logger.Debug("[signatureHelp] missing document or position");
            trace.Complete("not_found", "document_not_open");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            trace.Complete("unsupported", "unsupported_uri");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[signatureHelp] no captured snapshot uri='{documentUri}'");
            trace.Complete("not_found", "document_not_open");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            trace.Complete("not_found", "invalid_position");
            return null;
        }

        logger.Debug($"[signatureHelp] start uri='{documentUri}' v{context.Version} position={position.Line}:{position.Character} offset={offset}");
        BackendSignatureHelpResult? result;
        IOperationScope backend = trace.Backend();
        try
        {
            result = await Task.Run(() => Store.GetSignatureHelp(context, offset), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[signatureHelp] backend failure uri='{documentUri}' v{context.Version} offset={offset} elapsed={timer.ElapsedMilliseconds}ms: {ex.GetType().Name}");
            trace.CompleteBackend(backend, "faulted", "exception");
            trace.Complete("faulted", "exception");
            return null;
        }

        trace.CompleteBackend(backend, "completed");

        cancellationToken.ThrowIfCancellationRequested();
        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[signatureHelp] stale snapshot uri='{documentUri}' v{context.Version} elapsed={timer.ElapsedMilliseconds}ms");
            trace.Freshness(false);
            trace.Complete("stale_discarded", "document_version_stale");
            return null;
        }

        trace.Freshness(true);

        if (result is null)
        {
            logger.Debug($"[signatureHelp] compiler returned null uri='{documentUri}' v{context.Version} offset={offset} elapsed={timer.ElapsedMilliseconds}ms");
            trace.Complete("not_found", "symbol_lookup_miss");
            return null;
        }

        var mapped = Map(result);
        if (mapped is null)
        {
            logger.Debug($"[signatureHelp] discarded malformed result uri='{documentUri}' v{context.Version} offset={offset}");
            trace.MappingRejected("invalid_target_span");
            trace.Complete("not_found", "invalid_target_span");
            return null;
        }

        logger.Debug($"[signatureHelp] end uri='{documentUri}' v{context.Version} status=ok signatures={result.Signatures.Count} elapsed={timer.ElapsedMilliseconds}ms");
        trace.Complete("success");
        return mapped;
    }

    /// <summary>
    /// Validates the compiler result (??32) and maps it to the wire shape. Any malformed signature,
    /// label, parameter span or active-parameter value makes the whole result null ??? never a
    /// clamped or silent repair. Never clamp; never omit active-signature parameter data (LSP 3.17
    /// would default a missing top-level activeParameter to 0 and fabricate a choice).
    /// </summary>
    private SignatureHelpResponse? Map(BackendSignatureHelpResult result)
    {
        var signatures = result.Signatures;
        if (signatures.Count == 0)
            return null;

        int activeSignature = result.ActiveSignature;
        if (activeSignature < 0 || activeSignature >= signatures.Count)
        {
            logger.Warning($"[signatureHelp] discarding result with out-of-range ActiveSignature {activeSignature}.");
            return null;
        }

        for (var i = 0; i < signatures.Count; i++)
        {
            BackendSignatureCandidate signature = signatures[i];
            if (string.IsNullOrEmpty(signature.Label))
            {
                logger.Warning("[signatureHelp] discarding result with an empty signature label.");
                return null;
            }

            foreach (BackendSignatureParameter parameter in signature.Parameters)
            {
                var span = parameter.LabelSpan;
                if (span.Start < 0 || span.Length <= 0 || span.Start > signature.Label.Length || span.Length > signature.Label.Length - span.Start)
                {
                    logger.Warning($"[signatureHelp] discarding result whose parameter span ({span.Start} + {span.Length}) exceeds the label length {signature.Label.Length}.");
                    return null;
                }
            }

            // Active-parameter rules: the active signature must carry its compiler value (null only
            // when it has no parameters); a non-active signature may carry none (null) or an
            // in-range value ??? out-of-range is malformed (??11, ??32).
            bool isActive = i == activeSignature;
            int? activeParameter = signature.ActiveParameter;
            if (isActive)
            {
                if (signature.Parameters.Count == 0)
                {
                    if (activeParameter is not null)
                    {
                        logger.Warning("[signatureHelp] discarding result: active signature has no parameters but carries an active parameter.");
                        return null;
                    }
                }
                else if (activeParameter is null || activeParameter < 0 || activeParameter >= signature.Parameters.Count)
                {
                    logger.Warning("[signatureHelp] discarding result: active signature lacks a valid active parameter.");
                    return null;
                }
            }
            else if (activeParameter is not null && (activeParameter < 0 || activeParameter >= signature.Parameters.Count))
            {
                logger.Warning("[signatureHelp] discarding result: non-active signature carries an out-of-range active parameter.");
                return null;
            }
        }

        var labelOffset = labelOffsetSupport();
        var perSignatureActiveParameter = activeParameterSupport();

        var mapped = new SignatureInformationPayload[signatures.Count];
        int? topLevelActiveParameter = null;
        for (var i = 0; i < signatures.Count; i++)
        {
            BackendSignatureCandidate signature = signatures[i];
            var parameters = new ParameterInformationPayload[signature.Parameters.Count];
            for (var j = 0; j < signature.Parameters.Count; j++)
            {
                BackendSignatureParameter parameter = signature.Parameters[j];
                object label = labelOffset
                    ? new[] { parameter.LabelSpan.Start, parameter.LabelSpan.Start + parameter.LabelSpan.Length }
                    : signature.Label.Substring(parameter.LabelSpan.Start, parameter.LabelSpan.Length);
                parameters[j] = new ParameterInformationPayload(label, parameter.Documentation);
            }

            int? wireActiveParameter = perSignatureActiveParameter ? signature.ActiveParameter : null;
            mapped[i] = new SignatureInformationPayload(
                signature.Label,
                parameters,
                signature.Documentation,
                wireActiveParameter);

            if (i == activeSignature)
            {
                topLevelActiveParameter = signature.ActiveParameter;
            }
        }

        return new SignatureHelpResponse(mapped, activeSignature, topLevelActiveParameter);
    }
}