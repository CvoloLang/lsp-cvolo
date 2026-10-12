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
/// textDocument/documentSymbol handler. Returns the declaration outline of the
/// requested open document from its captured snapshot, either as a hierarchical
/// <c>DocumentSymbol[]</c> or, for clients without hierarchical support, a flat
/// <c>SymbolInformation[]</c> (§19, §20, §29.3). It performs only protocol
/// mapping and validation; no semantic lookup is repeated.
/// </summary>
internal sealed class DocumentSymbolHandler(ILspLogger logger, Func<DocumentStore> storeAccessor, Func<bool> hierarchicalAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod(Methods.TextDocumentDocumentSymbolName, UseSingleObjectParameterDeserialization = true)]
    public async Task<object?> DocumentSymbols(DocumentSymbolParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[documentSymbol] request without a textDocument; ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[documentSymbol] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[documentSymbol] '{documentUri}' is not open; no outline.");
            return null;
        }

        IReadOnlyList<BackendDocumentSymbol> symbols;
        try
        {
            symbols = await Task.Run(() => Store.GetDocumentSymbols(context), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[documentSymbol] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[documentSymbol] discarded stale result for '{documentUri}'.");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        int textLength = context.Document.Text.Length;

        if (hierarchicalAccessor())
        {
            var hierarchical = new List<DocumentSymbol>(symbols.Count);
            foreach (BackendDocumentSymbol symbol in symbols)
            {
                if (MapHierarchical(symbol, index, textLength) is { } mapped)
                {
                    hierarchical.Add(mapped);
                }
            }

            return hierarchical.ToArray();
        }

        var flat = new List<SymbolInformation>();
        foreach (BackendDocumentSymbol symbol in symbols)
        {
            MapFlat(symbol, index, textLength, context.Document.Uri, containerName: null, flat);
        }

        return flat.ToArray();
    }

    private static DocumentSymbol? MapHierarchical(BackendDocumentSymbol symbol, LineIndex index, int textLength)
    {
        // The LSP schema requires a non-empty name. A nameless symbol is omitted together with its
        // subtree rather than sent, because the client rejects the entire response over one (§19.5).
        if (string.IsNullOrWhiteSpace(symbol.Name))
        {
            return null;
        }

        // A node with an invalid range or selection span and its whole subtree are
        // omitted; ranges are never clamped or repaired (§19.5).
        if (!TryMapRange(symbol.Range, index, textLength, out LspRange range)
            || !TryMapRange(symbol.SelectionSpan, index, textLength, out LspRange selection))
        {
            return null;
        }

        var children = new List<DocumentSymbol>(symbol.Children.Count);
        foreach (BackendDocumentSymbol child in symbol.Children)
        {
            if (MapHierarchical(child, index, textLength) is { } mappedChild)
            {
                children.Add(mappedChild);
            }
        }

        return new DocumentSymbol
        {
            Name = symbol.Name,
            Detail = symbol.Detail,
            Kind = MapSymbolKind(symbol.Kind),
            Range = range,
            SelectionRange = selection,
            Children = children.ToArray(),
        };
    }

    private static void MapFlat(
        BackendDocumentSymbol symbol,
        LineIndex index,
        int textLength,
        DocumentUri documentUri,
        string? containerName,
        List<SymbolInformation> output)
    {
        if (!string.IsNullOrWhiteSpace(symbol.Name)
            && TryMapRange(symbol.SelectionSpan, index, textLength, out LspRange selection))
        {
            output.Add(new SymbolInformation
            {
                Name = symbol.Name,
                Kind = MapSymbolKind(symbol.Kind),
                Location = new Location
                {
                    Uri = new Uri(documentUri.LocalPath),
                    Range = selection,
                },
                ContainerName = containerName,
            });
        }

        foreach (BackendDocumentSymbol child in symbol.Children)
        {
            MapFlat(child, index, textLength, documentUri, symbol.Name, output);
        }
    }

    private static bool TryMapRange(TextSpan span, LineIndex index, int textLength, out LspRange range)
    {
        if (SpanMapper.TryMapRange(index, textLength, span, out LspRange mapped))
        {
            range = mapped;
            return true;
        }

        range = null!;
        return false;
    }

    private static SymbolKind MapSymbolKind(BackendSymbolKind kind)
    {
        return kind switch
        {
            BackendSymbolKind.Namespace => SymbolKind.Namespace,
            BackendSymbolKind.Module => SymbolKind.Module,
            BackendSymbolKind.Struct => SymbolKind.Struct,
            BackendSymbolKind.Union => SymbolKind.Struct,
            BackendSymbolKind.Enum => SymbolKind.Enum,
            BackendSymbolKind.EnumMember => SymbolKind.EnumMember,
            BackendSymbolKind.Interface => SymbolKind.Interface,
            BackendSymbolKind.Protocol => SymbolKind.Interface,
            BackendSymbolKind.Delegate => SymbolKind.Class,
            BackendSymbolKind.TypeAlias => SymbolKind.Class,
            BackendSymbolKind.TypeParameter => SymbolKind.TypeParameter,
            BackendSymbolKind.Function => SymbolKind.Function,
            BackendSymbolKind.Method => SymbolKind.Method,
            BackendSymbolKind.ExtensionMethod => SymbolKind.Method,
            BackendSymbolKind.AssociatedFunction => SymbolKind.Method,
            BackendSymbolKind.Constructor => SymbolKind.Constructor,
            BackendSymbolKind.Destructor => SymbolKind.Method,
            BackendSymbolKind.Field => SymbolKind.Field,
            BackendSymbolKind.Property => SymbolKind.Property,
            BackendSymbolKind.Parameter => SymbolKind.Variable,
            BackendSymbolKind.Local => SymbolKind.Variable,
            BackendSymbolKind.Global => SymbolKind.Variable,
            BackendSymbolKind.Constant => SymbolKind.Constant,
            BackendSymbolKind.Operator => SymbolKind.Operator,
            BackendSymbolKind.OtherType => SymbolKind.Class,
            _ => SymbolKind.Object,
        };
    }
}
