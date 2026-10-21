using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Tests.TestSupport;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

/// <summary>
/// Verifies the diagnostic pipeline observability: a burst of edits is traceable from the request
/// through coalescing, worker start and analysis to exactly one commit or a discard with a machine
/// reason, publication is bracketed by attempt/sent, and a disabled observer records nothing while
/// the pipeline keeps working. Every emitted event must satisfy the kernel catalog.
/// </summary>
public class DiagnosticPipelineObservabilityTests
{
    private static readonly DocumentUri DocA = DocumentUri.Create(Path.Combine(Path.GetTempPath(), "obs-diag-a.cvl"));

    private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateObserver(ObservabilityMode mode)
    {
        var options = new ObservabilityOptions { Mode = mode, Emitter = "lsp-diag-test", QueueCapacity = 20_000 };
        var queue = new BoundedEventQueue(options.QueueCapacity);
        var observer = new CompilerObserver(
            options,
            identity: new ObservabilityIdentity("lsp-diag-test"),
            clock: new SystemObservabilityClock(),
            queue: queue,
            catalog: EventCatalog.CreateKernelCatalog());
        return (observer, queue);
    }

    [Fact]
    public async Task Burst_of_edits_is_traceable_from_request_to_commit()
    {
        var harness = new Harness();

        harness.Store.Open(DocA, "cvolo", 1, "initial");
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));

        // Several rapid edits: exactly one request becomes a worker run, the rest coalesce.
        for (int version = 2; version < 8; version++)
        {
            harness.Store.ApplyChanges(DocA, version, [Insert($"v{version}")]);
            harness.Scheduler.Schedule(project);
        }

        await WaitUntil(() => harness.Publisher.Publishes.Count >= 1, "diagnostics committed");

        var events = harness.Drain();
        Assert.NotEmpty(Named(events, "diagnostics.requested"));
        Assert.NotEmpty(Named(events, "diagnostics.coalesced"));
        Assert.NotEmpty(Named(events, "worker.queued"));
        Assert.NotEmpty(Named(events, "worker.started"));
        Assert.NotEmpty(Named(events, "analysis.started"));

        var completed = Assert.Single(Named(events, "analysis.completed"));
        Assert.Equal("success", completed.Event.Outcome);
        Assert.False(string.IsNullOrEmpty(completed.Event.AnalysisId));

        // The run is traced by one analysis id shared across its start/completion/commit.
        var started = Named(events, "analysis.started").Single();
        Assert.Equal(started.Event.AnalysisId, completed.Event.AnalysisId);
        var committed = Assert.Single(Named(events, "diagnostics.committed"));
        Assert.Equal(started.Event.AnalysisId, committed.Event.AnalysisId);

        AssertCatalogClean(harness.Observer);
    }

    [Fact]
    public async Task Publication_is_bracketed_by_attempt_and_sent_with_counts()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        var sink = new DiagnosticSink(new RecordingLspLogger());
        var publisher = new DiagnosticPublisher(new RecordingLspLogger(), sink, observer);

        var project = new FakeProject();
        var snapshot = new FakeSnapshot(new Dictionary<DocumentUri, string>(DocumentUriPathComparer.Instance), 0);
        var target = new DiagnosticPublishTarget(DocA, new DocumentSessionId(1), new DocumentVersion(1), new FakeHandle(DocA));
        var context = new DiagnosticRunContext(project, snapshot, [target]);
        var run = new BackendDiagnosticRun(
            new Dictionary<DocumentUri, string>(DocumentUriPathComparer.Instance) { [DocA] = "text" },
            [
                new BackendDiagnostic(
                    BackendDiagnosticSeverity.Error,
                    "CVL0001",
                    "message",
                    new BackendDiagnosticLocation(DocA, new TextSpan(0, 1), null),
                    []),
            ]);

        publisher.Publish(context, run);

        var events = Drain(queue);
        var attempt = Assert.Single(Named(events, "diagnostics.publish_attempt"));
        Assert.Equal("attempted", attempt.Event.Outcome);

        var sent = Assert.Single(Named(events, "diagnostics.publish_sent"));
        Assert.Equal("sent", sent.Event.Outcome);
        Assert.Equal("1", AttributeOf(sent, "diagnosticCount"));
        Assert.Equal("1", AttributeOf(sent, "documentCount"));

        Assert.Single(Named(events, "decision.diagnostics.publish"));
        AssertCatalogClean(observer);
    }

    [Fact]
    public async Task Failed_analysis_is_recorded_as_faulted_without_a_commit()
    {
        var harness = new Harness { FailDiagnostics = true };

        harness.Store.Open(DocA, "cvolo", 1, "initial");
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));
        harness.Scheduler.Schedule(project);

        await WaitUntil(() => harness.Backend.GetDiagnosticsCalls >= 1, "analysis attempted");

        var events = harness.Drain();
        var completed = Assert.Single(Named(events, "analysis.completed"));
        Assert.Equal("faulted", completed.Event.Outcome);
        Assert.Equal(nameof(InvalidOperationException), completed.Event.Reason);

        Assert.Empty(Named(events, "diagnostics.committed"));
        Assert.Empty(Named(events, "diagnostics.publish_attempt"));
        AssertCatalogClean(harness.Observer);
    }

    [Fact]
    public async Task Disabled_observability_records_nothing_while_the_pipeline_still_publishes()
    {
        var harness = new Harness(ObservabilityMode.Off);

        harness.Store.Open(DocA, "cvolo", 1, "initial");
        Assert.True(harness.Store.TryGetProject(DocA, out BackendProject? project));
        harness.Scheduler.Schedule(project);

        await WaitUntil(() => harness.Publisher.Publishes.Count >= 1, "diagnostics committed");

        Assert.Empty(harness.Drain());
    }

    private static void AssertCatalogClean(CompilerObserver observer)
    {
        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
        Assert.Equal(0, observer.Catalog!.UnknownReasons);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    private static IEnumerable<StampedEvent> Named(IEnumerable<StampedEvent> events, string name) =>
        events.Where(e => e.Event.Event == name);

    private static string? AttributeOf(StampedEvent stamped, string key) =>
        stamped.Event.Attributes?.FirstOrDefault(a => a.Key == key).Value;

    private static List<StampedEvent> Drain(BoundedEventQueue queue)
    {
        var events = new List<StampedEvent>();
        queue.DrainAll(events);
        return events;
    }

    private static DocumentChange Insert(string text) =>
        new(new TextRange(TextPosition.Zero, TextPosition.Zero), text);

    private static BackendDiagnosticRun EmptyRun(BackendSnapshot snapshot) =>
        new(new Dictionary<DocumentUri, string>(((FakeSnapshot)snapshot).Texts, DocumentUriPathComparer.Instance), []);

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
        public Harness(ObservabilityMode mode = ObservabilityMode.Detailed)
        {
            if (mode == ObservabilityMode.Off)
            {
                Observer = null;
            }
            else
            {
                var options = new ObservabilityOptions { Mode = mode, Emitter = "lsp-diag-test", QueueCapacity = 20_000 };
                Queue = new BoundedEventQueue(options.QueueCapacity);
                Observer = new CompilerObserver(
                    options,
                    identity: new ObservabilityIdentity("lsp-diag-test"),
                    clock: new SystemObservabilityClock(),
                    queue: Queue,
                    catalog: EventCatalog.CreateKernelCatalog());
            }

            Backend = new ControllableBackend();
            Store = new DocumentStore(Backend, new RecordingCoreLogger(), observer: Observer);
            Publisher = new RecordingPublisher();
            Logger = new RecordingLspLogger();
            Scheduler = new DiagnosticScheduler(Store, Publisher, Logger, Observer);
        }

        public CompilerObserver? Observer { get; }

        public BoundedEventQueue? Queue { get; }

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

        public List<StampedEvent> Drain()
        {
            var list = new List<StampedEvent>();
            Queue?.DrainAll(list);
            return list;
        }
    }

    private sealed class ControllableBackend : ILanguageBackend
    {
        private readonly object _gate = new();

        public FakeProject Project { get; } = new();

        public bool FailDiagnostics { get; set; }

        public int GetDiagnosticsCalls;

        public BackendProject? OpenProject(DocumentUri document) => Project;

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

        public BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle) =>
            UpdateDocument(project, handle, string.Empty);

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

            return EmptyRun(snapshot);
        }

        public BackendCompletionResult GetCompletions(BackendSnapshot snapshot, BackendDocumentHandle document, int position) =>
            new(new TextSpan(position, 0), []);

        public BackendSignatureHelpResult? GetSignatureHelp(BackendSnapshot snapshot, BackendDocumentHandle document, int position) => null;

        public BackendSymbolInfo? GetSymbolAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position) => null;

        public BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol) =>
            new(new Dictionary<DocumentUri, string>(), []);

        public IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(BackendSnapshot snapshot, BackendDocumentHandle document) => [];

        public BackendSemanticTokenResult GetSemanticTokens(BackendSnapshot snapshot, BackendDocumentHandle document) => new([]);
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
        public List<(DiagnosticRunContext Context, BackendDiagnosticRun Run)> Publishes { get; } = [];

        public void Publish(DiagnosticRunContext context, BackendDiagnosticRun run) => Publishes.Add((context, run));

        public void PublishEmpty(IReadOnlyList<DiagnosticPublishTarget> targets)
        {
        }

        public void PublishEmpty(DocumentUri uri)
        {
        }

        public void PublishUnavailable(IReadOnlyList<DiagnosticPublishTarget> targets)
        {
        }
    }
}
