using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/codeAction and codeAction/resolve handler for quick fixes. Discovery and edit
/// planning are owned by the compiler-backed service; this handler captures editor state, maps
/// ranges, builds the WorkspaceEdit and guards resolve lifetime and staleness. It never applies
/// edits itself.
/// </summary>
internal sealed class CodeActionHandler(
    ILspLogger logger,
    Func<DocumentStore> storeAccessor,
    Func<bool> documentChangesSupportedAccessor,
    Func<bool> lazyResolveSupportedAccessor,
    Func<CodeActionResolveStore> resolveStoreAccessor)
{
    private const string QuickFixKind = "quickfix";
    private const string ResolveFallbackMessage = "Code fix could not be resolved.";

    private DocumentStore? _store;
    private DocumentStore Store => _store ??= storeAccessor();

    [JsonRpcMethod("textDocument/codeAction", UseSingleObjectParameterDeserialization = true)]
    public async Task<CodeActionPayload[]?> CodeAction(CodeActionParamsPayload? parameters, CancellationToken cancellationToken)
    {
        if (parameters?.TextDocument is not { } textDocument || parameters.Range is not { } requestRange)
            return null;

        await RequestBarrier.WaitAsync(Store, cancellationToken);

        if (!TextDocumentSyncHandler.TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri)
            || !TextDocumentSyncHandler.IsCvoloDocument(documentUri)
            || !Store.TryCaptureSemanticEdit(documentUri, out SemanticEditRequestContext editContext))
            return null;

        if (parameters.Context?.Only is { } only && !MatchesQuickFix(only))
            return [];

        var semantic = editContext.Semantic;
        string text = semantic.Document.Text;
        var index = new LineIndex(text);
        if (!TryMapRequestRange(text, index, requestRange, out TextSpan range))
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        BackendCodeFixResult backendResult;
        try
        {
            backendResult = await Task.Run(() => Store.GetCodeFixes(editContext, range), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[codeAction] backend failure for '{documentUri}': {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (backendResult.Fixes.Count == 0)
            return [];

        CodeActionResolveStore resolveStore = resolveStoreAccessor();
        bool lazy = lazyResolveSupportedAccessor() && backendResult.Fixes.Count <= resolveStore.Capacity;

        var actions = new List<CodeActionPayload>(backendResult.Fixes.Count);
        var staged = new List<StagedCodeActionResolveItem>();

        foreach (BackendCodeFixInfo fix in backendResult.Fixes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(fix.Title))
                continue;

            DiagnosticPayload[]? diagnostics = MapDiagnostics(text, index, documentUri, fix);
            if (diagnostics is null || diagnostics.Length == 0)
                continue;

            if (lazy)
            {
                string token = resolveStore.MintToken();
                staged.Add(new StagedCodeActionResolveItem(token, new CodeActionResolveEntry(editContext, fix.Handle)));
                actions.Add(new CodeActionPayload
                {
                    Title = fix.Title,
                    Kind = QuickFixKind,
                    Diagnostics = diagnostics,
                    Data = token,
                });
                continue;
            }

            WorkspaceEditPayload? edit = TryResolveEager(editContext, fix, cancellationToken);
            if (edit is null)
                continue;

            actions.Add(new CodeActionPayload
            {
                Title = fix.Title,
                Kind = QuickFixKind,
                Diagnostics = diagnostics,
                Edit = edit,
            });
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(semantic))
        {
            logger.Debug($"[codeAction] discarded stale result for '{documentUri}'.");
            return null;
        }

        if (lazy && staged.Count > 0)
            resolveStore.Commit(staged);

        return [.. actions];
    }

    [JsonRpcMethod("codeAction/resolve", UseSingleObjectParameterDeserialization = true)]
    public async Task<Dictionary<string, object?>?> ResolveCodeAction(Dictionary<string, object?>? action, CancellationToken cancellationToken)
    {
        if (action is null)
            throw RequestFailed("Invalid code action resolve request.");

        if (!action.TryGetValue("data", out object? data) || data is not string token || string.IsNullOrEmpty(token))
            throw RequestFailed(ResolveFallbackMessage);

        CodeActionResolveStore resolveStore = resolveStoreAccessor();
        if (!resolveStore.TryGet(token, out CodeActionResolveEntry entry))
            throw RequestFailed(ResolveFallbackMessage);

        cancellationToken.ThrowIfCancellationRequested();

        var semantic = entry.Context.Semantic;
        if (!Store.IsCurrent(semantic))
        {
            resolveStore.Evict(token);
            throw ContentModified("The project changed while the code fix was being resolved. Please retry.");
        }

        if (action.TryGetValue("edit", out object? existing) && existing is not null)
            return action;

        BackendCodeFixResolution resolution;
        try
        {
            resolution = await Task.Run(() => Store.ResolveCodeFix(entry.Context, entry.Handle), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[codeAction/resolve] backend failure: {ex.Message}");
            throw RequestFailed(ResolveFallbackMessage);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (resolution is BackendCodeFixFailure failure)
            throw RequestFailed(string.IsNullOrWhiteSpace(failure.Message) ? ResolveFallbackMessage : failure.Message);

        var success = (BackendCodeFixSuccess)resolution;
        if (!TryBuildWorkspaceEdit(success, entry.Context, out WorkspaceEditPayload? edit))
            throw RequestFailed(ResolveFallbackMessage);

        cancellationToken.ThrowIfCancellationRequested();

        if (!Store.IsCurrent(semantic))
        {
            resolveStore.Evict(token);
            throw ContentModified("The project changed while the code fix was being resolved. Please retry.");
        }

        action["edit"] = edit;
        return action;
    }

    private WorkspaceEditPayload? TryResolveEager(SemanticEditRequestContext context, BackendCodeFixInfo fix, CancellationToken cancellationToken)
    {
        BackendCodeFixResolution resolution;
        try
        {
            resolution = Store.ResolveCodeFix(context, fix.Handle);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning($"[codeAction] backend fix resolution failure: {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (resolution is not BackendCodeFixSuccess success)
            return null;

        return TryBuildWorkspaceEdit(success, context, out WorkspaceEditPayload? edit) ? edit : null;
    }

    private DiagnosticPayload[]? MapDiagnostics(string text, LineIndex index, DocumentUri documentUri, BackendCodeFixInfo fix)
    {
        var mapped = new List<DiagnosticPayload>(fix.Diagnostics.Count);
        var comparer = DocumentUriPathComparer.Instance;
        foreach (BackendDiagnostic diagnostic in fix.Diagnostics)
        {
            if (!comparer.Equals(diagnostic.Location.Document, documentUri))
                continue;

            if (!index.TryGetRange(diagnostic.Location.Span, out TextRange range))
                continue;

            mapped.Add(new DiagnosticPayload(
                ToLspRange(range),
                MapSeverity(diagnostic.Severity),
                diagnostic.Code,
                "cvolo",
                diagnostic.Message,
                null));
        }

        return mapped.Count == 0 ? null : [.. mapped];
    }

    private bool TryBuildWorkspaceEdit(BackendCodeFixSuccess result, SemanticEditRequestContext context, out WorkspaceEditPayload payload)
    {
        payload = null!;
        if (result.Edits.Count == 0)
            return false;

        var prepared = new List<PreparedEdit>(result.Edits.Count);
        int ordinal = 0;
        foreach (BackendCodeFixEdit edit in result.Edits)
        {
            if (edit.NewText is null)
                return false;

            if (!result.DocumentTexts.TryGetValue(edit.Document, out string? text))
                return false;

            if (edit.Span.Start < 0 || edit.Span.Length < 0 || edit.Span.End > text.Length)
                return false;

            if (!context.OpenDocuments.TryGetValue(edit.Document, out OpenDocumentEditState? open))
                return false;

            if (!TryMapEditSpan(text, edit.Span, out LspRange? range))
                return false;

            prepared.Add(new PreparedEdit(open.Uri, open.Version, edit.Span, edit.NewText, ordinal, range));
            ordinal++;
        }

        var groups = new List<ValidatedEditGroup>();
        foreach (var group in prepared.GroupBy(e => e.Uri, DocumentUriPathComparer.Instance))
        {
            List<PreparedEdit>? normalized = NormalizeGroup([.. group]);
            if (normalized is null)
                return false;

            groups.Add(new ValidatedEditGroup(group.Key, normalized));
        }

        groups.Sort((a, b) => string.Compare(
            a.Uri.LocalPath,
            b.Uri.LocalPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

        if (documentChangesSupportedAccessor())
        {
            var documentChanges = new List<TextDocumentEditPayload>(groups.Count);
            foreach (ValidatedEditGroup group in groups)
            {
                PreparedEdit first = group.Edits[0];
                documentChanges.Add(new TextDocumentEditPayload(
                    new OptionalVersionedTextDocumentIdentifierPayload(new Uri(group.Uri.LocalPath), first.Version?.Value),
                    [.. group.Edits.Select(e => new TextEditPayload(e.Range, e.NewText))]));
            }

            payload = new WorkspaceEditPayload { DocumentChanges = [.. documentChanges] };
            return true;
        }

        var changes = new Dictionary<string, TextEditPayload[]>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (ValidatedEditGroup group in groups)
        {
            changes[new Uri(group.Uri.LocalPath).AbsoluteUri] =
                [.. group.Edits.Select(e => new TextEditPayload(e.Range, e.NewText))];
        }

        payload = new WorkspaceEditPayload { Changes = changes };
        return true;
    }

    private static List<PreparedEdit>? NormalizeGroup(List<PreparedEdit> edits)
    {
        var ordered = edits
            .OrderBy(e => e.Span.Start)
            .ThenBy(e => e.Span.Length == 0 ? 0 : 1)
            .ThenBy(e => e.Ordinal)
            .ToList();

        var deduped = new List<PreparedEdit>(ordered.Count);
        foreach (PreparedEdit edit in ordered)
        {
            if (edit.Span.Length > 0 && deduped.Any(previous =>
                    previous.Span.Length == edit.Span.Length
                    && previous.Span.Start == edit.Span.Start
                    && string.Equals(previous.NewText, edit.NewText, StringComparison.Ordinal)))
                continue;

            deduped.Add(edit);
        }

        var nonZero = new List<PreparedEdit>();
        foreach (PreparedEdit edit in deduped)
        {
            if (edit.Span.Length == 0)
                continue;

            foreach (PreparedEdit previous in nonZero)
            {
                if (edit.Span.Start < previous.Span.End)
                    return null;
            }

            nonZero.Add(edit);
        }

        foreach (PreparedEdit insert in deduped)
        {
            if (insert.Span.Length != 0)
                continue;

            foreach (PreparedEdit region in nonZero)
            {
                if (insert.Span.Start > region.Span.Start && insert.Span.Start < region.Span.End)
                    return null;
            }
        }

        return deduped;
    }

    private static bool TryMapRequestRange(string text, LineIndex index, LspRange range, out TextSpan span)
    {
        span = default;
        if (!index.TryGetOffset(new TextPosition(range.Start.Line, range.Start.Character), out int start)
            || !index.TryGetOffset(new TextPosition(range.End.Line, range.End.Character), out int end))
            return false;

        if (start < 0 || end < start || end > text.Length)
            return false;

        if (IsInsideLineTerminator(text, start) || IsInsideLineTerminator(text, end))
            return false;

        span = new TextSpan(start, end - start);
        return true;
    }

    private static bool TryMapEditSpan(string text, TextSpan span, out LspRange range)
    {
        range = null!;
        if (span.Start < 0 || span.Length < 0 || span.End > text.Length)
            return false;

        if (IsInsideLineTerminator(text, span.Start) || IsInsideLineTerminator(text, span.End))
            return false;

        var index = new LineIndex(text);
        if (!index.TryGetRange(span, out TextRange mapped))
            return false;

        range = ToLspRange(mapped);
        return true;
    }

    private static bool IsInsideLineTerminator(string text, int offset)
        => offset > 0 && offset < text.Length && text[offset - 1] == '\r' && text[offset] == '\n';

    private static bool MatchesQuickFix(IReadOnlyList<string> only)
    {
        foreach (string requested in only)
        {
            if (string.Equals(QuickFixKind, requested, StringComparison.Ordinal)
                || QuickFixKind.StartsWith(requested + ".", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static LspRange ToLspRange(TextRange range)
    {
        return new LspRange
        {
            Start = new Position(range.Start.Line, range.Start.Character),
            End = new Position(range.End.Line, range.End.Character),
        };
    }

    private static DiagnosticSeverity MapSeverity(BackendDiagnosticSeverity severity)
    {
        return severity switch
        {
            BackendDiagnosticSeverity.Error => DiagnosticSeverity.Error,
            BackendDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
            BackendDiagnosticSeverity.Info => DiagnosticSeverity.Information,
            BackendDiagnosticSeverity.Hint => DiagnosticSeverity.Hint,
            _ => DiagnosticSeverity.Warning,
        };
    }

    private static LocalRpcException RequestFailed(string message)
        => new(message) { ErrorCode = ProtocolErrorCodes.RequestFailed };

    private static LocalRpcException ContentModified(string message)
        => new(message) { ErrorCode = ProtocolErrorCodes.ContentModified };

    private sealed record PreparedEdit(DocumentUri Uri, DocumentVersion? Version, TextSpan Span, string NewText, int Ordinal, LspRange Range);

    private sealed record ValidatedEditGroup(DocumentUri Uri, List<PreparedEdit> Edits);
}
