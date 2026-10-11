using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using System.Diagnostics;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// textDocument/didOpen, didChange and didClose handler. Converts wire payloads
/// into core documents, feeds the session-scoped <see cref="DocumentStore"/>,
/// schedules diagnostics, and never emits responses. All failures are
/// non-fatal: the server stays alive and simply does not publish state.
/// </summary>
internal sealed class TextDocumentSyncHandler(
    ILspLogger logger,
    DiagnosticSink diagnostics,
    Func<DocumentStore> storeAccessor,
    Action? onProjectChanged = null) : IDisposable
{
    private const string CvoloLanguageId = "cvolo";

    private DocumentStore? _store;
    private DiagnosticPublisher? _publisher;
    private DiagnosticScheduler? _scheduler;

    /// <summary>
    /// Guards publication of the members below. Notifications are dispatched on their own threads,
    /// so a didOpen and a concurrent request can both reach a member for the first time at once.
    /// </summary>
    private readonly object _publicationGate = new();

    /// <summary>
    /// Publishes exactly one instance: a volatile read on the fast path, then a re-check under the
    /// gate that returns the field instead of a caller's own temporary, so a forked publisher or
    /// scheduler (and its timers) is impossible.
    /// </summary>
    private T GetOrCreate<T>(ref T? slot, Func<T> factory) where T : class
    {
        T? published = Volatile.Read(ref slot);
        if (published is not null)
        {
            return published;
        }

        lock (_publicationGate)
        {
            return slot ??= factory();
        }
    }

    // Resolved on first use so the session-scoped store is created from the
    // workspace folders/root established by initialize, not at registration time.
    private DocumentStore Store => GetOrCreate(ref _store, storeAccessor);

    private DiagnosticPublisher Publisher => GetOrCreate(ref _publisher, () => new DiagnosticPublisher(logger, diagnostics));

    private DiagnosticScheduler Scheduler => GetOrCreate(ref _scheduler, () => new DiagnosticScheduler(Store, Publisher, logger));

    [JsonRpcMethod(Methods.TextDocumentDidOpenName, UseSingleObjectParameterDeserialization = true)]
    public void DidOpen(DidOpenTextDocumentParams? parameters)
    {
        var stopwatch = Stopwatch.StartNew();
        if (parameters?.TextDocument is not { } item)
        {
            logger.Warning("didOpen without a textDocument; ignored.");
            return;
        }

        if (!TryCreateDocumentUri(item.Uri, out DocumentUri documentUri))
        {
            logger.Debug($"didOpen for a non-file document ('{item.Uri}') ignored.");
            return;
        }

        if (!IsCvoloDocument(documentUri))
        {
            logger.Debug($"didOpen for non-cvolo document '{documentUri}' ignored.");
            return;
        }

        if (!string.Equals(item.LanguageId, CvoloLanguageId, StringComparison.OrdinalIgnoreCase))
        {
            logger.Debug($"didOpen for '{documentUri}' reports languageId '{item.LanguageId}' (expected '{CvoloLanguageId}').");
        }

        if (Store.Open(documentUri, item.LanguageId, item.Version, item.Text) is null)
        {
            logger.Warning($"[diag] didOpen for '{documentUri}' was rejected; diagnostics not scheduled.");
            return;
        }
        LogTiming("didOpen/store", stopwatch, documentUri, 100);

        if (!Store.TryGetProject(documentUri, out BackendProject? project))
        {
            logger.Warning($"[diag] no backend project for '{documentUri}'; diagnostics not scheduled.");
            return;
        }

        logger.Info($"[diag] didOpen '{documentUri}' v{item.Version}; scheduling diagnostics.");
        Scheduler.Schedule(project);
        onProjectChanged?.Invoke();
        LogTiming("didOpen/total", stopwatch, documentUri, 100);
    }

    [JsonRpcMethod(Methods.TextDocumentDidChangeName, UseSingleObjectParameterDeserialization = true)]
    public void DidChange(DidChangeTextDocumentParams? parameters)
    {
        var stopwatch = Stopwatch.StartNew();
        if (parameters?.TextDocument is not { } textDocument || parameters.ContentChanges is null)
        {
            logger.Warning("didChange without a textDocument or contentChanges; ignored.");
            return;
        }

        if (!TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri))
        {
            logger.Debug($"didChange for a non-file document ('{textDocument.Uri}') ignored.");
            return;
        }

        if (!IsCvoloDocument(documentUri))
        {
            return;
        }

        var changes = new List<DocumentChange>(parameters.ContentChanges.Length);
        foreach (var change in parameters.ContentChanges)
        {
            TextRange? range = change.Range is null ? null : ToCoreRange(change.Range);
            changes.Add(new DocumentChange(range, change.Text ?? string.Empty));
        }

        if (Store.ApplyChanges(documentUri, textDocument.Version, changes) is null)
        {
            logger.Debug($"[diag] didChange for '{documentUri}' v{textDocument.Version} was not applied; diagnostics not scheduled.");
            return;
        }
        LogTiming("didChange/store", stopwatch, documentUri);

        if (!Store.TryGetProject(documentUri, out BackendProject? project))
        {
            logger.Warning($"[diag] no backend project for '{documentUri}'; diagnostics not scheduled.");
            return;
        }

        logger.Info($"[diag] didChange '{documentUri}' v{textDocument.Version}; scheduling diagnostics.");
        Scheduler.Schedule(project);
        onProjectChanged?.Invoke();
        LogTiming("didChange/total", stopwatch, documentUri);
    }

    [JsonRpcMethod(Methods.TextDocumentDidCloseName, UseSingleObjectParameterDeserialization = true)]
    public void DidClose(DidCloseTextDocumentParams? parameters)
    {
        var stopwatch = Stopwatch.StartNew();
        if (parameters?.TextDocument is not { } textDocument)
        {
            logger.Warning("didClose without a textDocument; ignored.");
            return;
        }

        if (!TryCreateDocumentUri(textDocument.Uri, out DocumentUri documentUri))
        {
            logger.Debug($"didClose for a non-file document ('{textDocument.Uri}') ignored.");
            return;
        }

        if (!IsCvoloDocument(documentUri))
        {
            return;
        }

        BackendProject? project = Store.Close(documentUri);
        if (project is null)
        {
            logger.Debug($"[diag] didClose for '{documentUri}' ignored; document was not open.");
            return;
        }
        LogTiming("didClose/store", stopwatch, documentUri, 100);

        logger.Info($"[diag] didClose '{documentUri}'; cleared published diagnostics.");
        Publisher.PublishEmpty(documentUri);
        if (Store.HasOpenDocuments(project))
        {
            Scheduler.Schedule(project);
        }
        else
        {
            Scheduler.OnProjectDrained(project);
        }

        onProjectChanged?.Invoke();
        LogTiming("didClose/total", stopwatch, documentUri, 100);
    }

    private void LogTiming(string phase, Stopwatch stopwatch, DocumentUri uri, int thresholdMs = 50)
    {
        if (stopwatch.ElapsedMilliseconds >= thresholdMs)
            logger.Warning($"[perf] sync {phase} took {stopwatch.ElapsedMilliseconds} ms: uri='{uri}'");
        else
            logger.Debug($"[perf] sync {phase} took {stopwatch.ElapsedMilliseconds} ms: uri='{uri}'");
    }

    public void Dispose()
    {
        _scheduler?.Dispose();
    }

    internal static bool TryCreateDocumentUri(Uri? uri, out DocumentUri documentUri)
    {
        if (uri is { IsAbsoluteUri: true } && string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.LocalPath;

            // VS Code percent-encodes the Windows drive-letter colon
            // (file:///d%3A/...), which .NET surfaces as '/d:/...': a leading
            // slash that is not a valid fully-qualified Windows path. Strip it.
            if (OperatingSystem.IsWindows()
                && path.Length >= 3
                && path[0] == '/'
                && char.IsLetter(path[1])
                && path[2] == ':')
            {
                path = path.Substring(1);
            }

            try
            {
                path = Path.GetFullPath(path);
            }
            catch (Exception)
            {
                documentUri = default;
                return false;
            }

            return DocumentUri.TryCreate(path, out documentUri);
        }

        documentUri = default;
        return false;
    }

    internal static bool IsCvoloDocument(DocumentUri documentUri)
    {
        var extension = Path.GetExtension(documentUri.LocalPath);
        return string.Equals(extension, ".cvl", StringComparison.OrdinalIgnoreCase);
    }

    private static TextRange ToCoreRange(LspRange range)
    {
        return new TextRange(
            new TextPosition(range.Start.Line, range.Start.Character),
            new TextPosition(range.End.Line, range.End.Character));
    }
}
