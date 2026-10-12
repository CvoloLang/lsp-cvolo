using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using System.Diagnostics;
using System.Text;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/completion and completionItem/resolve handler. Validates the wire request,
/// captures a coherent semantic context, maps the UTF-16 line/character position to an absolute
/// offset over the captured text, queries the backend from that one immutable snapshot, suppresses
/// stale results, and maps the backend-neutral result into an LSP completion list. Rich items are
/// staged and atomically committed to the bounded resolve store only after the final freshness
/// gate, and resolve fills only the negotiated effective fields (§23-§27, §34). It owns no Cvolo
/// semantics.
/// </summary>
internal sealed class CompletionHandler(
    ILspLogger logger,
    Func<DocumentStore> storeAccessor,
    Func<bool> snippetSupport,
    Func<string[]> resolveProperties,
    Func<bool> documentationMarkdown,
    Func<CompletionResolveStore> resolveStoreAccessor)
{
    private const int CompletionQuietPeriodMs = 120;
    private const int CompletionMaxAutomaticTextLength = 100_000;

    private DocumentStore? _store;
    private CompletionResolveStore? _resolveStore;

    // Resolved on first use so the session-scoped store is created from the
    // workspace folders/root established by initialize, not at registration time.
    private DocumentStore Store => _store ??= storeAccessor();

    private CompletionResolveStore ResolveStore => _resolveStore ??= resolveStoreAccessor();

    [JsonRpcMethod(Methods.TextDocumentCompletionName, UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionList?> Completion(CompletionParams? parameters, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[completion] request without a textDocument; ignored.");
            return null;
        }

        Position position = parameters.Position;
        logger.Debug($"[completion] request {textDocument.Uri} {position.Line}:{position.Character}");

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri))
        {
            logger.Debug($"[completion] non-file document '{textDocument.Uri}' ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[completion] non-cvolo document '{documentUri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[completion] '{documentUri}' is not open; no completion.");
            return null;
        }

        logger.Debug($"[completion] captured session={context.SessionId} version={context.Version}");
        LogTiming("capture", stopwatch, documentUri);

        if (context.Document.Text.Length > CompletionMaxAutomaticTextLength && !IsExplicitInvocation(parameters))
        {
            logger.Warning($"[perf] completion skipped for large document auto-trigger: uri='{documentUri}', textLength={context.Document.Text.Length}, limit={CompletionMaxAutomaticTextLength}, triggerKind={parameters.Context?.TriggerKind}");
            return EmptyCompletionList();
        }

        // The request position is mapped against the exact captured text so the
        // replacement span is always valid over the text the offset refers to.
        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            logger.Debug($"[completion] invalid request position {position.Line}:{position.Character} for '{documentUri}'.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await Task.Delay(CompletionQuietPeriodMs, cancellationToken).ConfigureAwait(false);
        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[completion] skipped stale request after quiet period for '{documentUri}'.");
            return null;
        }

        BackendCompletionResult? result;
        try
        {
            // Run the (synchronous) semantic query off the JSON-RPC receive loop so
            // a slow query cannot stall other requests or cancellation (§19, §22).
            result = await Store.RunInteractiveQueryAsync(
                context,
                "completion",
                () => Store.GetCompletions(context, offset),
                cancellationToken).ConfigureAwait(false);
            LogTiming("backend", stopwatch, documentUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[completion] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (result is null)
        {
            logger.Debug($"[completion] skipped stale request for '{documentUri}'.");
            return null;
        }

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[completion] discarded stale result for '{documentUri}'.");
            return null;
        }

        logger.Debug($"[completion] backend returned {result.Items.Count} candidate(s) for '{documentUri}'.");

        MappedCompletion mapped = Map(result, index, offset, context.Document.Text.Length, context);
        if (mapped.List is null)
            return null;

        // Staged batch commit: only now, after the final freshness gate, are resolve entries
        // atomically committed and their opaque data tokens attached (§27.3, §34). Everything
        // between this gate and the response is commit + retention, never semantic work.
        HashSet<string> attached = ResolveStore.Commit(mapped.Staged);
        foreach (StagedResolveItem stagedItem in mapped.Staged)
        {
            if (attached.Contains(stagedItem.Token))
                mapped.List.Items[stagedItem.ItemIndex].Data = stagedItem.Token;
        }

        LogTiming("total", stopwatch, documentUri);
        return mapped.List;
    }

    private void LogTiming(string phase, Stopwatch stopwatch, DocumentUri uri, int thresholdMs = 50)
    {
        if (stopwatch.ElapsedMilliseconds >= thresholdMs)
            logger.Warning($"[perf] completion {phase} took {stopwatch.ElapsedMilliseconds} ms: uri='{uri}'");
        else
            logger.Debug($"[perf] completion {phase} took {stopwatch.ElapsedMilliseconds} ms: uri='{uri}'");
    }

    private static bool IsExplicitInvocation(CompletionParams parameters)
    {
        return parameters.Context?.TriggerKind is CompletionTriggerKind.Invoked;
    }

    private static CompletionList EmptyCompletionList() => new()
    {
        IsIncomplete = false,
        Items = [],
    };

    [JsonRpcMethod("completionItem/resolve", UseSingleObjectParameterDeserialization = true)]
    public async Task<CompletionItem?> Resolve(CompletionItem? item, CancellationToken cancellationToken)
    {
        // Unknown or malformed data tokens are left untouched: no reconstruction, no failure (§27.4).
        if (item is null || item.Data is not string token)
            return item;

        cancellationToken.ThrowIfCancellationRequested();
        if (!ResolveStore.TryGet(token, out CompletionResolveEntry entry))
            return item;

        BackendCompletionResolvableFields sessionEffective = entry.EffectiveResolvableFields & ClientResolvableFields();
        if (sessionEffective == BackendCompletionResolvableFields.None)
            return item;

        // Early freshness: an entry whose document or project snapshot has moved on is evicted.
        if (!Store.IsCurrent(entry.Context))
        {
            ResolveStore.Evict(token);
            return item;
        }

        BackendCompletionResolvedInfo? resolved;
        try
        {
            resolved = await Task.Run(() => Store.ResolveCompletion(entry.Context, entry.Handle), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[completionItem/resolve] backend failure: {ex.Message}");
            return item;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Final freshness: never enrich an item whose context moved on while we worked.
        if (!Store.IsCurrent(entry.Context))
        {
            ResolveStore.Evict(token);
            return item;
        }

        if (resolved is null)
            return item;

        // Only the stored effective fields may change; label, text edit, insert format and range
        // are immutable (§25, §33).
        if (sessionEffective.HasFlag(BackendCompletionResolvableFields.Detail) && resolved.Detail is not null)
            item.Detail = resolved.Detail;
        if (sessionEffective.HasFlag(BackendCompletionResolvableFields.Documentation) && resolved.Documentation is not null)
        {
            item.Documentation = new MarkupContent
            {
                Kind = documentationMarkdown() ? MarkupKind.Markdown : MarkupKind.PlainText,
                Value = resolved.Documentation,
            };
        }

        return item;
    }

    private sealed record MappedCompletion(CompletionList? List, List<StagedResolveItem> Staged);

    private MappedCompletion Map(BackendCompletionResult result, LineIndex index, int offset, int textLength, SemanticRequestContext context)
    {
        TextSpan span = result.ReplacementSpan;

        // Never clamp or fabricate a replacement range: a span that is outside the
        // captured text or does not contain the request offset is discarded (§17).
        if (span.Start < 0 || span.Length < 0 || span.End > textLength)
        {
            logger.Debug("[completion] discarding result with an out-of-range replacement span.");
            return new MappedCompletion(null, []);
        }

        if (offset < span.Start || offset > span.End)
        {
            logger.Debug("[completion] discarding result whose replacement span does not contain the request offset.");
            return new MappedCompletion(null, []);
        }

        if (!index.TryGetRange(span, out TextRange range) || range.Start.Line != range.End.Line)
        {
            logger.Debug("[completion] discarding result whose replacement span is not a single-line range.");
            return new MappedCompletion(null, []);
        }

        var editRange = new LspRange
        {
            Start = new Position(range.Start.Line, range.Start.Character),
            End = new Position(range.End.Line, range.End.Character),
        };

        BackendCompletionResolvableFields clientFields = ClientResolvableFields();
        var staged = new List<StagedResolveItem>();
        var items = new CompletionItem[result.Items.Count];
        for (var i = 0; i < result.Items.Count; i++)
        {
            BackendCompletionItem item = result.Items[i];

            string insertText;
            InsertTextFormat insertFormat;
            if (snippetSupport() && item.InsertionPlan is not null && EncodeSnippet(item.InsertionPlan) is { } encodedSnippet)
            {
                insertText = encodedSnippet;
                insertFormat = InsertTextFormat.Snippet;
            }
            else
            {
                insertText = item.PlainInsertText;
                insertFormat = InsertTextFormat.Plaintext;
            }

            items[i] = new CompletionItem
            {
                Label = item.Label,
                Kind = MapKind(item.Kind),
                Detail = item.Detail,
                InsertTextFormat = insertFormat,
                TextEdit = new TextEdit
                {
                    Range = editRange,
                    NewText = insertText,
                },
            };

            BackendCompletionResolvableFields effective = EffectiveResolvableFields(item, clientFields);
            if (effective != BackendCompletionResolvableFields.None)
            {
                staged.Add(new StagedResolveItem(
                    Guid.NewGuid().ToString("N"),
                    i,
                    new CompletionResolveEntry(context, item.ResolveHandle!, effective)));
            }
        }

        return new MappedCompletion(
            new CompletionList
            {
                IsIncomplete = false,
                Items = items,
            },
            staged);
    }

    /// <summary>
    /// The intersection of the backend's resolvable mask and the client's negotiated resolve
    /// properties, minus fields already populated by the initial response (§25, §37). A null
    /// <c>resolveSupport.properties</c> means the historical default {detail, documentation}.
    /// </summary>
    private BackendCompletionResolvableFields EffectiveResolvableFields(BackendCompletionItem item, BackendCompletionResolvableFields clientFields)
    {
        if (item.ResolveHandle is null)
            return BackendCompletionResolvableFields.None;

        BackendCompletionResolvableFields mask = item.ResolvableFields & clientFields;
        if (item.Detail is not null)
            mask &= ~BackendCompletionResolvableFields.Detail;

        return mask;
    }

    private BackendCompletionResolvableFields ClientResolvableFields()
    {
        string[]? properties = resolveProperties();
        var result = BackendCompletionResolvableFields.None;
        if (properties is null)
        {
            // No resolveSupport advertised: the LSP 3.17 historical default applies.
            return BackendCompletionResolvableFields.Detail | BackendCompletionResolvableFields.Documentation;
        }

        foreach (string property in properties)
        {
            if (string.Equals(property, "detail", StringComparison.Ordinal))
                result |= BackendCompletionResolvableFields.Detail;
            else if (string.Equals(property, "documentation", StringComparison.Ordinal))
                result |= BackendCompletionResolvableFields.Documentation;
        }

        return result;
    }

    /// <summary>
    /// Encodes a compiler-owned insertion plan into an LSP snippet with tab stops numbered 1..N in
    /// segment order and an optional final cursor <c>$0</c>. Returns null (calling the plain-text
    /// fallback) on any structural failure, never a partial or invented encoding (§23, §24).
    /// </summary>
    private static string? EncodeSnippet(BackendCompletionInsertionPlan plan)
    {
        if (plan is null || plan.SnippetSegments is null)
            return null;

        var builder = new StringBuilder();
        var tabStop = 0;
        var sawFinalCursor = false;
        foreach (BackendCompletionInsertSegment segment in plan.SnippetSegments)
        {
            switch (segment)
            {
                case BackendCompletionLiteral literal:
                    if (literal.Text is null)
                        return null;
                    AppendSnippetEscapedLiteral(builder, literal.Text);
                    break;
                case BackendCompletionPlaceholder placeholder:
                    if (placeholder.DefaultText is null)
                        return null;
                    builder.Append("${").Append(++tabStop).Append(':');
                    AppendSnippetEscaped(builder, placeholder.DefaultText);
                    builder.Append('}');
                    break;
                case BackendCompletionFinalCursor:
                    if (sawFinalCursor)
                        return null;
                    sawFinalCursor = true;
                    builder.Append("$0");
                    break;
                default:
                    return null;
            }
        }

        return builder.ToString();
    }

    private static void AppendSnippetEscaped(StringBuilder builder, string text)
    {
        foreach (char character in text)
        {
            switch (character)
            {
                case '$':
                    builder.Append("$$");
                    break;
                case '}':
                    builder.Append(@"\}");
                    break;
                case '\\':
                    builder.Append(@"\\");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }
    }

    private static void AppendSnippetEscapedLiteral(StringBuilder builder, string text)
    {
        foreach (char character in text)
        {
            switch (character)
            {
                case '$':
                    builder.Append("$$");
                    break;
                case '\\':
                    builder.Append(@"\\");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }
    }

    private static CompletionItemKind MapKind(BackendCompletionKind kind)
    {
        return kind switch
        {
            BackendCompletionKind.Keyword => CompletionItemKind.Keyword,
            BackendCompletionKind.Local => CompletionItemKind.Variable,
            BackendCompletionKind.Parameter => CompletionItemKind.Variable,
            BackendCompletionKind.Global => CompletionItemKind.Variable,
            BackendCompletionKind.Constant => CompletionItemKind.Constant,
            BackendCompletionKind.Function => CompletionItemKind.Function,
            BackendCompletionKind.Method => CompletionItemKind.Method,
            BackendCompletionKind.ExtensionMethod => CompletionItemKind.Method,
            BackendCompletionKind.Constructor => CompletionItemKind.Constructor,
            BackendCompletionKind.Field => CompletionItemKind.Field,
            BackendCompletionKind.StructField => CompletionItemKind.Field,
            BackendCompletionKind.Property => CompletionItemKind.Property,
            BackendCompletionKind.Struct => CompletionItemKind.Struct,
            BackendCompletionKind.Union => CompletionItemKind.Struct,
            BackendCompletionKind.Enum => CompletionItemKind.Enum,
            BackendCompletionKind.EnumMember => CompletionItemKind.EnumMember,
            BackendCompletionKind.EnumMetadata => CompletionItemKind.Property,
            BackendCompletionKind.Interface => CompletionItemKind.Interface,
            BackendCompletionKind.Protocol => CompletionItemKind.Interface,
            BackendCompletionKind.Module => CompletionItemKind.Module,
            BackendCompletionKind.Namespace => CompletionItemKind.Module,
            BackendCompletionKind.TypeAlias => CompletionItemKind.Class,
            BackendCompletionKind.Type => CompletionItemKind.Class,
            BackendCompletionKind.OtherType => CompletionItemKind.Class,
            BackendCompletionKind.UnionVariant => CompletionItemKind.Field,
            BackendCompletionKind.ArrayLength => CompletionItemKind.Property,
            _ => CompletionItemKind.Text,
        };
    }
}
