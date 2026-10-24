using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Verifies the F12 (textDocument/definition) observability: a successful navigation is
/// traced through position mapping, symbol lookup, target resolution, span mapping and the
/// response, while every dropped request records a distinct terminal outcome and reason so a
/// failed lookup is never indistinguishable from a legitimately absent symbol (§19.1, §20.3,
/// §45.4 OBS-LSP-001/002/003). A disabled observer records nothing while navigation still works.
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class DefinitionObservabilityTests : IDisposable
{
    private const string Text = "int main() { return 0; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly CompilerObserver _observer;
    private readonly BoundedEventQueue _queue;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public DefinitionObservabilityTests()
    {
        _workspace = TestWorkspace.CreateProject(["a.cvl", "b.cvl"]);
        _backend = new BlockingBackend();
        (_observer, _queue) = CreateObserver(ObservabilityMode.Detailed);
        _store = new DocumentStore(_backend, new RecordingCoreLogger(), observer: _observer);
        _session = ProtocolSession.Start(storeFactory: () => _store, observer: _observer);
    }

    private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateObserver(ObservabilityMode mode)
    {
        var options = new ObservabilityOptions { Mode = mode, Emitter = "lsp-def-test", QueueCapacity = 20_000 };
        var queue = new BoundedEventQueue(options.QueueCapacity);
        var observer = new CompilerObserver(
            options,
            identity: new ObservabilityIdentity("lsp-def-test"),
            clock: new SystemObservabilityClock(),
            queue: queue,
            catalog: EventCatalog.CreateKernelCatalog());
        return (observer, queue);
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private Uri Uri(string name) => _workspace.DocumentUri(name);

    private static BackendSymbolInfo Symbol() =>
        new(new FakeSymbolHandle(), new TextSpan(0, 3), "main", BackendSymbolKind.Function, "int main()");

    private async Task StartAsync()
    {
        await _session.Client.InitializeAsync().WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");

        // Boundary and lifecycle events are emitted before the request under test; drop them.
        _queue.DrainAll(new List<StampedEvent>());
    }

    private async Task OpenAsync(string name)
    {
        await _session.Client.NotifyDidOpenAsync(Uri(name), "cvolo", 1, Text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf(name));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
        _queue.DrainAll(new List<StampedEvent>());
    }

    private List<StampedEvent> Drain()
    {
        var events = new List<StampedEvent>();
        _queue.DrainAll(events);
        return events;
    }

    private static IEnumerable<StampedEvent> Named(IEnumerable<StampedEvent> events, string name) =>
        events.Where(e => e.Event.Event == name);

    private void AssertCatalogClean()
    {
        Assert.Equal(0, _observer.Catalog!.UnknownEvents);
        Assert.Equal(0, _observer.Catalog!.UnknownOutcomes);
        Assert.Equal(0, _observer.Catalog!.UnknownReasons);
        Assert.Equal(0, _observer.Catalog!.MissingFields);
    }

    [Theory]
    [InlineData("hover")]
    [InlineData("completion")]
    [InlineData("signatureHelp")]
    [InlineData("references")]
    public async Task Closed_document_feature_requests_have_distinct_terminal_traces(string feature)
    {
        await StartAsync();
        // No didOpen: each feature must terminate with a machine-readable
        // reason rather than a null RPC response without observability.
        Uri uri = Uri("a.cvl");
        switch (feature)
        {
            case "hover":
                Assert.Null(await _session.Client.HoverAsync(uri, 0, 4).WithTimeout("hover"));
                break;
            case "completion":
                var completion = await _session.Client.CompletionAsync(uri, 0, 4).WithTimeout("completion");
                Assert.True(completion is null || completion.Items is { Length: 0 });
                break;
            case "signatureHelp":
                Assert.Null(await _session.Client.SignatureHelpAsync(uri, 0, 4).WithTimeout("signatureHelp"));
                break;
            case "references":
                Assert.Null(await _session.Client.ReferencesAsync(uri, 0, 4).WithTimeout("references"));
                break;
        }

        var events = Drain();
        var started = Assert.Single(Named(events, "feature.request.started")
            .Where(e => e.Event.Stage == feature));
        var completed = Assert.Single(Named(events, "feature.request.completed")
            .Where(e => e.Event.Stage == feature));
        Assert.Equal(started.Event.OperationId, completed.Event.OperationId);
        Assert.Equal("not_found", completed.Event.Outcome);
        Assert.Equal("document_not_open", completed.Event.Reason);
        AssertCatalogClean();
    }

    [Fact]
    public async Task SuccessfulNavigation_IsTracedFromPositionThroughResponse()
    {
        await StartAsync();
        await OpenAsync("a.cvl");

        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        _backend.CannedSymbol = Symbol();
        _backend.CannedDefinitions = new BackendDefinitionResult(
            new Dictionary<DocumentUri, string> { [coreUri] = Text },
            [new BackendDefinitionTarget(coreUri, new TextSpan(0, 3), new TextSpan(4, 4))]);

        Location[]? locations = await _session.Client.DefinitionAsync(Uri("a.cvl"), 0, 4).WithTimeout("definition");
        Assert.NotNull(locations);

        var events = Drain();

        Assert.Equal("mapped", Assert.Single(Named(events, "definition.position_mapped")).Event.Outcome);

        var lookup = Assert.Single(Named(events, "symbol.lookup.started"));
        Assert.Equal("found", Assert.Single(Named(events, "symbol.lookup.completed")).Event.Outcome);
        Assert.Equal("found", Assert.Single(Named(events, "definition.targets.completed")).Event.Outcome);
        Assert.Equal("mapped", Assert.Single(Named(events, "definition.mapping.completed")).Event.Outcome);

        var request = Assert.Single(Named(events, "request.completed"));
        Assert.Equal("success", request.Event.Outcome);

        // The stage spans share the request operation id as their parent.
        Assert.Equal(request.Event.OperationId, lookup.Event.ParentOperationId);

        AssertCatalogClean();
    }

    [Fact]
    public async Task UnknownSymbol_RecordsSymbolLookupMiss()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        _backend.CannedSymbol = null;

        Location[]? locations = await _session.Client.DefinitionAsync(Uri("a.cvl"), 0, 4).WithTimeout("definition");
        Assert.Null(locations);

        var events = Drain();
        Assert.Equal("not_found", Assert.Single(Named(events, "symbol.lookup.completed")).Event.Outcome);

        var request = Assert.Single(Named(events, "request.completed"));
        Assert.Equal("not_found", request.Event.Outcome);
        Assert.Equal("symbol_lookup_miss", request.Event.Reason);
        AssertCatalogClean();
    }

    [Fact]
    public async Task InvalidPosition_RecordsInvalidPosition()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        _backend.CannedSymbol = Symbol();

        // A line far past the end of the single-line document cannot be mapped to an offset.
        Location[]? locations = await _session.Client.DefinitionAsync(Uri("a.cvl"), 500, 0).WithTimeout("definition");
        Assert.Null(locations);

        var events = Drain();
        var mapped = Assert.Single(Named(events, "definition.position_mapped"));
        Assert.Equal("invalid", mapped.Event.Outcome);
        Assert.Equal("invalid_position", mapped.Event.Reason);

        var request = Assert.Single(Named(events, "request.completed"));
        Assert.Equal("not_found", request.Event.Outcome);
        Assert.Equal("invalid_position", request.Event.Reason);
        AssertCatalogClean();
    }

    [Fact]
    public async Task EmptyTargets_RecordsNotFoundOnTargetsStage()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        _backend.CannedSymbol = Symbol();
        _backend.CannedDefinitions = new BackendDefinitionResult(new Dictionary<DocumentUri, string>(), []);

        Location[]? locations = await _session.Client.DefinitionAsync(Uri("a.cvl"), 0, 4).WithTimeout("definition");
        Assert.Null(locations);

        var events = Drain();
        Assert.Equal("none", Assert.Single(Named(events, "definition.targets.completed")).Event.Outcome);

        var request = Assert.Single(Named(events, "request.completed"));
        Assert.Equal("not_found", request.Event.Outcome);
        Assert.Equal("symbol_lookup_miss", request.Event.Reason);
        AssertCatalogClean();
    }

    [Fact]
    public async Task MalformedTarget_RejectsOnlyThatTargetAndStillResponds()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        _backend.CannedSymbol = Symbol();
        _backend.CannedDefinitions = new BackendDefinitionResult(
            new Dictionary<DocumentUri, string> { [coreUri] = Text },
            [
                new BackendDefinitionTarget(coreUri, new TextSpan(0, 3), new TextSpan(0, 5000)),
                new BackendDefinitionTarget(coreUri, new TextSpan(0, 3), new TextSpan(4, 4)),
            ]);

        Location[]? locations = await _session.Client.DefinitionAsync(Uri("a.cvl"), 0, 4).WithTimeout("definition");
        Assert.NotNull(locations);
        Assert.Single(locations!);

        var events = Drain();
        var rejected = Assert.Single(Named(events, "definition.mapping.rejected"));
        Assert.Equal("invalid_target_span", rejected.Event.Reason);

        var request = Assert.Single(Named(events, "request.completed"));
        Assert.Equal("success", request.Event.Outcome);
        AssertCatalogClean();
    }


    [Fact]
    public async Task UncapturedDefinitionTarget_RecordsMissingSourceAtMappingAndTerminal()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        _backend.CannedSymbol = Symbol();
        var targetUri = DocumentUri.Create(_workspace.PathOf("b.cvl"));
        // The backend knows the target location but did not capture source
        // text. The LSP must not call this an unresolved symbol or invalid span.
        _backend.CannedDefinitions = new BackendDefinitionResult(
            new Dictionary<DocumentUri, string>(),
            [new BackendDefinitionTarget(targetUri, new TextSpan(0, 3), new TextSpan(0, 3))]);

        Assert.Null(await _session.Client.DefinitionAsync(Uri("a.cvl"), 0, 4)
            .WithTimeout("missing target source"));

        var events = Drain();
        Assert.Equal("found", Assert.Single(Named(events, "symbol.lookup.completed")).Event.Outcome);
        Assert.Equal("found", Assert.Single(Named(events, "definition.targets.completed")).Event.Outcome);
        var rejected = Assert.Single(Named(events, "definition.mapping.rejected"));
        Assert.Equal("target_not_captured", rejected.Event.Reason);
        Assert.Equal("rejected", Assert.Single(Named(events, "definition.mapping.completed")).Event.Outcome);
        var terminal = Assert.Single(Named(events, "request.completed"));
        Assert.Equal("not_found", terminal.Event.Outcome);
        Assert.Equal("target_not_captured", terminal.Event.Reason);
        AssertCatalogClean();
    }

    [Fact]
    public async Task AllInvalidDefinitionSpans_KeepInvalidSpanTerminalReason()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        _backend.CannedSymbol = Symbol();
        var targetUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        // A captured source with a malformed selection span is not missing.
        // Returning null remains the existing behavior; only the observable
        // terminal reason is corrected.
        _backend.CannedDefinitions = new BackendDefinitionResult(
            new Dictionary<DocumentUri, string> { [targetUri] = Text },
            [new BackendDefinitionTarget(targetUri, new TextSpan(0, 3), new TextSpan(0, 5000))]);

        Assert.Null(await _session.Client.DefinitionAsync(Uri("a.cvl"), 0, 4)
            .WithTimeout("invalid target span"));

        var events = Drain();
        Assert.Equal("found", Assert.Single(Named(events, "symbol.lookup.completed")).Event.Outcome);
        Assert.Equal("found", Assert.Single(Named(events, "definition.targets.completed")).Event.Outcome);
        var rejected = Assert.Single(Named(events, "definition.mapping.rejected"));
        Assert.Equal("invalid_target_span", rejected.Event.Reason);
        Assert.Equal("rejected", Assert.Single(Named(events, "definition.mapping.completed")).Event.Outcome);
        var terminal = Assert.Single(Named(events, "request.completed"));
        Assert.Equal("not_found", terminal.Event.Outcome);
        Assert.Equal("invalid_target_span", terminal.Event.Reason);
        AssertCatalogClean();
    }

    [Fact]
    public async Task BackendFailure_IsRecordedAsFaulted()
    {
        await StartAsync();
        await OpenAsync("a.cvl");
        _backend.ThrowOnNavigation = true;

        // The handler contains backend faults and returns null rather than faulting the RPC.
        Location[]? locations = await _session.Client.DefinitionAsync(Uri("a.cvl"), 0, 4).WithTimeout("definition");
        Assert.Null(locations);

        var events = Drain();
        var terminal = Named(events, "request.completed")
            .Concat(Named(events, "request.faulted"))
            .ToList();
        var request = Assert.Single(terminal);
        Assert.Equal("faulted", request.Event.Outcome);
        AssertCatalogClean();
    }

    [Fact]
    public async Task NonCvoloDocument_RecordsUnsupportedUri()
    {
        await StartAsync();

        // A non-cvolo source file (wrong extension) is rejected by the handler before any
        // store work happens, so the drop is explicit rather than a bare null.
        Uri textUri = new(Path.Combine(_workspace.DirectoryPath, "notes.txt"));
        await _session.Client.NotifyDidOpenAsync(textUri, "plaintext", 1, Text).WithTimeout("didOpen");
        _queue.DrainAll(new List<StampedEvent>());

        Location[]? locations = await _session.Client.DefinitionAsync(textUri, 0, 4).WithTimeout("definition");
        Assert.Null(locations);

        var events = Drain();
        var request = Assert.Single(Named(events, "request.completed"));
        Assert.Equal("unsupported", request.Event.Outcome);
        Assert.Equal("unsupported_uri", request.Event.Reason);
        AssertCatalogClean();
    }

    [Fact]
    public async Task DisabledObservability_RecordsNothing_WhileNavigationStillWorks()
    {
        using var workspace = TestWorkspace.CreateProject(["a.cvl"]);
        var backend = new BlockingBackend();
        var (offObserver, offQueue) = CreateObserver(ObservabilityMode.Off);
        var store = new DocumentStore(backend, new RecordingCoreLogger(), observer: offObserver);
        using ProtocolSession session = ProtocolSession.Start(storeFactory: () => store, observer: offObserver);

        await session.Client.InitializeAsync().WithTimeout("initialize");
        await session.Client.NotifyInitializedAsync().WithTimeout("initialized");
        await session.Client.NotifyDidOpenAsync(workspace.DocumentUri("a.cvl"), "cvolo", 1, Text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(workspace.PathOf("a.cvl"));
        await session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out _), "didOpen applied");

        backend.CannedSymbol = Symbol();
        backend.CannedDefinitions = new BackendDefinitionResult(
            new Dictionary<DocumentUri, string> { [coreUri] = Text },
            [new BackendDefinitionTarget(coreUri, new TextSpan(0, 3), new TextSpan(4, 4))]);

        Location[]? locations = await session.Client.DefinitionAsync(workspace.DocumentUri("a.cvl"), 0, 4).WithTimeout("definition");
        Assert.NotNull(locations);
        Assert.Single(locations!);

        var events = new List<StampedEvent>();
        offQueue.DrainAll(events);
        Assert.Empty(events);
    }

    private sealed class FakeSymbolHandle : BackendSymbolHandle
    {
    }
}