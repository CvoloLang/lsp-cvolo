using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Verifies that document synchronization is explainable from a trace: every accepted edit leaves an
/// applied record with the document version, every refused edit carries a machine reason, and a
/// successful synchronization schedules diagnostics as a separate, linked step. Tracing must never
/// change what the server does, and it must record nothing at all when observability is off.
/// </summary>
public class DocumentSyncObservabilityTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly RecordingLspLogger _logger;

    public DocumentSyncObservabilityTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl", "other.cvl"]);
        _logger = new RecordingLspLogger();
    }

    public void Dispose() => _workspace.Dispose();

    private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateObserver(ObservabilityMode mode)
    {
        var options = new ObservabilityOptions
        {
            Mode = mode,
            Emitter = "lsp-test",
            QueueCapacity = 20_000,
        };
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
        await session.Client.InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath)
            .WithTimeout("initialize");
        return session.Server.Store;
    }

    [Fact]
    public async Task DidOpen_records_applied_document_and_schedules_diagnostics()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        await StartAsync(session);
        Drain(queue); // boundary request/response events from initialize are not under test here

        await session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "int main() { return 0; }")
            .WithTimeout("didOpen");

        // RPC and worker events can arrive before didOpen finishes. Wait for the actual
        // didOpen outcome while retaining earlier events, rather than using queue size.
        var events = new List<StampedEvent>();
        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(events);
            return Named(events, "document.did_open").Any();
        }, "didOpen outcome");

        Assert.True(session.Server.Store.TryGet(DocumentUri.Create(_workspace.PathOf("main.cvl")), out var stored));
        Assert.Equal(1, stored!.Version.Value);
        var opened = Assert.Single(Named(events, "document.did_open"));
        Assert.Equal("applied", opened.Event.Outcome);
        Assert.Null(opened.Event.Reason);
        Assert.Equal(1, opened.Event.DocumentVersion);

        Assert.Contains(events, e => e.Event.Event == "diagnostics.scheduled" && e.Event.Outcome == "scheduled");
        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
        Assert.Equal(0, observer.Catalog!.UnknownReasons);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    [Fact]
    public async Task DidChange_records_received_then_applied_with_the_new_version()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        var store = await StartAsync(session);

        await session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "int main() { return 0; }")
            .WithTimeout("didOpen");

        // NotifyAsync finishes when the notification is written, not when the
        // ordered input worker has applied it. Observe the real didOpen outcome.
        var openedEvents = new List<StampedEvent>();
        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(openedEvents);
            return Named(openedEvents, "document.did_open").Any();
        }, "didOpen applied before didChange");
        var opened = Assert.Single(Named(openedEvents, "document.did_open"));
        Assert.Equal("applied", opened.Event.Outcome);
        Assert.Equal(1, opened.Event.DocumentVersion);

        await session.Client.NotifyDidChangeAsync(
                _workspace.DocumentUri("main.cvl"),
                2,
                new TextDocumentContentChangeEvent
                {
                    Range = new LspRange { Start = new Position { Line = 0, Character = 20 }, End = new Position { Line = 0, Character = 21 } },
                    Text = "1",
                })
            .WithTimeout("didChange");
        // Other RPC/worker events can satisfy queue.Count >= 3 before the edit
        // runs. Retain every drained event and wait for the edit's own terminal.
        var events = new List<StampedEvent>();
        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(events);
            return Named(events, "document.did_change.applied").Any()
                || Named(events, "document.did_change.rejected").Any();
        }, "didChange applied or rejected");

        Assert.Empty(Named(events, "document.did_change.rejected"));
        var received = Assert.Single(Named(events, "document.did_change.received"));
        Assert.Equal("received", received.Event.Outcome);
        Assert.Equal(2, received.Event.DocumentVersion);

        var applied = Assert.Single(Named(events, "document.did_change.applied"));
        Assert.Equal("applied", applied.Event.Outcome);
        Assert.Equal(2, applied.Event.DocumentVersion);
        Assert.True(store.TryGet(DocumentUri.Create(_workspace.PathOf("main.cvl")), out var state));
        Assert.Equal(2, state!.Version.Value);

        Assert.Contains(events, e => e.Event.Event == "diagnostics.scheduled");
    }

    [Fact]
    public async Task Non_cvolo_document_is_rejected_with_an_explicit_reason()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        await StartAsync(session);

        var textUri = new Uri(Path.Combine(_workspace.DirectoryPath, "notes.txt"));
        await session.Client.NotifyDidOpenAsync(textUri, "plaintext", 1, "hello").WithTimeout("didOpen");
        await session.Client.DrainNotificationsAsync().WithTimeout("drain");

        var events = Drain(queue);
        var rejected = Assert.Single(Named(events, "document.did_open"));
        Assert.Equal("rejected", rejected.Event.Outcome);
        Assert.Equal("unsupported_language", rejected.Event.Reason);
        Assert.DoesNotContain(events, e => e.Event.Event == "diagnostics.scheduled");
    }

    [Fact]
    public async Task Change_for_an_unopened_document_reports_store_not_applied()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        await StartAsync(session);
        Drain(queue);

        await session.Client.NotifyDidChangeAsync(
                _workspace.DocumentUri("other.cvl"),
                1,
                new TextDocumentContentChangeEvent { Text = "int main() { return 0; }" })
            .WithTimeout("didChange");
        await session.Client.WaitUntilAsync(() => queue.Count >= 2, "didChange events");

        var events = Drain(queue);
        var received = Assert.Single(Named(events, "document.did_change.received"));
        Assert.Equal("received", received.Event.Outcome);

        var rejected = Assert.Single(Named(events, "document.did_change.rejected"));
        Assert.Equal("rejected", rejected.Event.Outcome);
        Assert.Equal("store_not_applied", rejected.Event.Reason);
    }

    [Fact]
    public async Task Disabled_observability_records_nothing_while_sync_still_works()
    {
        var (observer, queue) = CreateObserver(ObservabilityMode.Off);
        using var session = ProtocolSession.Start(_logger, observer: observer);
        var store = await StartAsync(session);

        await session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, "int main() { return 0; }")
            .WithTimeout("didOpen");

        // Project discovery happens before didOpen settles; wait for the observable store state.
        var documentUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await session.Client.WaitUntilAsync(() => store.TryGet(documentUri, out _), "didOpen applied");

        Assert.True(store.TryGet(documentUri, out var state));
        Assert.Equal(1, state!.Version.Value);
        Assert.Empty(Drain(observer.Queue));
    }
}
