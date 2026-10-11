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
/// textDocument/codeLens handler. Returns the reference and layout annotations of the requested open
/// document from its captured snapshot. It answers the whole document in one request and never
/// resolves a single lens lazily; the only decisions made here are which settings were in force when
/// the snapshot was queried and which lenses can be presented as they are (§4, §55, §83).
/// </summary>
internal sealed class CodeLensHandler(
    ILspLogger logger,
    Func<DocumentStore> storeAccessor,
    Func<EditorIntelligenceSettings> settingsAccessor)
{
    private DocumentStore? _store;

    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/codeLens", UseSingleObjectParameterDeserialization = true)]
    public async Task<CodeLens[]?> CodeLenses(CodeLensParamsPayload? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Debug("[codeLens] request without a textDocument; ignored.");
            return null;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            logger.Debug($"[codeLens] non-cvolo document '{textDocument.Uri}' ignored.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.TryCapture(documentUri, out SemanticRequestContext context))
        {
            logger.Debug($"[codeLens] '{documentUri}' is not open; no lenses.");
            return null;
        }

        logger.Warning($"[codeLens] capture uri='{documentUri}', version={context.Document.Version}, textLength={context.Document.Text.Length}.");

        BackendCodeLensOptions options = settingsAccessor().CodeLens;
        BackendOffsetFormat offsetFormat = settingsAccessor().Layout.OffsetFormat;
        IReadOnlyList<BackendCodeLensInfo> lenses;
        try
        {
            lenses = await Task.Run(() => Store.GetCodeLenses(context, options), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.Warning($"[codeLens] request cancelled during compute for '{documentUri}', version={context.Document.Version}.");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[codeLens] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            logger.Warning($"[codeLens] request cancelled after compute for '{documentUri}', version={context.Document.Version}; lenses={lenses.Count} computed then dropped.");
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!Store.IsCurrent(context))
        {
            logger.Warning($"[codeLens] discarded stale result for '{documentUri}', version={context.Document.Version}.");
            return null;
        }

        var index = new LineIndex(context.Document.Text);
        int textLength = context.Document.Text.Length;

        // A lens command is handed back to the client, which resolves the document it names, so it
        // has to carry the URI the client understands. The local path is not a URI: on Windows
        // Uri treats "d:\dir\file.cvl" as scheme "d", and a client that parses that back cannot
        // resolve the resource. Echoing the validated document's absolute URI keeps the round trip
        // exact and never re-encodes a path into something the client did not send.
        string documentUriText = textDocument.Uri!.AbsoluteUri;

        var ordered = new List<BackendCodeLensInfo>(lenses);
        ordered.Sort(CompareLenses);

        var mapped = new List<CodeLens>(ordered.Count);
        foreach (BackendCodeLensInfo lens in ordered)
        {
            if (TryMapLens(lens, index, textLength, documentUriText, offsetFormat, out CodeLens codeLens))
            {
                mapped.Add(codeLens);
            }
        }

        logger.Warning($"[codeLens] response uri='{documentUri}', version={context.Document.Version}, lenses={lenses.Count}, mapped={mapped.Count}.");

        return [.. mapped];
    }

    private bool TryMapLens(
        BackendCodeLensInfo lens,
        LineIndex index,
        int textLength,
        string documentUriText,
        BackendOffsetFormat offsetFormat,
        out CodeLens codeLens)
    {
        if (!SpanMapper.TryMapRange(index, textLength, lens.Range, out LspRange range))
        {
            logger.Debug($"[codeLens] dropped a '{lens.Kind}' lens whose range does not map to the captured text.");
            codeLens = null!;
            return false;
        }

        codeLens = new CodeLens
        {
            Range = range,
            Command = MapCommand(lens, PresentTitle(lens, offsetFormat), index, documentUriText),
        };
        return true;
    }

    /// <summary>
    /// The text a reader sees. It is the compiler's own presentation, except for a field layout lens,
    /// which is written again from the compiler's structured numbers when the reader asked for another
    /// offset notation. Only the radix changes: the offset, size, alignment and padding are the ones
    /// the compiler computed, so the lens and the detailed view can never disagree (§17).
    /// </summary>
    private static string PresentTitle(BackendCodeLensInfo lens, BackendOffsetFormat offsetFormat) =>
        lens.FieldLayout is { } field
            ? LayoutTextFormatter.FieldLayout(field, offsetFormat)
            : lens.Title;

    /// <summary>
    /// Builds the command a client presents. Its title is always the text the reader sees, because the
    /// title is the only place a CodeLens can carry one; its identifier is the backend's command name
    /// when the lens is clickable, and empty when it is not (§11, §21, §39). A lens the compiler
    /// described but gave no behaviour to — an import or export linkage fact, say — is still worth
    /// showing, and an empty identifier is how the protocol says "text, no action". A lens that was
    /// clickable but whose position no longer exists in the captured text falls back to the same
    /// shape: a command that would open the wrong location is worse than a label that does nothing.
    /// </summary>
    private Command? MapCommand(BackendCodeLensInfo lens, string title, LineIndex index, string documentUriText)
    {
        if (lens.Command is not { } command)
        {
            return new Command { Title = title, CommandIdentifier = string.Empty, Arguments = [] };
        }

        var arguments = new List<object>(command.Arguments.Count + 1) { documentUriText };
        for (int i = 0; i < command.Arguments.Count; i++)
        {
            if (command.Arguments[i] is not BackendCodeLensPositionArgument position
                || !index.TryGetPosition(position.Position, out TextPosition mapped))
            {
                logger.Debug($"[codeLens] presented the '{lens.Kind}' lens as text only: its position does not map to the captured text.");
                return new Command { Title = title, CommandIdentifier = string.Empty, Arguments = [] };
            }

            arguments.Add(new
            {
                line = mapped.Line,
                character = mapped.Character,
            });
        }

        return new Command
        {
            Title = title,
            CommandIdentifier = command.Name,
            Arguments = [.. arguments],
        };
    }

    /// <summary>
    /// Orders the lenses of one declaration the way the editor presents them: references, then
    /// layout, then native interop. The anchored range is compared first, so the result never
    /// depends on the order the backend happened to enumerate declarations in (§83).
    /// </summary>
    private static int CompareLenses(BackendCodeLensInfo left, BackendCodeLensInfo right)
    {
        int byStart = left.Range.Start.CompareTo(right.Range.Start);
        if (byStart != 0)
        {
            return byStart;
        }

        byStart = left.Range.End.CompareTo(right.Range.End);
        if (byStart != 0)
        {
            return byStart;
        }

        byStart = KindRank(left.Kind).CompareTo(KindRank(right.Kind));
        return byStart != 0 ? byStart : string.CompareOrdinal(left.Title, right.Title);
    }

    private static int KindRank(BackendCodeLensKind kind) => kind switch
    {
        BackendCodeLensKind.References => 0,
        BackendCodeLensKind.Layout => 1,
        BackendCodeLensKind.NativeInterop => 2,
        _ => 3,
    };
}
