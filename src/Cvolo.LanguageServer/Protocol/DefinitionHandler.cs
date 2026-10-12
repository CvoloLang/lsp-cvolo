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
/// textDocument/definition handler. Resolves the symbol at the request position
/// and its source declarations from the same immutable snapshot, then maps each
/// target's selection span against that target document's captured text — even
/// when the target is a closed project document. Malformed targets are skipped,
/// never clamped (§17, §18, §29.2).
/// </summary>
internal sealed class DefinitionHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod(Methods.TextDocumentDefinitionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Location[]?> Definition(TextDocumentPositionParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[definition] request without a textDocument; ignored.");
            return null;
        }

        Position position = parameters.Position;

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[definition] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[definition] '{documentUri}' is not open; no definition.");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            logger.Debug($"[definition] invalid request position {position.Line}:{position.Character} for '{documentUri}'.");
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
                        : Store.GetDefinitions(context.CurrentProjectSnapshot, symbol.Symbol);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[definition] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[definition] discarded stale result for '{documentUri}'.");
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
                logger.Debug($"[definition] skipping target in '{target.Document}' without captured text.");
                continue;
            }

            TextSpan span = target.SelectionSpan;
            if (span.Start < 0 || span.Length < 0 || span.End > text.Length)
            {
                logger.Debug($"[definition] skipping target in '{target.Document}' with an out-of-range selection span.");
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
