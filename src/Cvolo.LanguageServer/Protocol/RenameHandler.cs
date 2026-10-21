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

/// <summary>
/// Semantic prepare-rename and rename handler. Rename planning is all-or-nothing and never applies
/// source edits itself. Open documents resynchronize through didChange; closed project documents are
/// refreshed from disk by the backend before the next semantic snapshot is captured.
/// </summary>
internal sealed class RenameHandler(
    ILspLogger logger,
    Func<DocumentStore> storeAccessor,
    Func<bool> documentChangesSupportedAccessor,
    ICompilerObserver? observer = null)
{
    private readonly ICompilerObserver _observer = observer ?? NoopCompilerObserver.Instance;
    private DocumentStore? _store;
    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/prepareRename", UseSingleObjectParameterDeserialization = true)]
    public async Task<PrepareRenameResultPayload?> PrepareRename(PrepareRenameParamsPayload? parameters, CancellationToken cancellationToken)
    {
        using var trace = FeatureTrace.Start(_observer, "prepareRename", cancellationToken);

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!TryCapture(parameters?.TextDocument, parameters?.Position, out DocumentUri documentUri, out SemanticRequestContext context, out int offset, out string captureReason))
        {
            trace.Complete("not_found", captureReason);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        BackendRenamePreparation? preparation;
        IOperationScope backend = trace.Backend();
        try
        {
            preparation = await Task.Run(() => Store.PrepareRename(context, offset), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[prepareRename] backend failure for '{documentUri}': {ex.GetType().Name}");
            trace.CompleteBackend(backend, "faulted", "exception");
            trace.Complete("faulted", "exception");
            return null;
        }

        trace.CompleteBackend(backend, "completed");

        if (preparation is null)
        {
            trace.Complete("not_found", "symbol_lookup_miss");
            return null;
        }

        if (!TryMapSpan(context.Document.Text, preparation.SubjectSpan, out LspRange? range))
        {
            trace.MappingRejected("invalid_target_span");
            trace.Complete("not_found", "invalid_target_span");
            return null;
        }

        if (preparation.SubjectSpan.End > context.Document.Text.Length
            || context.Document.Text.Substring(preparation.SubjectSpan.Start, preparation.SubjectSpan.Length) != preparation.Placeholder)
        {
            trace.MappingRejected("invalid_target_span");
            trace.Complete("not_found", "invalid_target_span");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!Store.IsCurrent(context))
        {
            trace.Freshness(false);
            trace.Complete("stale_discarded", "document_version_stale");
            return null;
        }

        trace.Freshness(true);
        trace.Complete("success");
        return new PrepareRenameResultPayload(range, preparation.Placeholder);
    }

    [JsonRpcMethod("textDocument/rename", UseSingleObjectParameterDeserialization = true)]
    public async Task<WorkspaceEditPayload> Rename(RenameParamsPayload? parameters, CancellationToken cancellationToken)
    {
        using var trace = FeatureTrace.Start(_observer, "rename", cancellationToken);

        if (parameters?.TextDocument is not { } textDocument || parameters.Position is not { } position || parameters.NewName is null)
        {
            trace.Complete("not_found", "document_not_open");
            throw RequestFailed("Invalid rename request.");
        }

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            trace.Complete("unsupported", "unsupported_uri");
            throw RequestFailed("The rename target is not an open Cvolo document.");
        }

        if (!Store.TryCaptureSemanticEdit(documentUri, out SemanticEditRequestContext editContext))
        {
            trace.Complete("not_found", "document_not_open");
            throw RequestFailed("The rename target is not an open Cvolo document.");
        }

        var semantic = editContext.Semantic;
        var sourceIndex = new LineIndex(semantic.Document.Text);
        if (!sourceIndex.TryGetOffset(new TextPosition(position.Line, position.Character), out int offset))
        {
            trace.Complete("not_found", "invalid_position");
            throw RequestFailed("The rename position is outside the captured document.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        BackendRenameResult backendResult;
        IOperationScope backend = trace.Backend();
        try
        {
            backendResult = await Task.Run(() =>
            {
                BackendRenamePreparation? preparation = Store.PrepareRename(semantic, offset);
                if (preparation is null)
                    return new BackendRenameFailure("The selected occurrence cannot be renamed.");
                return Store.RenameSymbol(semantic.CurrentProjectSnapshot, preparation.Symbol, parameters.NewName);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[rename] backend failure for '{documentUri}': {ex.GetType().Name}");
            trace.CompleteBackend(backend, "faulted", "exception");
            trace.Complete("faulted", "exception");
            throw RequestFailed("Rename analysis failed.");
        }

        trace.CompleteBackend(backend, "completed");

        cancellationToken.ThrowIfCancellationRequested();

        if (backendResult is BackendRenameFailure failure)
        {
            trace.Complete("not_found", "symbol_lookup_miss");
            throw RequestFailed(failure.Message);
        }

        var success = (BackendRenameSuccess)backendResult;
        WorkspaceEditPayload payload;
        try
        {
            payload = BuildWorkspaceEdit(success, editContext);
        }
        catch (LocalRpcException)
        {
            trace.MappingRejected("invalid_target_span");
            trace.Complete("not_found", "invalid_target_span");
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!Store.IsCurrent(semantic))
        {
            trace.Freshness(false);
            trace.Complete("stale_discarded", "document_version_stale");
            throw ContentModified("The project changed while rename was being prepared. Please retry the rename.");
        }

        trace.Freshness(true);
        trace.Complete("success");
        return payload;
    }

    private WorkspaceEditPayload BuildWorkspaceEdit(BackendRenameSuccess result, SemanticEditRequestContext context)
    {
        if (result.Edits.Count == 0)
        {
            return documentChangesSupportedAccessor()
                ? new WorkspaceEditPayload { DocumentChanges = [] }
                : new WorkspaceEditPayload { Changes = new Dictionary<string, TextEditPayload[]>() };
        }

        var canonical = new List<ValidatedEdit>(result.Edits.Count);
        foreach (BackendRenameEdit edit in result.Edits)
        {
            if (!result.DocumentTexts.TryGetValue(edit.Document, out string? text))
                throw RequestFailed("Rename returned an affected document without captured snapshot text.");

            if (string.IsNullOrEmpty(edit.NewText) || edit.NewText.IndexOfAny(['\r', '\n']) >= 0)
                throw RequestFailed("Rename returned an invalid replacement text.");

            if (!TryMapSpan(text, edit.Span, out LspRange? range))
                throw RequestFailed("Rename returned an invalid source edit span.");

            // Prefer the URI/version captured from the editor for open documents. Closed project
            // files remain valid WorkspaceEdit targets with a null document version; the backend
            // will observe their disk text before serving the next semantic snapshot.
            DocumentUri canonicalUri = edit.Document;
            DocumentVersion? version = null;
            if (context.OpenDocuments.TryGetValue(edit.Document, out OpenDocumentEditState? open))
            {
                canonicalUri = open.Uri;
                version = open.Version;
            }

            canonical.Add(new ValidatedEdit(canonicalUri, version, edit.Span, range, edit.NewText));
        }

        // Canonicalize editor-owned URI spelling for open documents before checking duplicates/overlaps.
        var deduped = new List<ValidatedEdit>();
        foreach (var group in canonical.GroupBy(e => e.Uri, DocumentUriPathComparer.Instance))
        {
            var ordered = group.OrderBy(e => e.Span.Start).ThenBy(e => e.Span.End).ToArray();
            ValidatedEdit? previous = null;
            foreach (ValidatedEdit edit in ordered)
            {
                if (previous is not null && edit.Span.Start == previous.Span.Start && edit.Span.Length == previous.Span.Length)
                {
                    if (!string.Equals(edit.NewText, previous.NewText, StringComparison.Ordinal))
                        throw RequestFailed("Rename produced conflicting edits for the same source occurrence.");
                    continue;
                }

                if (previous is not null && edit.Span.Start < previous.Span.End)
                    throw RequestFailed("Rename produced overlapping source edits.");

                deduped.Add(edit);
                previous = edit;
            }
        }

        var groups = deduped
            .GroupBy(e => e.Uri, DocumentUriPathComparer.Instance)
            .OrderBy(g => g.Key.LocalPath, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();

        if (documentChangesSupportedAccessor())
        {
            var documentChanges = new List<TextDocumentEditPayload>(groups.Length);
            foreach (var group in groups)
            {
                ValidatedEdit first = group.First();
                documentChanges.Add(new TextDocumentEditPayload(
                    new OptionalVersionedTextDocumentIdentifierPayload(new Uri(first.Uri.LocalPath), first.Version?.Value),
                    [.. group.OrderBy(e => e.Span.Start).Select(e => new TextEditPayload(e.Range, e.NewText))]));
            }
            return new WorkspaceEditPayload { DocumentChanges = [.. documentChanges] };
        }

        var changes = new Dictionary<string, TextEditPayload[]>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var group in groups)
        {
            changes[new Uri(group.Key.LocalPath).AbsoluteUri] =
                [.. group.OrderBy(e => e.Span.Start).Select(e => new TextEditPayload(e.Range, e.NewText))];
        }
        return new WorkspaceEditPayload { Changes = changes };
    }

    private bool TryCapture(
        TextDocumentIdentifier? textDocument,
        Position? position,
        out DocumentUri documentUri,
        out SemanticRequestContext context,
        out int offset,
        out string reason)
    {
        documentUri = default;
        context = default;
        offset = 0;
        if (textDocument is null || position is null)
        {
            reason = "document_not_open";
            return false;
        }

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri))
        {
            reason = "unsupported_uri";
            return false;
        }

        if (!Store.TryCapture(documentUri, out context))
        {
            reason = "document_not_open";
            return false;
        }

        var index = new LineIndex(context.Document.Text);
        if (!index.TryGetOffset(new TextPosition(position.Line, position.Character), out offset))
        {
            reason = "invalid_position";
            return false;
        }

        reason = "success";
        return true;
    }

    private static bool TryMapSpan(string text, TextSpan span, out LspRange range)
    {
        range = null!;
        if (span.Start < 0 || span.Length <= 0 || span.End > text.Length || ReferencesHandler.IntersectsLineTerminator(text, span))
            return false;
        var index = new LineIndex(text);
        if (!index.TryGetRange(span, out TextRange mapped) || mapped.Start.Line != mapped.End.Line)
            return false;
        if (mapped.End.Character - mapped.Start.Character != span.Length)
            return false;
        range = new LspRange
        {
            Start = new Position(mapped.Start.Line, mapped.Start.Character),
            End = new Position(mapped.End.Line, mapped.End.Character),
        };
        return true;
    }

    private static LocalRpcException RequestFailed(string message)
        => new(message) { ErrorCode = ProtocolErrorCodes.RequestFailed };

    private static LocalRpcException ContentModified(string message)
        => new(message) { ErrorCode = ProtocolErrorCodes.ContentModified };

    private sealed record ValidatedEdit(DocumentUri Uri, DocumentVersion? Version, TextSpan Span, LspRange Range, string NewText);
}
