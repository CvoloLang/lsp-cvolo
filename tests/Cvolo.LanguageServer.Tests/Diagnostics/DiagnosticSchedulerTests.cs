using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

public class DiagnosticSchedulerTests
{
    private static readonly DocumentUri DocA = DocumentUri.Create(Path.Combine(Path.GetTempPath(), "sched-a.cvl"));
    private static readonly DocumentUri DocB = DocumentUri.Create(Path.Combine(Path.GetTempPath(), "sched-b.cvl"));

    [Fact]
    public async Task RapidEdits_CoalesceIntoOneDiagnosticAnalysis()
    {
        var harness = new Harness();
        Assert.NotNull(harness.Store.Open(DocA, "cvolo", 1, "initial"));
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        for (int version = 2; version < 15; version++)
        {
            Assert.NotNull(harness.Store.ApplyChanges(DocA, version,
                [new DocumentChange(null, $"version-{version}")]));
            harness.Scheduler.Schedule(project);
        }

        await WaitUntil(() => Volatile.Read(ref harness.Backend.GetDiagnosticsCalls) > 0,
            "debounced diagnostics");
        await Task.Delay(400);

        Assert.Equal(1, Volatile.Read(ref harness.Backend.GetDiagnosticsCalls));
        var last = Assert.Single(harness.Publisher.Publishes);
        Assert.Equal("version-14", last.Run.DocumentTexts[DocA]);
    }

    [Fact]
    public async Task StaleGeneration_PublishesNothing_NewerGenerationPublishes()
    {
        var harness = new Harness();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        int calls = 0;
        harness.Backend.OnGetDiagnostics = (snapshot, targets) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                release.Task.Wait();
                return RunWithDiagnostic(snapshot, "stale");
            }

            return EmptyRun(snapshot);
        };

        Assert.NotNull(harness.Store.Open(DocA, "cvolo", 1, "first"));
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        harness.Scheduler.Schedule(project);
        await entered.Task.WithTimeout("first analysis entered");

        harness.Store.ApplyChanges(DocA, 2, [Insert("x")]);
        harness.Scheduler.Schedule(project);
        release.SetResult();

        await WaitUntil(() => harness.Publisher.Publishes.Count >= 1, "newer generation published");

        Assert.Single(harness.Publisher.Publishes);
        Assert.DoesNotContain(harness.Publisher.Publishes, p => p.Run.Diagnostics.Any(d => d.Code == "stale"));
    }

    [Fact]
    public async Task ConcurrentChanges_FinalSnapshotWins_NoOlderResultPublishes()
    {
        var harness = new Harness();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var calls = 0;
        harness.Backend.OnGetDiagnostics = (snapshot, targets) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                release.Task.Wait();
            }

            return EmptyRun(snapshot);
        };

        harness.Store.Open(DocA, "cvolo", 1, "first");
        harness.Store.Open(DocB, "cvolo", 1, "second");
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        harness.Scheduler.Schedule(project);
        await entered.Task.WithTimeout("first analysis entered");

        harness.Store.ApplyChanges(DocA, 2, [Insert("x")]);
        harness.Scheduler.Schedule(project);
        release.SetResult();

        await WaitUntil(() => harness.Publisher.Publishes.Count >= 1, "final publish");
        await Task.Delay(50);

        var publish = Assert.Single(harness.Publisher.Publishes);
        var snapshot = (FakeSnapshot)publish.Context.CurrentProjectSnapshot;
        Assert.Equal(harness.Backend.Project.Generation, snapshot.Generation);
        Assert.Equal("xfirst", publish.Run.DocumentTexts[DocA]);
        Assert.Equal("second", publish.Run.DocumentTexts[DocB]);
    }

    [Fact]
    public async Task ClosedAndReopened_OldSessionResultNeverPublishes()
    {
        var harness = new Harness();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var calls = 0;
        harness.Backend.OnGetDiagnostics = (snapshot, targets) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                release.Task.Wait();
                return RunWithDiagnostic(snapshot, "old-session");
            }

            return EmptyRun(snapshot);
        };

        Assert.NotNull(harness.Store.Open(DocA, "cvolo", 1, "first"));
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        harness.Scheduler.Schedule(project);
        await entered.Task.WithTimeout("first analysis entered");

        harness.Store.Close(DocA);
        Assert.NotNull(harness.Store.Open(DocA, "cvolo", 1, "reopened"));
        release.SetResult();

        harness.Scheduler.Schedule(project);
        await WaitUntil(() => harness.Publisher.Publishes.Count >= 1, "reopened publish");
        await Task.Delay(50);

        Assert.DoesNotContain(harness.Publisher.Publishes, p => p.Run.Diagnostics.Any(d => d.Code == "old-session"));
        var publish = Assert.Single(harness.Publisher.Publishes);
        Assert.Equal("reopened", publish.Run.DocumentTexts[DocA]);
    }

    [Fact]
    public async Task ThreeConsecutiveFailures_PublishUnavailableStateWithWarning()
    {
        var harness = new Harness { FailDiagnostics = true };
        harness.Store.Open(DocA, "cvolo", 1, "text");
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        harness.Scheduler.Schedule(project);
        await WaitUntil(() => harness.Backend.GetDiagnosticsCalls == 1, "failure 1");
        harness.Scheduler.Schedule(project);
        await WaitUntil(() => harness.Backend.GetDiagnosticsCalls == 2, "failure 2");

        Assert.Equal(0, harness.Publisher.UnavailableCalls);
        Assert.False(harness.Scheduler.IsUnavailable(project));

        harness.Scheduler.Schedule(project);
        await WaitUntil(() => harness.Publisher.UnavailableCalls >= 1, "unavailable publication");

        // The failure is reported as unavailability, never as a certified-clean empty set.
        Assert.Equal(0, harness.Publisher.EmptyCalls);
        Assert.True(harness.Scheduler.IsUnavailable(project));
        Assert.True(harness.Logger.Has("Warning", "consecutively"));
    }

    [Fact]
    public async Task SuccessResetsConsecutiveFailureCount()
    {
        var harness = new Harness { FailDiagnostics = true };
        harness.Store.Open(DocA, "cvolo", 1, "text");
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        await FailAsync(harness, project, 1);
        await FailAsync(harness, project, 2);

        harness.FailDiagnostics = false;
        harness.Scheduler.Schedule(project);
        await WaitUntil(() => harness.Backend.GetDiagnosticsCalls == 3, "success");
        await WaitUntil(() => !harness.Scheduler.IsUnavailable(project), "availability restored");

        harness.FailDiagnostics = true;
        await FailAsync(harness, project, 4);
        await FailAsync(harness, project, 5);

        Assert.Equal(0, harness.Publisher.UnavailableCalls);

        await FailAsync(harness, project, 6);
        await WaitUntil(() => harness.Publisher.UnavailableCalls >= 1, "recovery after reset");
    }

    [Fact]
    public async Task ClosingLastDocument_ResetsFailureCount()
    {
        var harness = new Harness { FailDiagnostics = true };
        harness.Store.Open(DocA, "cvolo", 1, "text");
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        await FailAsync(harness, project, 1);
        await FailAsync(harness, project, 2);

        harness.Store.Close(DocA);
        harness.Scheduler.OnProjectDrained(project);

        harness.Store.Open(DocA, "cvolo", 1, "text");
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? reopened));
        await FailAsync(harness, reopened, 3);
        await FailAsync(harness, reopened, 4);

        Assert.Equal(0, harness.Publisher.UnavailableCalls);

        await FailAsync(harness, reopened, 5);
        await WaitUntil(() => harness.Publisher.UnavailableCalls >= 1, "recovery after reopen");
    }

    [Fact]
    public async Task StaleFailedGeneration_DoesNotClearAfterNewerCurrent()
    {
        var harness = new Harness();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var calls = 0;
        harness.Backend.OnGetDiagnostics = (snapshot, targets) =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n <= 2)
            {
                throw new InvalidOperationException("boom");
            }

            if (n == 3)
            {
                entered.SetResult();
                release.Task.Wait();
                throw new InvalidOperationException("boom");
            }

            return EmptyRun(snapshot);
        };

        harness.Store.Open(DocA, "cvolo", 1, "text");
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        harness.Scheduler.Schedule(project);
        await WaitUntil(() => calls == 1, "failure 1");
        harness.Scheduler.Schedule(project);
        await WaitUntil(() => calls == 2, "failure 2");
        harness.Scheduler.Schedule(project);
        await entered.Task.WithTimeout("third analysis entered");

        harness.Store.ApplyChanges(DocA, 2, [Insert("y")]);
        release.SetResult();

        harness.Scheduler.Schedule(project);
        await WaitUntil(() => harness.Publisher.Publishes.Count >= 1, "recovered publish");

        Assert.Equal(0, harness.Publisher.EmptyCalls);
        Assert.Equal(0, harness.Publisher.UnavailableCalls);
    }

    [Fact]
    public async Task DocumentAdvancement_CannotInterleaveBetweenValidationAndPublication()
    {
        var harness = new Harness();
        Assert.NotNull(harness.Store.Open(DocA, "cvolo", 1, "first"));
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        var publishEntered = new TaskCompletionSource();
        var publishRelease = new TaskCompletionSource();
        harness.Publisher.PublishEntered = publishEntered;
        harness.Publisher.PublishRelease = publishRelease;

        harness.Scheduler.Schedule(project);
        await publishEntered.Task.WithTimeout("publication entered");

        Task<DocumentState?> change = Task.Run(() => harness.Store.ApplyChanges(DocA, 2, [Insert("x")]));
        await Task.Delay(150);
        Assert.False(change.IsCompleted);

        publishRelease.SetResult();
        await change.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(harness.Store.TryGet(DocA, out DocumentState state));
        Assert.Equal(2, state.Version.Value);
    }

    [Fact]
    public async Task Close_CannotInterleaveBetweenValidationAndPublication()
    {
        var harness = new Harness();
        Assert.NotNull(harness.Store.Open(DocA, "cvolo", 1, "first"));
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        var publishEntered = new TaskCompletionSource();
        var publishRelease = new TaskCompletionSource();
        harness.Publisher.PublishEntered = publishEntered;
        harness.Publisher.PublishRelease = publishRelease;

        harness.Scheduler.Schedule(project);
        await publishEntered.Task.WithTimeout("publication entered");

        Task<BackendProject?> close = Task.Run(() => harness.Store.Close(DocA));
        await Task.Delay(150);
        Assert.False(close.IsCompleted);

        publishRelease.SetResult();
        await close.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(harness.Store.TryGet(DocA, out _));
    }

    [Fact]
    public async Task SuccessfulButSupersededAnalysis_ResetsFailureCount()
    {
        var harness = new Harness();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var calls = 0;
        harness.Backend.OnGetDiagnostics = (snapshot, targets) =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n <= 2)
            {
                throw new InvalidOperationException("boom");
            }

            if (n == 3)
            {
                entered.SetResult();
                release.Task.Wait();
            }

            return EmptyRun(snapshot);
        };

        harness.Store.Open(DocA, "cvolo", 1, "text");
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        harness.Scheduler.Schedule(project);
        await WaitUntil(() => calls == 1, "failure 1");
        harness.Scheduler.Schedule(project);
        await WaitUntil(() => calls == 2, "failure 2");

        harness.Scheduler.Schedule(project);
        await entered.Task.WithTimeout("third analysis entered");

        harness.Store.ApplyChanges(DocA, 2, [Insert("y")]);
        release.SetResult();
        await WaitUntil(() => calls >= 3, "successful analysis completed");

        harness.Backend.FailDiagnostics = true;
        await FailAsync(harness, project, 4);
        await FailAsync(harness, project, 5);

        Assert.Equal(0, harness.Publisher.UnavailableCalls);

        await FailAsync(harness, project, 6);
        await WaitUntil(() => harness.Publisher.UnavailableCalls >= 1, "recovery after reset");
    }

    private static async Task FailAsync(Harness harness, BackendProject project, int expectedCalls)
    {
        harness.Scheduler.Schedule(project);
        await WaitUntil(() => harness.Backend.GetDiagnosticsCalls == expectedCalls, $"failure {expectedCalls}");
    }

    private static DocumentChange Insert(string text)
    {
        return new DocumentChange(new TextRange(TextPosition.Zero, TextPosition.Zero), text);
    }

    private static BackendDiagnosticRun EmptyRun(BackendSnapshot snapshot)
    {
        return new BackendDiagnosticRun(new Dictionary<DocumentUri, string>(((FakeSnapshot)snapshot).Texts, DocumentUriPathComparer.Instance), []);
    }

    private static BackendDiagnosticRun RunWithDiagnostic(BackendSnapshot snapshot, string code)
    {
        return new BackendDiagnosticRun(
            new Dictionary<DocumentUri, string>(((FakeSnapshot)snapshot).Texts, DocumentUriPathComparer.Instance),
            [
                new BackendDiagnostic(
                    BackendDiagnosticSeverity.Error,
                    code,
                    "message",
                    new BackendDiagnosticLocation(DocA, new TextSpan(0, 1), null),
                    []),
            ]);
    }

    private static async Task WaitUntil(Func<bool> condition, string label, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {label}");
            }

            await Task.Delay(10);
        }
    }

    private sealed class Harness
    {
        public Harness()
        {
            Backend = new ControllableBackend();
            Store = new DocumentStore(Backend, new RecordingCoreLogger());
            Publisher = new RecordingPublisher();
            Logger = new RecordingLspLogger();
            Scheduler = new DiagnosticScheduler(Store, Publisher, Logger);
        }

        public ControllableBackend Backend { get; }

        public DocumentStore Store { get; }

        public RecordingPublisher Publisher { get; }

        public RecordingLspLogger Logger { get; }

        public DiagnosticScheduler Scheduler { get; }

        public bool FailDiagnostics
        {
            get => Backend.FailDiagnostics;
            set => Backend.FailDiagnostics = value;
        }
    }

    private sealed class ControllableBackend : ILanguageBackend
    {
        private readonly object _gate = new();

        public FakeProject Project { get; } = new();

        public Func<BackendSnapshot, IReadOnlyList<BackendDocumentHandle>, BackendDiagnosticRun>? OnGetDiagnostics { get; set; }

        public bool FailDiagnostics { get; set; }

        public int GetDiagnosticsCalls;

        public BackendProject? OpenProject(DocumentUri document)
        {
            return Project;
        }

        public bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle)
        {
            handle = new FakeHandle(document);
            return true;
        }

        public BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text)
        {
            lock (_gate)
            {
                var texts = new Dictionary<DocumentUri, string>(Project.Current.Texts, DocumentUriPathComparer.Instance)
                {
                    [((FakeHandle)handle).Uri] = text,
                };
                Project.Generation++;
                Project.Current = new FakeSnapshot(texts, Project.Generation);
                return Project.Current;
            }
        }

        public BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle)
        {
            return UpdateDocument(project, handle, string.Empty);
        }

        public BackendSnapshot CaptureCurrentSnapshot(BackendProject project)
        {
            lock (_gate)
            {
                return Project.Current;
            }
        }

        public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
        {
            lock (_gate)
            {
                return ((FakeSnapshot)snapshot).Generation == Project.Generation;
            }
        }

        public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
        {
            Interlocked.Increment(ref GetDiagnosticsCalls);
            if (FailDiagnostics)
            {
                throw new InvalidOperationException("simulated analysis failure");
            }

            return OnGetDiagnostics?.Invoke(snapshot, targets) ?? EmptyRun(snapshot);
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
    }

    private sealed class FakeProject : BackendProject
    {
        public long Generation { get; set; }

        public FakeSnapshot Current { get; set; } = new(new Dictionary<DocumentUri, string>(), 0);
    }

    private sealed class FakeSnapshot(IReadOnlyDictionary<DocumentUri, string> texts, long generation) : BackendSnapshot
    {
        public IReadOnlyDictionary<DocumentUri, string> Texts { get; } = texts;

        public long Generation { get; } = generation;
    }

    private sealed class FakeHandle(DocumentUri uri) : BackendDocumentHandle
    {
        public DocumentUri Uri { get; } = uri;
    }

    private sealed class RecordingPublisher : IDiagnosticPublisher
    {
        private readonly object _lock = new();

        public List<(DiagnosticRunContext Context, BackendDiagnosticRun Run)> Publishes { get; } = [];

        public List<DocumentUri> Empties { get; } = [];

        public List<DocumentUri> Unavailables { get; } = [];

        public TaskCompletionSource? PublishEntered { get; set; }

        public TaskCompletionSource? PublishRelease { get; set; }

        public int EmptyCalls
        {
            get
            {
                lock (_lock)
                {
                    return Empties.Count;
                }
            }
        }

        public int UnavailableCalls
        {
            get
            {
                lock (_lock)
                {
                    return Unavailables.Count;
                }
            }
        }

        public void Publish(DiagnosticRunContext context, BackendDiagnosticRun run)
        {
            PublishEntered?.TrySetResult();
            PublishRelease?.Task.Wait();
            lock (_lock)
            {
                Publishes.Add((context, run));
            }
        }

        public void PublishEmpty(IReadOnlyList<DiagnosticPublishTarget> targets)
        {
            lock (_lock)
            {
                foreach (DiagnosticPublishTarget target in targets)
                {
                    Empties.Add(target.Uri);
                }
            }
        }

        public void PublishEmpty(DocumentUri uri)
        {
            lock (_lock)
            {
                Empties.Add(uri);
            }
        }

        public void PublishUnavailable(IReadOnlyList<DiagnosticPublishTarget> targets)
        {
            lock (_lock)
            {
                foreach (DiagnosticPublishTarget target in targets)
                {
                    Unavailables.Add(target.Uri);
                }
            }
        }
    }
}
