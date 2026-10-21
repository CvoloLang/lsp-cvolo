using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// The session lifecycle is explainable from a trace: the negotiated capabilities, the client
/// handshake completing with the initialized notification, and a settings change being applied in
/// place. Tracing must record nothing when observability is off.
/// </summary>
public sealed class LifecycleObservabilityTests : IDisposable
{
    private const string Text = "int main() { return 0; }\n";

    private readonly TestWorkspace _workspace;
    private readonly RecordingLspLogger _logger;

    public LifecycleObservabilityTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"], _ => Text);
        _logger = new RecordingLspLogger();
    }

    public void Dispose() => _workspace.Dispose();

    private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateObserver(ObservabilityMode mode)
    {
        var options = new ObservabilityOptions { Mode = mode, Emitter = "lsp-lifecycle-test", QueueCapacity = 20_000 };
        var queue = new BoundedEventQueue(options.QueueCapacity);
        var observer = new CompilerObserver(
            options,
            identity: new ObservabilityIdentity("lsp-lifecycle-test"),
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

    private static InitializeRequestParams RefreshCapabilities => new()
    {
        Capabilities = new ClientCapabilitiesPayload
        {
            Workspace = new WorkspaceClientCapabilitiesPayload
            {
                CodeLens = new CodeLensWorkspaceClientCapabilitiesPayload { RefreshSupport = true },
                InlayHint = new InlayHintWorkspaceClientCapabilitiesPayload { RefreshSupport = true },
            },
        },
    };

    [Fact]
    public async Task Initialize_records_negotiated_capabilities_and_the_client_handshake()
    {
        (CompilerObserver observer, BoundedEventQueue queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);

        await session.Client.InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath)
            .WithTimeout("initialize");
        Drain(queue); // boundary rpc events from initialize are not under test here

        await session.Client.NotifyInitializedAsync().WithTimeout("initialized");
        await session.Client.WaitUntilAsync(() => session.Server.InitializedReceived, "the initialized notification");

        List<StampedEvent> events = Drain(queue);
        StampedEvent initialized = Assert.Single(Named(events, "client.initialized"));
        Assert.Equal("initialized", initialized.Event.Outcome);
        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
        Assert.Equal(0, observer.Catalog!.UnknownReasons);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    [Fact]
    public async Task Initialize_records_capabilities_negotiated()
    {
        (CompilerObserver observer, BoundedEventQueue queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);

        await session.Client.InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath)
            .WithTimeout("initialize");

        List<StampedEvent> events = Drain(queue);
        StampedEvent negotiated = Assert.Single(Named(events, "capabilities.negotiated"));
        Assert.Equal("negotiated", negotiated.Event.Outcome);
        Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
    }

    [Fact]
    public async Task Configuration_change_records_applied()
    {
        (CompilerObserver observer, BoundedEventQueue queue) = CreateObserver(ObservabilityMode.Detailed);
        using var session = ProtocolSession.Start(_logger, observer: observer);

        await session.Client.InitializeWithAsync(RefreshCapabilities).WithTimeout("initialize");
        await session.Client.NotifyInitializedAsync().WithTimeout("initialized");

        // The configuration notification is dispatched on the ingress scheduler; wait for its
        // observable effect (the lens refresh the handler requests) before reading the trace.
        await session.Client.NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.codeLens.references"] = false })
            .WithTimeout("didChangeConfiguration");
        await session.Client.WaitUntilAsync(
            () => session.Server.GetServerFrames().Any(frame => frame.Contains("workspace/codeLens/refresh", StringComparison.Ordinal)),
            "the lens refresh request");

        List<StampedEvent> events = Drain(queue);
        StampedEvent applied = Assert.Single(Named(events, "configuration.applied"));
        Assert.Equal("applied", applied.Event.Outcome);
        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
    }

    [Fact]
    public async Task Disabled_observability_records_nothing_while_the_session_still_works()
    {
        (CompilerObserver observer, BoundedEventQueue queue) = CreateObserver(ObservabilityMode.Off);
        using var session = ProtocolSession.Start(_logger, observer: observer);

        await session.Client.InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath)
            .WithTimeout("initialize");
        await session.Client.NotifyInitializedAsync().WithTimeout("initialized");
        await session.Client.NotifyDidChangeConfigurationAsync(new JObject()).WithTimeout("didChangeConfiguration");

        Assert.True(session.Server.InitializedReceived);
        List<StampedEvent> events = Drain(queue);
        Assert.Empty(events);
    }
}
