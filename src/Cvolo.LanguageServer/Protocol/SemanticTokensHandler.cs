using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Cvolo.LanguageServer.Observability;
using global::Cvolo.Observability.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using System.Diagnostics;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/semanticTokens/full handler. Captures a coherent snapshot, asks the backend for
/// compiler-classified tokens, validates/normalizes/orders them, maps them into the negotiated
/// session legend, and relative-encodes them (§16, §18–§20).
/// </summary>
internal sealed class SemanticTokensHandler(
    ILspLogger logger,
    Func<DocumentStore> storeAccessor,
    Func<string[]> tokenTypesAccessor,
    Func<string[]> tokenModifiersAccessor,
    Func<int> maxInteractiveTextLengthAccessor,
    ICompilerObserver? observer = null)
{
    private readonly ICompilerObserver _observer = observer ?? NoopCompilerObserver.Instance;
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod(Methods.TextDocumentSemanticTokensFullName, UseSingleObjectParameterDeserialization = true)]
    public async Task<SemanticTokensResponse?> SemanticTokensFull(SemanticTokensParams? parameters, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var trace = FeatureTrace.Start(_observer, "semanticTokens", cancellationToken);

        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[semanticTokens] request without a textDocument; ignored.");
            trace.Complete("not_found", "document_not_open");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[semanticTokens] non-cvolo document '{textDocument.Uri}' ignored.");
            trace.Complete("unsupported", "unsupported_uri");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[semanticTokens] '{documentUri}' is not open; no tokens.");
            trace.Complete("not_found", "document_not_open");
            return null;
        }
        LogTiming("capture", stopwatch, documentUri);

        int maxInteractiveTextLength = maxInteractiveTextLengthAccessor();
        if (context.Document.Text.Length > maxInteractiveTextLength)
        {
            // An oversized document is a resource denial, not proof that a nonempty document has
            // zero semantic tokens: return a typed non-success, never a successful empty array.
            logger.Info($"[perf] semanticTokens refused for large document: uri='{documentUri}', textLength={context.Document.Text.Length}, limit={maxInteractiveTextLength}");
            trace.Complete("unavailable", "resource_limit");
            throw TypedError(
                "Semantic tokens are unavailable for this document because it exceeds the interactive resource budget.",
                CvoloErrorData.BackendUnavailable,
                retryable: true,
                reason: CvoloErrorData.ResourceLimit);
        }

        BackendSemanticTokenResult? result;
        IOperationScope backend = trace.Backend();
        try
        {
            result = await Store.RunInteractiveQueryAsync(
                context,
                "semanticTokens",
                () => Store.GetSemanticTokens(context),
                cancellationToken).ConfigureAwait(false);
            LogTiming("backend", stopwatch, documentUri);
            trace.CompleteBackend(backend, "completed");
        }
        // A typed ContentModified (or any other LocalRpcException) already carries the protocol
        // error the client must see; never re-wrap it as a backend RequestFailed.
        catch (Exception ex) when (ex is not OperationCanceledException and not LocalRpcException)
        {
            // Never publish the exception text: it may embed source. The type is enough to trace.
            logger.Warning($"[semanticTokens] backend failure for '{documentUri}': {ex.GetType().Name}");
            trace.CompleteBackend(backend, "faulted", "exception");
            trace.Complete("faulted", "exception");
            throw TypedError(
                "The compiler backend failed while computing semantic tokens.",
                CvoloErrorData.BackendUnavailable,
                retryable: true,
                reason: CvoloErrorData.BackendFailure);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (result is null)
        {
            logger.Debug($"[semanticTokens] superseded request for '{documentUri}'; no tokens.");
            trace.Complete("stale_discarded", "document_version_stale");
            throw ContentModified(
                "The semantic token request was superseded by newer document input.");
        }

        int[] data = Encode(result, context.Document.Text, tokenTypesAccessor(), tokenModifiersAccessor());
        LogTiming("encode", stopwatch, documentUri);

        // Encoding can be non-trivial for large documents. Perform the final freshness
        // gate after normalization/encoding so no result from an old project generation
        // can escape as current semantic highlighting.
        cancellationToken.ThrowIfCancellationRequested();
        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[semanticTokens] result no longer current for '{documentUri}'; discarded.");
            trace.Freshness(fresh: false);
            trace.Complete("stale_discarded", "document_version_stale");
            throw ContentModified(
                "The semantic token result is no longer valid for the current document.");
        }

        trace.Freshness(fresh: true);
        logger.Info($"[semanticTokens] {documentUri}: {data.Length / 5} token(s).");
        LogTiming("total", stopwatch, documentUri);
        LogTokenBreakdown(documentUri, context.Document.Version, result);
        trace.Complete("success");
        return new SemanticTokensResponse(data);
    }

    private readonly Dictionary<DocumentUri, DocumentVersion> _lastTokenBreakdownVersion = new();

    /// <summary>
    /// Emits one debug line per document version summarizing which semantic token kinds the
    /// compiler produced, so a missing colour can be traced to the emitted kinds rather than
    /// guessed at. The version gate keeps it to a single line per edit, not per request.
    /// </summary>
    private void LogTokenBreakdown(DocumentUri uri, DocumentVersion version, BackendSemanticTokenResult result)
    {
        lock (_lastTokenBreakdownVersion)
        {
            if (_lastTokenBreakdownVersion.TryGetValue(uri, out var last) && last == version)
            {
                return;
            }

            _lastTokenBreakdownVersion[uri] = version;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (BackendSemanticToken token in result.Tokens)
        {
            var name = Fallbacks(token.Kind) is [var first, ..] ? first : token.Kind.ToString();
            counts[name] = counts.TryGetValue(name, out var existing) ? existing + 1 : 1;
        }

        var summary = string.Join(", ", counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
        logger.Debug($"[perf] semantic tokens breakdown: uri='{uri}', version={version}, total={result.Tokens.Count} [{summary}]");
    }

    private void LogTiming(string phase, Stopwatch stopwatch, DocumentUri uri, int thresholdMs = 50)
    {
        if (stopwatch.ElapsedMilliseconds >= thresholdMs)
            logger.Debug($"[perf] semanticTokens {phase} took {stopwatch.ElapsedMilliseconds} ms: uri='{uri}'");
        else
            logger.Debug($"[perf] semanticTokens {phase} took {stopwatch.ElapsedMilliseconds} ms: uri='{uri}'");
    }

    private static LocalRpcException TypedError(string message, string cvoloKind, bool retryable, string? reason = null)
        => new(message)
        {
            ErrorCode = ProtocolErrorCodes.RequestFailed,
            ErrorData = new CvoloErrorData(cvoloKind, retryable, reason: reason),
        };

    /// <summary>
    /// A stale document or project generation is not a backend failure: the client's own edit
    /// invalidated the answer mid-flight. LSP reserves <see cref="ProtocolErrorCodes.ContentModified"/>
    /// for exactly this, and language clients treat it (and RequestCancelled) as silent — no error
    /// notification. Reporting it as RequestFailed made ordinary typing surface a user-visible
    /// "Request … failed" message. The freshness checks are unchanged; only the error class differs.
    /// </summary>
    private static LocalRpcException ContentModified(string message)
        => new(message)
        {
            ErrorCode = ProtocolErrorCodes.ContentModified,
            ErrorData = new CvoloErrorData(CvoloErrorData.SupersededByQueuedInput, retryable: true),
        };

    private int[] Encode(BackendSemanticTokenResult result, string text, string[] tokenTypes, string[] tokenModifiers)
    {
        var index = new LineIndex(text);
        var accepted = new List<(int Line, int Character, int Length, int Type, int Modifiers)>();

        foreach (BackendSemanticToken token in result.Tokens)
        {
            TextSpan span = token.Span;

            // Reject malformed spans rather than repair them (§20).
            if (span.Start < 0 || span.Length <= 0 || span.End > text.Length)
                continue;

            if (IntersectsLineTerminator(text, span))
                continue;

            int typeIndex = MapKind(token.Kind, tokenTypes);
            if (typeIndex < 0)
                continue;

            if (!index.TryGetRange(span, out TextRange range) || range.Start.Line != range.End.Line)
                continue;

            accepted.Add((
                range.Start.Line,
                range.Start.Character,
                span.Length,
                typeIndex,
                MapModifiers(token.Modifiers, tokenModifiers)));
        }

        // Deterministic source order, collapsing exact duplicates and dropping overlaps (§20.1–20.3).
        accepted.Sort(static (a, b) =>
        {
            int byLine = a.Line.CompareTo(b.Line);
            if (byLine != 0) return byLine;
            int byChar = a.Character.CompareTo(b.Character);
            if (byChar != 0) return byChar;
            return a.Length.CompareTo(b.Length);
        });

        var data = new List<int>(accepted.Count * 5);
        var previousLine = 0;
        var previousCharacter = 0;
        var hasPrevious = false;
        var previousEnd = -1;

        foreach (var token in accepted)
        {
            int start = LineStartOffset(index, text, token.Line, token.Character);
            if (hasPrevious && start < previousEnd)
                continue; // overlap

            if (hasPrevious && token.Line == previousLine && token.Character == previousCharacter
                && token.Length == data[^3] && token.Type == data[^2] && token.Modifiers == data[^1])
                continue; // exact duplicate

            int deltaLine = hasPrevious ? token.Line - previousLine : token.Line;
            int deltaStart = hasPrevious && token.Line == previousLine ? token.Character - previousCharacter : token.Character;

            data.Add(deltaLine);
            data.Add(deltaStart);
            data.Add(token.Length);
            data.Add(token.Type);
            data.Add(token.Modifiers);

            previousLine = token.Line;
            previousCharacter = token.Character;
            previousEnd = start + token.Length;
            hasPrevious = true;
        }

        return [.. data];
    }

    private static int LineStartOffset(LineIndex index, string text, int line, int character)
    {
        // Recompute the absolute start from the mapped position for overlap checks.
        var position = new TextPosition(line, character);
        return index.TryGetOffset(position, out int offset) ? offset : 0;
    }

    private static bool IntersectsLineTerminator(string text, TextSpan span)
    {
        for (var i = span.Start; i < span.End; i++)
        {
            if (text[i] is '\r' or '\n')
                return true;
        }

        return false;
    }

    private static int MapModifiers(BackendSemanticTokenModifiers modifiers, string[] legend)
    {
        var bits = 0;
        if (modifiers.HasFlag(BackendSemanticTokenModifiers.Declaration))
            bits |= BitFor(legend, "declaration");
        if (modifiers.HasFlag(BackendSemanticTokenModifiers.Readonly))
            bits |= BitFor(legend, "readonly");
        if (modifiers.HasFlag(BackendSemanticTokenModifiers.Static))
            bits |= BitFor(legend, "static");
        if (modifiers.HasFlag(BackendSemanticTokenModifiers.Constant))
            bits |= BitFor(legend, "cvoloConstant");
        if (modifiers.HasFlag(BackendSemanticTokenModifiers.ExternFunction))
            bits |= BitFor(legend, "cvoloExternFunction");
        return bits;
    }

    private static int BitFor(string[] legend, string name)
    {
        int index = Array.IndexOf(legend, name);
        return index < 0 ? 0 : 1 << index;
    }

    private static int MapKind(BackendSymbolKind kind, string[] legend)
    {
        foreach (var candidate in Fallbacks(kind))
        {
            int index = Array.IndexOf(legend, candidate);
            if (index >= 0)
                return index;
        }

        return -1;
    }

    private static string[] Fallbacks(BackendSymbolKind kind) => kind switch
    {
        BackendSymbolKind.Namespace or BackendSymbolKind.Module => ["namespace", "type"],
        BackendSymbolKind.Struct => ["struct", "type"],
        BackendSymbolKind.Union => ["struct", "type"],
        BackendSymbolKind.Enum => ["enum", "type"],
        BackendSymbolKind.Interface => ["interface", "type"],
        BackendSymbolKind.Protocol => ["interface", "type"],
        BackendSymbolKind.Delegate => ["type"],
        BackendSymbolKind.TypeAlias or BackendSymbolKind.OtherType => ["type"],
        BackendSymbolKind.TypeParameter => ["typeParameter", "type"],
        BackendSymbolKind.Parameter => ["parameter", "variable"],
        BackendSymbolKind.Local or BackendSymbolKind.Global or BackendSymbolKind.Constant => ["variable"],
        BackendSymbolKind.Field or BackendSymbolKind.Property => ["property", "variable"],
        BackendSymbolKind.EnumMember => ["enumMember", "property", "variable"],
        BackendSymbolKind.Function => ["function", "method"],
        BackendSymbolKind.Method or BackendSymbolKind.ExtensionMethod or BackendSymbolKind.AssociatedFunction or BackendSymbolKind.Constructor => ["method", "function"],
        BackendSymbolKind.Operator => ["operator", "method", "function"],
        BackendSymbolKind.Keyword => ["keyword"],
        _ => [],
    };
}
