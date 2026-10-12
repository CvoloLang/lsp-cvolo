using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>Project-semantic textDocument/references handler.</summary>
internal sealed class ReferencesHandler(ILspLogger logger, Func<DocumentStore> storeAccessor)
{
    private DocumentStore? _store;
    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/references", UseSingleObjectParameterDeserialization = true)]
    public async Task<Location[]?> References(ReferenceParamsPayload? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument || parameters.Position is not { } position)
            return null;

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
            return null;

        var sourceIndex = new LineIndex(context.Document.Text);
        if (!sourceIndex.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
            return null;

        BackendReferenceResult? result;
        try
        {
            result = await Task.Run(() =>
            {
                BackendSymbolInfo? symbol = Store.GetSymbolAtPosition(context, offset);
                return symbol is null
                    ? null
                    : Store.GetReferences(context.CurrentProjectSnapshot, symbol.Symbol, parameters.Context?.IncludeDeclaration == true);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[references] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Location[]? mapped = result is null ? null : Map(result);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[references] discarded stale result for '{documentUri}'.");
            return null;
        }

        return mapped;
    }

    private Location[] Map(BackendReferenceResult result)
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
