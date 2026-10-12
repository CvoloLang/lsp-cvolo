using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Instrumentation;
using Cvolo.LanguageServer.Core.Logging;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Documents;

public class DocumentStoreTests
{
    private static readonly DocumentUri A = DocumentUri.Create(Path.Combine(Path.GetTempPath(), "a.cvl"));

    [Fact]
    public void Open_StoresExactEditorTextVersionAndSession()
    {
        var (store, _, _) = NewStore();
        var state = store.Open(A, "cvolo", 7, "int Main() { return 0; }");

        Assert.NotNull(state);
        Assert.Equal(A, state!.Uri);
        Assert.Equal(7, state.Version.Value);
        Assert.Equal("cvolo", state.LanguageId);
        Assert.Equal("int Main() { return 0; }", state.Text);
        Assert.True(store.TryGet(A, out var stored));
        Assert.Equal(state, stored);
    }

    [Fact]
    public void Open_Twice_DiscardsSecondOpen_KeepsOriginalState()
    {
        var (store, logger, _) = NewStore();
        var first = store.Open(A, "cvolo", 1, "original");
        var second = store.Open(A, "cvolo", 2, "replacement");

        Assert.Null(second);
        Assert.True(store.TryGet(A, out var stored));
        Assert.Equal(first, stored);
        Assert.Equal("original", stored!.Text);
        Assert.Equal(1, stored.Version.Value);
        Assert.True(logger.Has(CoreLogLevel.Warning, "Duplicate didOpen"));
    }

    [Fact]
    public void Open_WhenBackendFails_ReturnsNull_AndPublishesNothing()
    {
        var (store, logger, backend) = NewStore();
        backend.FailOpen = true;

        var state = store.Open(A, "cvolo", 1, "text");

        Assert.Null(state);
        Assert.False(store.TryGet(A, out _));
        Assert.True(logger.Has(CoreLogLevel.Warning, "No backend project"));
    }

    [Fact]
    public void Open_WhenBackendCannotResolve_ReturnsNull_AndPublishesNothing()
    {
        var (store, _, backend) = NewStore();
        backend.FailResolve = true;

        var state = store.Open(A, "cvolo", 1, "text");

        Assert.Null(state);
        Assert.False(store.TryGet(A, out _));
    }

    [Fact]
    public void ApplyChanges_Replacement_UpdatesTextVersionBackend()
    {
        var (store, _, backend) = NewStore();
        store.Open(A, "cvolo", 1, "hello world");

        var changed = store.ApplyChanges(A, 2, [new DocumentChange(Range(6, 0, 11, 0), "cvolo")]);

        Assert.NotNull(changed);
        Assert.Equal(2, changed!.Version.Value);
        Assert.Equal("hello cvolo", changed.Text);
        Assert.True(store.TryGet(A, out var stored));
        Assert.Equal("hello cvolo", stored!.Text);
        Assert.Equal(changed.BackendSnapshot, stored.BackendSnapshot);
        Assert.Equal("hello cvolo", backend.TextOf(A));
    }

    [Fact]
    public void ApplyChanges_MultiChange_AppliesSequentiallyToIntermediateText()
    {
        var (store, _, _) = NewStore();
        store.Open(A, "cvolo", 1, "abcdef");

        var changed = store.ApplyChanges(A, 2, [
            new DocumentChange(Range(1, 0, 2, 0), "1"),
            new DocumentChange(Range(3, 0, 4, 0), "2"),
        ]);

        Assert.Equal("a1c2ef", changed!.Text);
    }

    [Fact]
    public void ApplyChanges_FullReplacement_BeatsIncrementalAdvertising()
    {
        var (store, _, _) = NewStore();
        store.Open(A, "cvolo", 1, "old");

        var changed = store.ApplyChanges(A, 2, [new DocumentChange(null, "brand new")]);

        Assert.Equal("brand new", changed!.Text);
        Assert.True(store.TryGet(A, out var stored));
        Assert.Equal("brand new", stored!.Text);
    }

    [Fact]
    public void ApplyChanges_EqualToCurrentVersion_IsStale_AndDoesNotMutate()
    {
        var (store, logger, _) = NewStore();
        store.Open(A, "cvolo", 10, "v10");

        var result = store.ApplyChanges(A, 10, [new DocumentChange(Range(4, 0, 4, 0), "!")]);

        Assert.Null(result);
        Assert.True(store.TryGet(A, out var stored));
        Assert.Equal("v10", stored!.Text);
        Assert.Equal(10, stored.Version.Value);
        Assert.True(logger.Has(CoreLogLevel.Warning, "Stale didChange"));
    }

    [Fact]
    public void ApplyChanges_LowerVersion_IsStale_AndDoesNotMutate()
    {
        var (store, _, _) = NewStore();
        store.Open(A, "cvolo", 10, "v10");
        store.ApplyChanges(A, 11, [new DocumentChange(Range(0, 0, 0, 0), "X")]);

        var result = store.ApplyChanges(A, 9, [new DocumentChange(Range(0, 0, 0, 0), "Y")]);

        Assert.Null(result);
        Assert.True(store.TryGet(A, out var stored));
        Assert.Equal(11, stored!.Version.Value);
    }

    [Fact]
    public void ApplyChanges_ForUnopenedDocument_IsIgnored_NoImplicitOpen()
    {
        var (store, logger, _) = NewStore();
        var result = store.ApplyChanges(A, 1, [new DocumentChange(Range(0, 0, 0, 0), "Z")]);

        Assert.Null(result);
        Assert.True(logger.Has(CoreLogLevel.Warning, "didChange for unopened"));
        Assert.Empty(store.OpenUris);
    }

    [Fact]
    public void ApplyChanges_WithAnyInvalidRange_AppliesNothing_Transactionally()
    {
        var (store, _, _) = NewStore();
        store.Open(A, "cvolo", 1, "abc\n");

        var result = store.ApplyChanges(A, 2, [
            new DocumentChange(Range(0, 0, 0, 0), "OK"),
            new DocumentChange(Range(7, 0, 7, 0), "BOOM"),
        ]);

        Assert.Null(result);
        Assert.True(store.TryGet(A, out var stored));
        Assert.Equal(1, stored!.Version.Value);
        Assert.Equal("abc\n", stored.Text);

        var retry = store.ApplyChanges(A, 2, [new DocumentChange(Range(0, 0, 0, 0), "OK")]);
        Assert.NotNull(retry);
        Assert.Equal("OKabc\n", retry!.Text);
        Assert.Equal(2, retry.Version.Value);
    }

    [Fact]
    public void Close_RemovesState_RestoresBaseline()
    {
        var (store, logger, backend) = NewStore();
        store.Open(A, "cvolo", 1, "overlay text");
        store.ApplyChanges(A, 2, [new DocumentChange(Range(0, 0, 0, 0), "X")]);
        Assert.Equal("Xoverlay text", backend.TextOf(A));

        store.Close(A);

        Assert.False(store.TryGet(A, out _));
        Assert.Equal("overlay text", backend.TextOf(A));
        Assert.Equal(1, backend.RestoreCalls);
        Assert.True(logger.Has(CoreLogLevel.Info, "Closed"));
    }

    [Fact]
    public void Close_ForUnopenedDocument_IsTolerated_AndLogged()
    {
        var (store, logger, _) = NewStore();
        store.Close(A);
        Assert.True(logger.Has(CoreLogLevel.Warning, "didClose for unopened"));
        Assert.Empty(store.OpenUris);
    }

    [Fact]
    public void ReopenAfterClose_StartsNewSessionAndVersionOrdering()
    {
        var (store, _, _) = NewStore();
        var first = store.Open(A, "cvolo", 1, "one")!;
        store.Close(A);

        var second = store.Open(A, "cvolo", 1, "two")!;

        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal("two", second.Text);
        Assert.NotNull(store.ApplyChanges(A, 2, [new DocumentChange(Range(0, 0, 0, 0), ">")]));
    }

    [Fact]
    public async Task ConcurrentDistinctDocuments_StayCoherentWithBackend()
    {
        var (store, _, backend) = NewStore();
        for (var i = 0; i < 8; i++)
        {
            store.Open(At(i), "cvolo", 1, "seed");
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            for (var v = 2; v <= 20; v++)
            {
                store.ApplyChanges(At(i), v, [new DocumentChange(null, $"doc{i}-v{v}")]);
            }
        })));

        for (var i = 0; i < 8; i++)
        {
            Assert.True(store.TryGet(At(i), out var state));
            Assert.Equal($"doc{i}-v20", state!.Text);
            Assert.Equal(20, state.Version.Value);
            Assert.Equal($"doc{i}-v20", backend.TextOf(At(i)));
        }
    }

    [Fact]
    public void TryCapture_ReturnsCoherentStateAndBackendSnapshot()
    {
        var (store, _, backend) = NewStore();
        var opened = store.Open(A, "cvolo", 1, "v1 text")!;

        Assert.True(store.TryCapture(A, out var context));
        Assert.Equal(A, context.Uri);
        Assert.Equal(opened.SessionId, context.SessionId);
        Assert.Equal(new DocumentVersion(1), context.Version);
        Assert.Equal("v1 text", context.Document.Text);
        Assert.Same(context.CurrentProjectSnapshot, context.Document.BackendSnapshot);
        Assert.Equal("v1 text", backend.TextOf(A));
    }

    [Fact]
    public void TryCapture_ForUnopenedDocument_ReturnsFalse()
    {
        var (store, _, _) = NewStore();

        Assert.False(store.TryCapture(A, out _));
    }

    [Fact]
    public void TryCapture_IsCoherentHistoricalView_UnchangedByLaterEdits()
    {
        var (store, _, _) = NewStore();
        store.Open(A, "cvolo", 1, "root");

        Assert.True(store.TryCapture(A, out var captured));
        store.ApplyChanges(A, 2, [new DocumentChange(Range(0, 0, 0, 0), "!")]);

        Assert.Equal(1, captured.Version.Value);
        Assert.Equal("root", captured.Document.Text);
        Assert.Same(captured.CurrentProjectSnapshot, captured.Document.BackendSnapshot);

        Assert.True(store.TryCapture(A, out var current));
        Assert.Equal(2, current.Version.Value);
        Assert.NotEqual(captured.CurrentProjectSnapshot, current.CurrentProjectSnapshot);
    }

    private static DocumentUri At(int i)
    {
        return DocumentUri.Create(Path.Combine(Path.GetTempPath(), $"doc{i}.cvl"));
    }

    private static (DocumentStore Store, RecordingCoreLogger Logger, FakeBackend Backend) NewStore()
    {
        var logger = new RecordingCoreLogger();
        var backend = new FakeBackend();
        return (new DocumentStore(backend, logger), logger, backend);
    }

    private static TextRange Range(int startChar, int startLine, int endChar, int endLine)
    {
        return new TextRange(new TextPosition(startLine, startChar), new TextPosition(endLine, endChar));
    }

    [Fact]
    public void GetCompletions_ForwardsCapturedSnapshotAndPosition()
    {
        var (store, _, _) = NewStore();
        Assert.NotNull(store.Open(A, "cvolo", 1, "text"));

        Assert.True(store.TryCapture(A, out SemanticRequestContext context));
        BackendCompletionResult result = store.GetCompletions(context, 2);

        Assert.Equal(new TextSpan(2, 0), result.ReplacementSpan);
        Assert.True(store.IsCurrent(context));

        Assert.NotNull(store.ApplyChanges(A, 2, [new DocumentChange(null, "new text")]));
        Assert.False(store.IsCurrent(context));
    }

    [Fact]
    public void Metrics_RecordOpenChangeCloseSnapshotAndGateHold()
    {
        var metrics = new ServerMetrics();
        var store = new DocumentStore(new FakeBackend(), new RecordingCoreLogger(), metrics);

        Assert.NotNull(store.Open(A, "cvolo", 1, "text"));
        Assert.NotNull(store.ApplyChanges(A, 2, [new DocumentChange(null, "new text")]));
        Assert.True(store.TryCapture(A, out SemanticRequestContext context));
        store.GetCompletions(context, 2);
        Assert.NotNull(store.Close(A));

        Assert.Equal(1, metrics.DidOpen);
        Assert.Equal(1, metrics.DidChange);
        Assert.Equal(1, metrics.DidClose);
        Assert.Equal(1, metrics.SnapshotAdvances);
        Assert.Equal(5, metrics.PublicationGateHolds);
        Assert.Contains("didChange=1", metrics.FormatReport());
    }

    [Fact]
    public async Task Metrics_RecordInteractiveQuerySkippedWhenStale()
    {
        var metrics = new ServerMetrics();
        var store = new DocumentStore(new FakeBackend(), new RecordingCoreLogger(), metrics);
        Assert.NotNull(store.Open(A, "cvolo", 1, "text"));
        Assert.True(store.TryCapture(A, out SemanticRequestContext context));
        Assert.NotNull(store.ApplyChanges(A, 2, [new DocumentChange(null, "new text")]));

        int result = await store.RunInteractiveQueryAsync(context, "hover", () => 1, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(1, metrics.InteractiveQueriesSkippedStale);
        Assert.Equal(0, metrics.InteractiveQueriesRun);
    }

    private sealed class FakeBackend : ILanguageBackend
    {
        private readonly object _gate = new();
        private readonly Dictionary<DocumentUri, FakeProject> _projects = new();

        public bool FailOpen { get; set; }

        public bool FailResolve { get; set; }

        public int RestoreCalls { get; private set; }

        public BackendProject? OpenProject(DocumentUri document)
        {
            lock (_gate)
            {
                if (FailOpen)
                {
                    return null;
                }

                if (!_projects.TryGetValue(document, out var project))
                {
                    project = new FakeProject { CurrentSnapshot = new FakeSnapshot(string.Empty, 0) };
                    _projects[document] = project;
                }

                return project;
            }
        }

        public bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle)
        {
            handle = new FakeHandle(document);
            return !FailResolve;
        }

        public BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text)
        {
            lock (_gate)
            {
                var fake = (FakeProject)project;
                fake.Baseline ??= text;
                fake.Current = text;
                var snapshot = new FakeSnapshot(text, ++fake.Generation);
                fake.CurrentSnapshot = snapshot;
                return snapshot;
            }
        }

        public BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle)
        {
            lock (_gate)
            {
                RestoreCalls += 1;
                var fake = (FakeProject)project;
                fake.Current = fake.Baseline ?? string.Empty;
                var snapshot = new FakeSnapshot(fake.Current, ++fake.Generation);
                fake.CurrentSnapshot = snapshot;
                return snapshot;
            }
        }

        public BackendSnapshot CaptureCurrentSnapshot(BackendProject project)
        {
            lock (_gate)
            {
                var fake = (FakeProject)project;
                return fake.CurrentSnapshot ?? new FakeSnapshot(fake.Current, fake.Generation);
            }
        }

        public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
        {
            lock (_gate)
            {
                var fake = (FakeProject)project;
                return snapshot is FakeSnapshot current && current.Generation == fake.Generation;
            }
        }

        public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
        {
            return new BackendDiagnosticRun(new Dictionary<DocumentUri, string>(), []);
        }

        public BackendCompletionResult GetCompletions(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        {
            return new BackendCompletionResult(new TextSpan(position, 0), []);
        }

        public BackendSignatureHelpResult? GetSignatureHelp(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        {
            return null;
        }

        public BackendSymbolInfo? GetSymbolAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        {
            return null;
        }

        public BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol)
        {
            return new BackendDefinitionResult(new Dictionary<DocumentUri, string>(), []);
        }

        public IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(BackendSnapshot snapshot, BackendDocumentHandle document)
        {
            return [];
        }

        public BackendSemanticTokenResult GetSemanticTokens(BackendSnapshot snapshot, BackendDocumentHandle document)
        {
            return new BackendSemanticTokenResult([]);
        }

        public string? TextOf(DocumentUri document)
        {
            lock (_gate)
            {
                return _projects.TryGetValue(document, out var project) ? project.Current : null;
            }
        }
    }

    private sealed class FakeProject : BackendProject
    {
        public string? Baseline { get; set; }

        public string Current { get; set; } = string.Empty;

        public long Generation { get; set; }

        public FakeSnapshot? CurrentSnapshot { get; set; }
    }

    private sealed class FakeSnapshot(string text, long generation) : BackendSnapshot
    {
        public string Text { get; } = text;

        public long Generation { get; } = generation;
    }

    private sealed class FakeHandle(DocumentUri uri) : BackendDocumentHandle
    {
        public DocumentUri Uri { get; } = uri;
    }
}
