using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Cvolo.LanguageServer.Observability;
using global::Cvolo.Observability.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>Project-semantic textDocument/references handler.</summary>
internal sealed class ReferencesHandler(ILspLogger logger, Func<DocumentStore> storeAccessor, ICompilerObserver? observer = null)
{
    private readonly ICompilerObserver _observer = observer ?? NoopCompilerObserver.Instance;
    private DocumentStore? _store;
    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/references", UseSingleObjectParameterDeserialization = true)]
    public async Task<Location[]?> References(ReferenceParamsPayload? parameters, CancellationToken cancellationToken)
    {
        using var trace = FeatureTrace.Start(_observer, "references", cancellationToken);

        if (parameters?.TextDocument is not { } textDocument || parameters.Position is not { } position)
        {
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
            trace.Complete("not_found", "document_not_open");
            return null;
        }

        var sourceIndex = new LineIndex(context.Document.Text);
        if (!sourceIndex.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            trace.Complete("not_found", "invalid_position");
            return null;
        }

        BackendReferenceResult? result;
        IOperationScope backend = trace.Backend();
        try
        {
            result = await Task.Run(() =>
            {
                BackendSymbolInfo? symbol = Store.GetSymbolAtPosition(context, offset);
                return symbol is null
                    ? null
                    : Store.GetReferences(context.CurrentProjectSnapshot, symbol.Symbol, parameters.Context?.IncludeDeclaration == true);
            }, cancellationToken).ConfigureAwait(false);
            trace.CompleteBackend(backend, "completed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[references] backend failure for '{documentUri}': {ex.GetType().Name}");
            trace.CompleteBackend(backend, "faulted", "exception");
            trace.Complete("faulted", "exception");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Location[]? mapped = result is null ? null : Map(result);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[references] discarded stale result for '{documentUri}'.");
            trace.Freshness(fresh: false);
            trace.Complete("stale_discarded", "document_version_stale");
            return null;
        }

        trace.Freshness(fresh: true);
        trace.Complete(mapped is null || mapped.Length == 0 ? "not_found" : "success",
            mapped is null || mapped.Length == 0 ? "symbol_lookup_miss" : null);
        return mapped;
    }

    internal static Location[] Map(BackendReferenceResult result)
    {
        var indexes = new Dictionary<DocumentUri, LineIndex>(DocumentUriPathComparer.Instance);
        var seen = new HashSet<(string Path, int Start, int Length)>(ReferenceKeyComparer.Instance);
        var locations = new List<(DocumentUri Uri, TextSpan Span, Location Location)>();

        foreach (BackendReferenceLocation target in result.Locations)
        {
            if (!result.DocumentTexts.TryGetValue(target.Document, out string? text))
                continue;
            if (!TryMapSpan(text, target.Span, indexes, target.Document, out LspRange? range))
                continue;
            if (!seen.Add((target.Document.LocalPath, target.Span.Start, target.Span.Length)))
                continue;

            locations.Add((target.Document, target.Span, new Location
            {
                Uri = new Uri(target.Document.LocalPath),
                Range = range,
            }));
        }

        return [.. locations
            .OrderBy(x => x.Uri.LocalPath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ThenBy(x => x.Span.Start)
            .ThenBy(x => x.Span.End)
            .Select(x => x.Location)];
    }

    private static bool TryMapSpan(string text, TextSpan span, Dictionary<DocumentUri, LineIndex> indexes, DocumentUri uri, out LspRange range)
    {
        if (span.Start < 0 || span.Length <= 0)
        {
            range = null!;
            return false;
        }

        if (!indexes.TryGetValue(uri, out LineIndex? index))
        {
            index = new LineIndex(text);
            indexes[uri] = index;
        }

        if (!SpanMapper.TryMapSingleLineRange(text, index, text.Length, span, out LspRange mapped))
        {
            range = null!;
            return false;
        }

        range = mapped;
        return true;
    }

    internal static bool IntersectsLineTerminator(string text, TextSpan span) => SpanMapper.IntersectsLineTerminator(text, span);

    private sealed class ReferenceKeyComparer : IEqualityComparer<(string Path, int Start, int Length)>
    {
        public static ReferenceKeyComparer Instance { get; } = new();
        private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        public bool Equals((string Path, int Start, int Length) x, (string Path, int Start, int Length) y)
            => Paths.Equals(x.Path, y.Path) && x.Start == y.Start && x.Length == y.Length;
        public int GetHashCode((string Path, int Start, int Length) obj)
            => HashCode.Combine(Paths.GetHashCode(obj.Path), obj.Start, obj.Length);
    }
}
