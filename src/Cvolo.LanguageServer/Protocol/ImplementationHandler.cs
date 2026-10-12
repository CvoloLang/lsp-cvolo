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
/// textDocument/implementation handler. On an interface it answers with the extension blocks that
/// declare conformance, preferring the conformance site over the bare type declaration; on an
/// interface member, the exact implementing members; on a protocol it answers with every concrete
/// project type the compiler's structural conformance accepts; on a protocol member, the matching
/// methods. Conformance is decided entirely by the compiler; the protocol layer never matches method
/// names or spellings itself. Targets in closed documents are mapped against their captured text so
/// they still navigate (§6-§11).
/// </summary>
internal sealed class ImplementationHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod(Methods.TextDocumentImplementationName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Location[]?> Implementation(TextDocumentPositionParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[implementation] request without a textDocument; ignored.");
            return null;
        }

        Position position = parameters.Position;

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[implementation] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[implementation] '{documentUri}' is not open; no target.");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            logger.Debug($"[implementation] invalid request position {position.Line}:{position.Character} for '{documentUri}'.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        BackendDefinitionResult? result;
        try
        {
            result = await Task.Run(
                () =>
                {
                    BackendSymbolInfo? symbol = Store.GetSymbolAtPosition(context, offset);
                    return symbol is null
                        ? null
                        : Store.GetImplementations(context.CurrentProjectSnapshot, symbol.Symbol);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[implementation] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[implementation] discarded stale result for '{documentUri}'.");
            return null;
        }

        return result is null ? null : Map(result);
    }

    private Location[]? Map(BackendDefinitionResult result)
    {
        if (result.Targets.Count == 0)
        {
            return null;
        }

        var indexes = new Dictionary<DocumentUri, LineIndex>();
        var seen = new HashSet<(string Path, int Start, int Length)>();
        var locations = new List<Location>(result.Targets.Count);

        foreach (BackendDefinitionTarget target in result.Targets)
        {
            if (!result.DocumentTexts.TryGetValue(target.Document, out string? text))
            {
                logger.Debug($"[implementation] skipping target in '{target.Document}' without captured text.");
                continue;
            }

            TextSpan span = target.SelectionSpan;
            if (span.Start < 0 || span.Length < 0 || span.End > text.Length)
            {
                logger.Debug($"[implementation] skipping target in '{target.Document}' with an out-of-range selection span.");
                continue;
            }

            if (!indexes.TryGetValue(target.Document, out LineIndex? index))
            {
                index = new LineIndex(text);
                indexes[target.Document] = index;
            }

            if (!index.TryGetRange(span, out TextRange range))
            {
                logger.Debug($"[implementation] skipping target in '{target.Document}' whose selection span cannot be mapped.");
                continue;
            }

            if (!seen.Add((target.Document.LocalPath, span.Start, span.Length)))
            {
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

        return locations.Count == 0 ? null : [.. locations];
    }
}
