using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using System.Diagnostics;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/hover handler. Validates the wire request, captures a coherent
/// semantic context, maps the UTF-16 position to an absolute offset, resolves
/// the symbol from that one immutable snapshot, suppresses stale results, and
/// formats the compiler-owned display text as LSP markup content. It owns no
/// Cvolo semantics (§16, §24, §29.1).
/// </summary>
internal sealed class HoverHandler(ILspLogger logger, Func<DocumentStore> storeAccessor, Func<bool> markdownAccessor)
{
    private DocumentStore? _store;

    // Resolved on first use so the session-scoped store is created from the
    // workspace folders/root established by initialize, not at registration time.
    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod(Methods.TextDocumentHoverName, UseSingleObjectParameterDeserialization = true)]
    public async Task<Hover?> Hover(TextDocumentPositionParams? parameters, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[hover] request without a textDocument; ignored.");
            return null;
        }

        Position position = parameters.Position;

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[hover] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[hover] '{documentUri}' is not open; no hover.");
            return null;
        }
        LogTiming("capture", stopwatch, documentUri);

        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            logger.Debug($"[hover] invalid request position {position.Line}:{position.Character} for '{documentUri}'.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        BackendSymbolInfo? symbol;
        BackendTypeLayoutInspection? layout = null;
        try
        {
            // Both reads belong to the captured snapshot. The layout service
            // resolves the compiler's type rather than parsing the hover text.
            var response = await Store.RunInteractiveQueryAsync(
                context,
                "hover",
                () =>
                {
                    var resolved = Store.GetSymbolAtPosition(context, offset);
                    var typeLayout = resolved is { Kind: BackendSymbolKind.Struct or BackendSymbolKind.Union
                        or BackendSymbolKind.Local or BackendSymbolKind.Parameter
                        or BackendSymbolKind.Global or BackendSymbolKind.Constructor }
                        ? Store.GetTypeLayoutAtPosition(context, offset)
                        : null;
                    return (resolved, typeLayout);
                },
                cancellationToken).ConfigureAwait(false);
            symbol = response.resolved;
            layout = response.typeLayout;
            LogTiming("backend", stopwatch, documentUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[hover] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(context))
        {
            logger.Debug($"[hover] discarded stale result for '{documentUri}'.");
            return null;
        }

        Hover? hover = symbol is null ? null : Map(symbol, layout, index, context.Document.Text.Length);
        LogTiming("total", stopwatch, documentUri);
        return hover;
    }

    private void LogTiming(string phase, Stopwatch stopwatch, DocumentUri uri, int thresholdMs = 50)
    {
        if (stopwatch.ElapsedMilliseconds >= thresholdMs)
            logger.Debug($"[perf] hover {phase} took {stopwatch.ElapsedMilliseconds} ms: uri='{uri}'");
        else
            logger.Debug($"[perf] hover {phase} took {stopwatch.ElapsedMilliseconds} ms: uri='{uri}'");
    }

    private Hover? Map(BackendSymbolInfo symbol, BackendTypeLayoutInspection? layout, LineIndex index, int textLength)
    {
        TextSpan span = symbol.SubjectSpan;

        // Never clamp or fabricate a subject range: a span that is outside the
        // captured text or cannot be mapped is discarded (§16.2).
        if (span.Start < 0 || span.Length < 0 || span.End > textLength)
        {
            logger.Debug("[hover] discarding result with an out-of-range subject span.");
            return null;
        }

        if (!index.TryGetRange(span, out TextRange range))
        {
            logger.Debug("[hover] discarding result whose subject span cannot be mapped.");
            return null;
        }

        return new Hover
        {
            Contents = FormatContents(symbol.DisplayText, symbol.Documentation, symbol.NativeInterop, layout),
            Range = new LspRange
            {
                Start = new Position(range.Start.Line, range.Start.Character),
                End = new Position(range.End.Line, range.End.Character),
            },
        };
    }

    private MarkupContent FormatContents(string displayText, string? documentation, BackendNativeInteropMetadata? nativeInterop, BackendTypeLayoutInspection? layout)
    {
        string? nativeDetails = FormatNativeInterop(nativeInterop);
        string? layoutDetails = FormatCompactLayout(layout);
        if (!markdownAccessor())
        {
            var sections = new[] { displayText, layoutDetails, nativeDetails, documentation }
                .Where(section => !string.IsNullOrEmpty(section));
            return new MarkupContent
            {
                Kind = MarkupKind.PlainText,
                Value = string.Join("\n\n", sections),
            };
        }

        // Choose a backtick fence longer than any run present in the display text
        // so the compiler-owned content can never break out of the code block (§16.3).
        var fence = "```";
        while (displayText.Contains(fence, StringComparison.Ordinal))
        {
            fence += "`";
        }

        var markdown = $"{fence}cvolo\n{displayText}\n{fence}";
        if (!string.IsNullOrEmpty(layoutDetails))
            markdown += $"\n\n{layoutDetails}";
        if (!string.IsNullOrEmpty(nativeDetails))
            markdown += $"\n\n**Native interop**\n\n{nativeDetails.Replace("\n", "  \n", StringComparison.Ordinal)}";

        if (!string.IsNullOrEmpty(documentation))
            markdown += $"\n\n{documentation}";

        return new MarkupContent
        {
            Kind = MarkupKind.Markdown,
            Value = markdown,
        };
    }

    /// <summary>
    /// A brief, compiler-derived storage summary for type and value hover.
    /// Offset notation refers to fields within the struct, not the local
    /// variable's stack address. Missing metadata is omitted, never guessed.
    /// </summary>
    internal static string? FormatCompactLayout(BackendTypeLayoutInspection? layout)
    {
        if (layout is null)
            return null;

        var summary = $"**{layout.Size} B** · align {layout.Alignment} B";
        if (layout.PaddingSize > 0)
            summary += $" · padding {layout.PaddingSize} B";

        if (layout.Members.Count == 0)
            return summary;

        // Keep a hover short: show a few fields and let the layout view own
        // the complete table. No fabricated details for unknown fields.
        var fields = layout.Members.Take(3)
            .Select(member => $"`{member.Name}` +{member.Offset}: {member.Size} B");
        var suffix = layout.Members.Count > 3 ? " · …" : "";
        return summary + "\n\n" + string.Join(" · ", fields) + suffix;
    }

    private static string? FormatNativeInterop(BackendNativeInteropMetadata? metadata)
    {
        if (metadata is null)
            return null;

        var lines = new List<string>
        {
            $"kind: {metadata.Kind switch { BackendNativeInteropKind.NativeDelegate => "native delegate", BackendNativeInteropKind.RawUnion => "raw union", BackendNativeInteropKind.ForeignGlobal => "foreign global", _ => "native interop" }}",
        };

        Add("calling convention", metadata.CallingConvention);
        Add("import name", metadata.ImportName);
        Add("library", metadata.LibraryName);
        Add("windows path", metadata.WinPath);
        Add("linux path", metadata.LinuxPath);
        Add("macOS path", metadata.MacPath);
        return string.Join("\n", lines);

        void Add(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                lines.Add($"{label}: {value}");
        }
    }

}
