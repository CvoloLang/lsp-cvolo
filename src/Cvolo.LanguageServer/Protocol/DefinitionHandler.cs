using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using global::Cvolo.Observability.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/definition handler. Resolves the symbol at the request position
/// and its source declarations from the same immutable snapshot, then maps each
/// target's selection span against that target document's captured text — even
/// when the target is a closed project document. Malformed targets are skipped,
/// never clamped (§17, §18, §29.2).
/// </summary>
/// <remarks>
/// Every <c>return null</c> path records an explicit terminal reason (§19.1, §20.3)
/// so a dropped F12 is never indistinguishable from a legitimately absent symbol.
/// </remarks>
internal sealed class DefinitionHandler(ILspLogger logger, Func<DocumentStore> storeAccessor, ICompilerObserver? observer = null)
{
    private const string Stage = "definition";

    private const string RequestStartedEvent = "request.started";

    private const string PositionMappedEvent = "definition.position_mapped";
    private const string SymbolLookupStartedEvent = "symbol.lookup.started";
    private const string TargetsStartedEvent = "definition.targets.started";
    private const string MappingStartedEvent = "definition.mapping.started";
    private const string MappingRejectedEvent = "definition.mapping.rejected";
    private const string ResponseCompletedEvent = "definition.response.completed";

    private readonly ICompilerObserver _observer = observer ?? NoopCompilerObserver.Instance;

    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod(Methods.TextDocumentDefinitionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Location[]?> Definition(TextDocumentPositionParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[definition] request without a textDocument; ignored.");
            EmitRequestTerminal("not_found", "document_not_open");
            return null;
        }

        Position position = parameters.Position;

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[definition] non-cvolo document '{textDocument.Uri}' ignored.");
            EmitRequestTerminal("unsupported", "unsupported_uri");
            return null;
        }

        IOperationScope request = EmitRequestStarted();

        if (cancellationToken.IsCancellationRequested)
        {
            CompleteRequest(request, "cancelled", "request_cancelled");
            cancellationToken.ThrowIfCancellationRequested();
        }

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[definition] '{documentUri}' is not open; no definition.");
            CompleteRequest(request, "not_found", "document_not_open");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            logger.Debug($"[definition] invalid request position {position.Line}:{position.Character} for '{documentUri}'.");
            EmitStage(PositionMappedEvent, "invalid", "invalid_position", request.OperationId, context.Version.Value);
            CompleteRequest(request, "not_found", "invalid_position");
            return null;
        }

        EmitStage(PositionMappedEvent, "mapped", null, request.OperationId, context.Version.Value);

        if (cancellationToken.IsCancellationRequested)
        {
            CompleteRequest(request, "cancelled", "request_cancelled");
            cancellationToken.ThrowIfCancellationRequested();
        }

        BackendSymbolInfo? symbol;
        IOperationScope lookup = EmitStageOperation(SymbolLookupStartedEvent, request.OperationId, context.Version.Value);
        try
        {
            symbol = await Task.Run(() => Store.GetSymbolAtPosition(context, offset), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[definition] symbol lookup failed for '{documentUri}': {ex.GetType().Name}.");
            CompleteStage(lookup, "faulted", "exception");
            CompleteRequest(request, "faulted", "exception");
            return null;
        }

        CompleteStage(lookup, symbol is null ? "not_found" : "found", null);

        if (symbol is null)
        {
            logger.Debug($"[definition] no symbol found at the request position for '{documentUri}'.");
            CompleteRequest(request, "not_found", "symbol_lookup_miss");
            return null;
        }

        BackendDefinitionResult? result;
        IOperationScope targets = EmitStageOperation(TargetsStartedEvent, request.OperationId, context.Version.Value);
        try
        {
            result = await Task.Run(
                () => Store.GetDefinitions(context.CurrentProjectSnapshot, symbol.Symbol),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[definition] definition lookup failed for '{documentUri}': {ex.GetType().Name}.");
            CompleteStage(targets, "faulted", "exception");
            CompleteRequest(request, "faulted", "exception");
            return null;
        }

        CompleteStage(targets, result is null || result.Targets.Count == 0 ? "none" : "found", null);

        if (cancellationToken.IsCancellationRequested)
        {
            CompleteRequest(request, "cancelled", "request_cancelled");
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[definition] discarded stale result for '{documentUri}'.");
            CompleteRequest(request, "stale_discarded", "document_version_stale");
            return null;
        }

        if (result is null || result.Targets.Count == 0)
        {
            CompleteRequest(request, "not_found", "symbol_lookup_miss");
            return null;
        }

        Location[]? locations = Map(result, request.OperationId, context.Version.Value);
        if (locations is null)
        {
            CompleteRequest(request, "not_found", "target_not_captured");
            return null;
        }

        EmitStage(ResponseCompletedEvent, "found", null, request.OperationId, context.Version.Value);
        CompleteRequest(request, "success", null);
        return locations;
    }

    private Location[]? Map(BackendDefinitionResult result, string operationId, long version)
    {
        IOperationScope mapping = EmitStageOperation(MappingStartedEvent, operationId, version);

        var indexes = new Dictionary<DocumentUri, LineIndex>();
        var seen = new HashSet<(string Path, int Start, int Length)>();
        var locations = new List<Location>(result.Targets.Count);
        bool rejected = false;

        foreach (BackendDefinitionTarget target in result.Targets)
        {
            if (!result.DocumentTexts.TryGetValue(target.Document, out string? text))
            {
                logger.Debug($"[definition] skipping target in '{target.Document}' without captured text.");
                EmitStage(MappingRejectedEvent, "rejected", "target_not_captured", operationId, version);
                rejected = true;
                continue;
            }

            TextSpan span = target.SelectionSpan;
            if (span.Start < 0 || span.Length < 0 || span.End > text.Length)
            {
                logger.Debug($"[definition] skipping target in '{target.Document}' with an out-of-range selection span.");
                EmitStage(MappingRejectedEvent, "rejected", "invalid_target_span", operationId, version);
                rejected = true;
                continue;
            }

            if (!indexes.TryGetValue(target.Document, out LineIndex? index))
            {
                index = new LineIndex(text);
                indexes[target.Document] = index;
            }

            if (!index.TryGetRange(span, out TextRange range))
            {
                logger.Debug($"[definition] skipping target in '{target.Document}' whose selection span cannot be mapped.");
                EmitStage(MappingRejectedEvent, "rejected", "invalid_target_span", operationId, version);
                rejected = true;
                continue;
            }

            if (!seen.Add((target.Document.LocalPath, span.Start, span.Length)))
            {
                EmitStage(MappingRejectedEvent, "rejected", "target_deduplicated", operationId, version);
                rejected = true;
                continue;
            }

            locations.Add(new Location
            {
                Uri = new Uri(target.Document.LocalPath),
                Range = new LspRange
                {
                    Start = new Position(range.Start.Line, range.Start.Character),
                    End = new Position(range.End.Line, range.End.Character),
                },
            });
        }

        string mappingOutcome = locations.Count == 0 ? "rejected" : rejected ? "partial" : "mapped";
        CompleteStage(mapping, mappingOutcome, null);

        return locations.Count == 0 ? null : [.. locations];
    }

    private IOperationScope EmitRequestStarted()
    {
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0))
            return NoopOperationScope.Instance;

        return _observer.BeginOperation(new OperationStart
        {
            Event = RequestStartedEvent,
            Stage = Stage,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
        });
    }

    private IOperationScope EmitStageOperation(string eventCode, string operationId, long version)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0))
            return NoopOperationScope.Instance;

        return _observer.BeginOperation(new OperationStart
        {
            Event = eventCode,
            Stage = Stage,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            ParentOperationId = operationId,
            DocumentVersion = version,
        });
    }

    private static void CompleteStage(IOperationScope scope, string outcome, string? reason)
    {
        if (ReferenceEquals(scope, NoopOperationScope.Instance))
            return;

        scope.Complete(outcome, reason);
    }

    private static void CompleteRequest(IOperationScope request, string outcome, string? reason)
    {
        if (ReferenceEquals(request, NoopOperationScope.Instance))
            return;

        request.Complete(outcome, reason);
    }

    /// <summary>
    /// Emits a terminal <c>request.completed</c> for branches that reject the
    /// request before a request span is opened (missing textDocument or a
    /// non-cvolo document), so those drops still carry an explicit outcome and
    /// reason (§19.1, §20.3).
    /// </summary>
    private void EmitRequestTerminal(string outcome, string reason)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0))
            return;

        _observer.Emit(new CompilerEvent
        {
            Event = "request.completed",
            Stage = Stage,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            Outcome = outcome,
            Reason = reason,
        });
    }

    private void EmitStage(
        string eventCode,
        string outcome,
        string? reason,
        string? operationId,
        long? version,
        IReadOnlyList<KeyValuePair<string, string>>? attributes = null)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0))
            return;

        _observer.Emit(new CompilerEvent
        {
            Event = eventCode,
            Stage = Stage,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            Outcome = outcome,
            Reason = reason,
            OperationId = operationId,
            DocumentVersion = version,
            Attributes = attributes,
        });
    }
}
