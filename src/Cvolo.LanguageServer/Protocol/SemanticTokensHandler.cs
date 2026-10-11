using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
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
    Func<string[]> tokenModifiersAccessor)
{
    private const int SemanticTokensMaxInteractiveTextLength = 100_000;

    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod(Methods.TextDocumentSemanticTokensFullName, UseSingleObjectParameterDeserialization = true)]
    public async Task<SemanticTokensResponse?> SemanticTokensFull(SemanticTokensParams? parameters, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[semanticTokens] request without a textDocument; ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[semanticTokens] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[semanticTokens] '{documentUri}' is not open; no tokens.");
            return null;
        }
        LogTiming("capture", stopwatch, documentUri);

        if (context.Document.Text.Length > SemanticTokensMaxInteractiveTextLength)
        {
            logger.Warning($"[perf] semanticTokens skipped for large document: uri='{documentUri}', textLength={context.Document.Text.Length}, limit={SemanticTokensMaxInteractiveTextLength}");
            return new SemanticTokensResponse([]);
        }

        BackendSemanticTokenResult? result;
        try
        {
            result = await Store.RunInteractiveQueryAsync(
                context,
                "semanticTokens",
                () => Store.GetSemanticTokens(context),
                cancellationToken).ConfigureAwait(false);
            LogTiming("backend", stopwatch, documentUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[semanticTokens] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (result is null)
        {
            logger.Debug($"[semanticTokens] skipped stale request for '{documentUri}'.");
            return null;
        }

        int[]? data = Encode(result, context.Document.Text, tokenTypesAccessor(), tokenModifiersAccessor());
        LogTiming("encode", stopwatch, documentUri);
        if (data is null)
        {
            return null;
        }

        // Encoding can be non-trivial for large documents. Perform the final freshness
        // gate after normalization/encoding so no result from an old project generation
        // can escape as current semantic highlighting.
        cancellationToken.ThrowIfCancellationRequested();
        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[semanticTokens] discarded stale result for '{documentUri}'.");
            return null;
        }

        logger.Info($"[semanticTokens] {documentUri}: {data.Length / 5} token(s).");
        LogTiming("total", stopwatch, documentUri);
        LogTokenBreakdown(documentUri, context.Document.Version, result);
        return new SemanticTokensResponse(data);
    }

    private readonly Dictionary<DocumentUri, DocumentVersion> _lastTokenBreakdownVersion = new();

    /// <summary>
    /// Emits one warning per document version summarizing which semantic token kinds the
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
        logger.Warning($"[perf] semantic tokens breakdown: uri='{uri}', version={version}, total={result.Tokens.Count} [{summary}]");
    }

    private void LogTiming(string phase, Stopwatch stopwatch, DocumentUri uri, int thresholdMs = 50)
    {
        if (stopwatch.ElapsedMilliseconds >= thresholdMs)
            logger.Warning($"[perf] semanticTokens {phase} took {stopwatch.ElapsedMilliseconds} ms: uri='{uri}'");
        else
            logger.Debug($"[perf] semanticTokens {phase} took {stopwatch.ElapsedMilliseconds} ms: uri='{uri}'");
    }

    private int[]? Encode(BackendSemanticTokenResult result, string text, string[] tokenTypes, string[] tokenModifiers)
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
