using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Tests.Documents;

/// <summary>
/// Verifies the DocumentStore update observability: every applied change is traceable from the
/// received event to the applied terminal with its document version, every rejected change carries
/// a machine reason that matches the real branch that refused it, disabling observability records
/// nothing while the store keeps working, and all emitted events satisfy the kernel catalog.
/// </summary>
public class DocumentStoreObservabilityTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly RecordingLspLogger _logger;

    public DocumentStoreObservabilityTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl", "other.cvl"]);
        _logger = new RecordingLspLogger();
    }

    public void Dispose() => _workspace.Dispose();

    private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateObserver(ObservabilityMode mode)
    {
        var options = new ObservabilityOptions { Mode = mode, Emitter = "lsp-test", QueueCapacity = 20_000 };
        var queue = new BoundedEventQueue(options.QueueCapacity);
        var observer = new CompilerObserver(
            options,
            identity: new ObservabilityIdentity("lsp-test"),
            clock: new SystemObservabilityClock(),
            queue: queue,
            catalog: EventCatalog.CreateKernelCatalog());
        return (observer, queue);
    }

    private static List<StampedEvent> Drain(BoundedEventQueue queue)
    {
        var list = new List<StampedEvent>();
        queue.DrainAll(list);
        return list;
    }

    private static IEnumerable<StampedEvent> Named(IEnumerable<StampedEvent> events, string name) =>
        events.Where(e => e.Event.Event == name);

    private async Task<DocumentStore> StartAsync(ProtocolSession session)
    {
        await session.Client
            .InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath)
            .WithTimeout("initialize");
        return session.Server.Store;
    }

    private async Task OpenMainAsync(ProtocolSession session)
    {
        await session.Client.NotifyDidOpenAsync(
            _workspace.DocumentUri("main.cvl"),
            "cvolo",
            1,
            "int main() { return 0; }");
    }

    private static TextDocumentContentChangeEvent Replace(
        int line, int startCharacter, int endCharacter, string text) =>
        new()
        {
            Range = new LspRange
            {
                Start = new Position { Line = line, Character = startCharacter },
                End = new Position { Line = line, Character = endCharacter },
            },
            Text = text,
        };

    [Fact]
    public async Task Applied_change_is_traceable_from_received_to_applied_with_its_version()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        await StartAsync(session);
        Drain(queue); // initialize boundary events are not part of this scenario
        await OpenMainAsync(session);
        await session.Client.WaitUntilAsync(() => queue.Count >= 2, "didOpen events");
        Drain(queue);

        await session.Client.NotifyDidChangeAsync(
            _workspace.DocumentUri("main.cvl"),
            2,
            Replace(0, 20, 21, "1"));
        await session.Client.WaitUntilAsync(() => queue.Count >= 3, "document update events");

        var events = Drain(queue);
        var received = Assert.Single(Named(events, "document.update.received"));
        Assert.Equal("received", received.Event.Outcome);
        Assert.Equal(2, received.Event.DocumentVersion);

        var applied = Assert.Single(Named(events, "document.update.applied"));
        Assert.Equal("applied", applied.Event.Outcome);
        Assert.Null(applied.Event.Reason);
        Assert.Equal(2, applied.Event.DocumentVersion);

        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
        Assert.Equal(0, observer.Catalog!.UnknownReasons);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    [Fact]
    public async Task Stale_change_is_rejected_with_stale_version()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        var store = await StartAsync(session);
        Drain(queue); // initialize boundary events are not part of this scenario

        var documentUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await OpenMainAsync(session);

        // A sent notification does not imply an applied didOpen. Wait for its
        // actual terminal before advancing the document.
        var openedEvents = new List<StampedEvent>();
        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(openedEvents);
            return Named(openedEvents, "document.did_open").Any();
        }, "didOpen terminal");
        var opened = Assert.Single(Named(openedEvents, "document.did_open"));
        Assert.Equal("applied", opened.Event.Outcome);
        Assert.True(store.TryGet(documentUri, out var initial));
        Assert.Equal(1, initial!.Version.Value);

        await session.Client.NotifyDidChangeAsync(
            _workspace.DocumentUri("main.cvl"),
            2,
            Replace(0, 20, 21, "1"));

        // Unrelated RPC and diagnostic events may fill the queue. A version-2
        // update must actually complete before version 1 can be called stale.
        var firstChangeEvents = new List<StampedEvent>();
        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(firstChangeEvents);
            return Named(firstChangeEvents, "document.update.applied").Any()
                || Named(firstChangeEvents, "document.update.rejected").Any();
        }, "first update terminal");
        Assert.Empty(Named(firstChangeEvents, "document.update.rejected"));
        var applied = Assert.Single(Named(firstChangeEvents, "document.update.applied"));
        Assert.Equal(2, applied.Event.DocumentVersion);
        Assert.True(store.TryGet(documentUri, out var advanced));
        Assert.Equal(2, advanced!.Version.Value);
        Assert.Contains("return 1;", advanced.Text, StringComparison.Ordinal);

        // An older version than the current one must be refused by the store,
        // with no text or version mutation and a truthful machine reason.
        Drain(queue);
        await session.Client.NotifyDidChangeAsync(
            _workspace.DocumentUri("main.cvl"),
            1,
            Replace(0, 20, 21, "2"));

        var staleEvents = new List<StampedEvent>();
        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(staleEvents);
            return Named(staleEvents, "document.update.rejected").Any();
        }, "stale change terminal");

        var rejected = Assert.Single(Named(staleEvents, "document.update.rejected"));
        Assert.Equal("rejected", rejected.Event.Outcome);
        Assert.Equal("stale_version", rejected.Event.Reason);
        Assert.Equal(1, rejected.Event.DocumentVersion);
        Assert.DoesNotContain(staleEvents, e =>
            e.Event.Event == "document.update.applied" && e.Event.DocumentVersion == 1);
        Assert.True(store.TryGet(documentUri, out var unchanged));
        Assert.Equal(2, unchanged!.Version.Value);
        Assert.Contains("return 1;", unchanged.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Out_of_range_change_is_rejected_with_invalid_change()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        await StartAsync(session);
        Drain(queue); // initialize boundary events are not part of this scenario
        await OpenMainAsync(session);
        await session.Client.WaitUntilAsync(() => queue.Count >= 2, "didOpen events");
        Drain(queue);

        // Replacing a character far beyond the end of the document cannot be applied.
        await session.Client.NotifyDidChangeAsync(
            _workspace.DocumentUri("main.cvl"),
            2,
            Replace(0, 200, 201, "x"));
        await session.Client.WaitUntilAsync(() => queue.Count >= 2, "invalid change rejection");

        var rejected = Assert.Single(Named(Drain(queue), "document.update.rejected"));
        Assert.Equal("rejected", rejected.Event.Outcome);
        Assert.Equal("invalid_change", rejected.Event.Reason);
    }

    [Fact]
    public async Task Change_for_a_document_that_was_never_opened_is_rejected_with_document_not_open()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        var store = await StartAsync(session);
        Drain(queue);

        // The sync handler refuses this before the store, so this exercises the store contract directly.
        var result = store.ApplyChanges(
            DocumentUri.Create(_workspace.PathOf("other.cvl")),
            1,
            Array.Empty<DocumentChange>());

        Assert.Null(result);
        var rejected = Assert.Single(Named(Drain(queue), "document.update.rejected"));
        Assert.Equal("rejected", rejected.Event.Outcome);
        Assert.Equal("document_not_open", rejected.Event.Reason);
        Assert.Equal(1, rejected.Event.DocumentVersion);
    }

    [Fact]
    public async Task Disabled_observability_records_nothing_while_the_store_still_applies_changes()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Off);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        var store = await StartAsync(session);

        var documentUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await OpenMainAsync(session);
        await session.Client.WaitUntilAsync(() => store.TryGet(documentUri, out _), "didOpen applied");
        await session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("main.cvl"), 2, Replace(0, 20, 21, "1"));
        await session.Client.WaitUntilAsync(
            () => store.TryGet(documentUri, out var state) && state.Version.Value == 2,
            "didChange applied");

        Assert.True(store.TryGet(documentUri, out var current));
        Assert.Equal(2, current.Version.Value);
        Assert.Contains("return 1;", current.Text, StringComparison.Ordinal);
        Assert.Empty(Drain(observer.Queue));
    }

    [Fact]
    public async Task Semantic_request_capture_is_traced_as_a_paired_span()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        var store = await StartAsync(session);
        Drain(queue); // initialize boundary events are not part of this scenario
        await OpenMainAsync(session);
        await session.Client.WaitUntilAsync(() => store.TryGet(DocumentUri.Create(_workspace.PathOf("main.cvl")), out _), "didOpen applied");
        Drain(queue);

        var documentUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        Assert.True(store.TryCapture(documentUri, out _));

        var events = Drain(queue);
        var started = Assert.Single(Named(events, "snapshot.capture.started"));
        var completed = Assert.Single(Named(events, "snapshot.capture.completed"));

        // The span halves must be correlated by the operation id and carry the capture kind.
        Assert.False(string.IsNullOrEmpty(started.Event.OperationId));
        Assert.Equal(started.Event.OperationId, completed.Event.OperationId);
        Assert.Equal("success", completed.Event.Outcome);
        Assert.Equal("request", AttributeOf(started, "captureKind"));
        Assert.Null(started.Event.DocumentVersion); // unknown until capture; never synthetic zero
        Assert.Null(completed.Event.DocumentVersion); // completion retains the opened span's fields
        Assert.Equal("1", AttributeOf(completed, "documentVersion")); // actual captured version

        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    [Fact]
    public async Task Capture_of_an_unopened_document_completes_with_not_found()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        var store = await StartAsync(session);
        Drain(queue);

        var otherUri = DocumentUri.Create(_workspace.PathOf("other.cvl"));
        Assert.False(store.TryCapture(otherUri, out _));

        var events = Drain(queue);
        var started = Assert.Single(Named(events, "snapshot.capture.started"));
        var completed = Assert.Single(Named(events, "snapshot.capture.completed"));
        Assert.Equal("not_found", completed.Event.Outcome);
        Assert.Equal("request", AttributeOf(completed, "captureKind"));
        Assert.Null(started.Event.DocumentVersion);
        Assert.Null(completed.Event.DocumentVersion);
        Assert.Null(AttributeOf(completed, "documentVersion"));
    }

    [Fact]
    public async Task Diagnostic_run_capture_does_not_fabricate_a_single_document_version()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        var store = await StartAsync(session);
        var documentUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await OpenMainAsync(session);
        await session.Client.WaitUntilAsync(
            () => store.TryGet(documentUri, out _), "didOpen applied");
        Assert.True(store.TryGetProject(documentUri, out var project));
        Drain(queue);

        Assert.True(store.TryCaptureDiagnosticRun(project, out var capture));
        Assert.NotEmpty(capture.Targets);

        var events = Drain(queue);
        var started = Assert.Single(Named(events, "snapshot.capture.started"));
        var completed = Assert.Single(Named(events, "snapshot.capture.completed"));
        Assert.Equal("diagnostic_run", AttributeOf(started, "captureKind"));
        Assert.Equal("success", completed.Event.Outcome);
        Assert.Equal(started.Event.OperationId, completed.Event.OperationId);
        Assert.Null(started.Event.DocumentVersion);
        Assert.Null(completed.Event.DocumentVersion);
        Assert.Null(AttributeOf(completed, "documentVersion"));
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    [Fact]
    public async Task Freshness_check_of_a_current_context_reports_fresh()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        var store = await StartAsync(session);
        Drain(queue);
        await OpenMainAsync(session);
        await session.Client.WaitUntilAsync(() => store.TryGet(DocumentUri.Create(_workspace.PathOf("main.cvl")), out _), "didOpen applied");

        var documentUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        Assert.True(store.TryCapture(documentUri, out SemanticRequestContext context));
        Drain(queue);

        Assert.True(store.IsCurrent(context));

        var checkedEvent = Assert.Single(Named(Drain(queue), "snapshot.freshness_checked"));
        Assert.Equal("fresh", checkedEvent.Event.Outcome);
    }

    [Fact]
    public async Task Freshness_check_after_the_document_advances_reports_stale()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        var store = await StartAsync(session);
        Drain(queue);
        await OpenMainAsync(session);
        await session.Client.WaitUntilAsync(() => store.TryGet(DocumentUri.Create(_workspace.PathOf("main.cvl")), out _), "didOpen applied");

        var documentUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        Assert.True(store.TryCapture(documentUri, out SemanticRequestContext context));

        // Advance the document so the captured context is no longer the live one.
        await session.Client.NotifyDidChangeAsync(_workspace.DocumentUri("main.cvl"), 2, Replace(0, 20, 21, "1"));
        await session.Client.WaitUntilAsync(
            () => store.TryGet(documentUri, out var state) && state.Version.Value == 2,
            "didChange applied");

        // Exercise the single-context and single-target overloads; both must report stale.
        Assert.False(store.IsCurrent(context));
        Assert.False(store.IsCurrent(context.Uri, context.SessionId, context.Version, context.Project));

        var checkedEvents = Named(Drain(queue), "snapshot.freshness_checked").ToList();
        Assert.Equal(2, checkedEvents.Count);
        Assert.All(checkedEvents, e => Assert.Equal("stale", e.Event.Outcome));
    }

    private static string? AttributeOf(StampedEvent stamped, string key) =>
        stamped.Event.Attributes?.FirstOrDefault(a => a.Key == key).Value;
}
