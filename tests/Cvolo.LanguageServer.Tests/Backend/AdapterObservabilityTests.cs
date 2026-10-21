using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Tests.TestSupport;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Backend;

/// <summary>
/// The Cvolo backend adapter records project discovery, package-session creation and snapshot
/// advancement. These events explain which project universe answered a request and how the
/// generation moved, without the handler having to guess.
/// </summary>
public sealed class AdapterObservabilityTests : IDisposable
{
    private const string Text = """
        public int Add(int left, int right) { return left + right; }

        int Main() { return Add(1, 2); }
        """;

    private readonly TestWorkspace _workspace;
    private readonly CompilerObserver _observer;
    private readonly BoundedEventQueue _queue;
    private readonly CvoloLanguageBackend _backend;

    public AdapterObservabilityTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"], _ => Text);
        (_observer, _queue) = CreateObserver(ObservabilityMode.Detailed);
        _backend = new CvoloLanguageBackend(
            [_workspace.DirectoryPath],
            null,
            new RecordingCoreLogger(),
            observer: _observer);
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private static (CompilerObserver, BoundedEventQueue) CreateObserver(ObservabilityMode mode)
    {
        var options = new ObservabilityOptions { Mode = mode, Emitter = "lsp-adapter-test", QueueCapacity = 20_000 };
        var queue = new BoundedEventQueue(options.QueueCapacity);
        var observer = new CompilerObserver(
            options,
            identity: new ObservabilityIdentity("lsp-adapter-test"),
            clock: new SystemObservabilityClock(),
            queue: queue,
            catalog: EventCatalog.CreateKernelCatalog());
        return (observer, queue);
    }

    private DocumentUri MainUri => DocumentUri.Create(_workspace.PathOf("main.cvl"));

    private List<StampedEvent> Drain()
    {
        var events = new List<StampedEvent>();
        _queue.DrainAll(events);
        return events;
    }

    private static List<StampedEvent> Named(IEnumerable<StampedEvent> events, string name) =>
        events.Where(e => e.Event.Event == name).ToList();

    private void AssertCatalogClean()
    {
        Assert.Equal(0, _observer.Catalog.UnknownEvents);
        Assert.Equal(0, _observer.Catalog.UnknownOutcomes);
        Assert.Equal(0, _observer.Catalog.UnknownReasons);
        Assert.Equal(0, _observer.Catalog.MissingFields);
    }

    [Fact]
    public void DiscoveringAProject_RecordsOneDiscoveredEvent()
    {
        BackendProject? project = _backend.OpenProject(MainUri);
        Assert.NotNull(project);

        List<StampedEvent> events = Drain();
        StampedEvent discovered = Assert.Single(Named(events, "project.discovered"));
        Assert.Equal("discovered", discovered.Event.Outcome);
        AssertCatalogClean();
    }

    [Fact]
    public void AdvancingASnapshot_RecordsAGenerationChange()
    {
        BackendProject project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = _backend.Resolve(project, MainUri);
        Drain();

        _backend.UpdateDocument(project, handle, Text + "\n");

        List<StampedEvent> events = Drain();
        StampedEvent changed = Assert.Single(Named(events, "generation.changed"));
        Assert.Equal("changed", changed.Event.Outcome);
        Assert.False(string.IsNullOrEmpty(changed.Event.GenerationId));
        AssertCatalogClean();
    }

    [Fact]
    public void ResolvingAnOpenDocument_RecordsResolved()
    {
        BackendProject project = _backend.OpenProject(MainUri)!;

        Assert.True(_backend.TryResolveDocument(project, MainUri, out _));

        List<StampedEvent> events = Drain();
        StampedEvent resolved = Assert.Single(Named(events, "document.resolve.completed"));
        Assert.Equal("resolved", resolved.Event.Outcome);
        AssertCatalogClean();
    }

    [Fact]
    public void Captured_snapshot_identity_matches_public_tooling_identity_when_available()
    {
        BackendProject project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = _backend.Resolve(project, MainUri);
        BackendSnapshot captured = _backend.UpdateDocument(project, handle, Text + "\n");
        var identityProperty = typeof(Cvolo.Compiler.Tooling.ProjectSnapshot)
            .GetProperty("SnapshotId",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        if (identityProperty is null)
        {
            // The pinned Tooling bundle predates the public API. It is incorrect
            // to manufacture an id from generation or the object address.
            Assert.Null(captured.SnapshotId);
            return;
        }

        Assert.IsType<ToolingBackendSnapshot>(captured);
        var tooling = ((ToolingBackendSnapshot)captured).Snapshot;
        var expected = Assert.IsType<string>(identityProperty.GetValue(tooling));
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, captured.SnapshotId);
        Assert.Equal(expected, _backend.CaptureCurrentSnapshot(project).SnapshotId);
        AssertCatalogClean();
    }

    [Fact]
    public void DisabledObservability_RecordsNothing_WhileDiscoveryStillWorks()
    {
        (CompilerObserver offObserver, BoundedEventQueue offQueue) = CreateObserver(ObservabilityMode.Off);
        var backend = new CvoloLanguageBackend(
            [_workspace.DirectoryPath],
            null,
            new RecordingCoreLogger(),
            observer: offObserver);

        BackendProject? project = backend.OpenProject(MainUri);
        Assert.NotNull(project);

        var events = new List<StampedEvent>();
        offQueue.DrainAll(events);
        Assert.Empty(events);
    }
}
