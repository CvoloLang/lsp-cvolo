using System.Diagnostics;
using System.Text.Json;
using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Verifies the JSON-RPC boundary instrumentation (OBS-LSP-008): every request that reaches the
/// server publishes one received event with a typed request id, every response pairs back to the
/// same request uid, numeric and string ids keep their wire type, concurrent requests never cross
/// their correlation, and the boundary emits only catalog-valid events. The wire itself must stay
/// pure framed JSON-RPC (OBS-LSP-009): instrumentation never writes to the transport.
/// </summary>
public sealed class RpcBoundaryObservabilityTests
{
    private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateObserver()
    {
        var options = new ObservabilityOptions
        {
            Mode = ObservabilityMode.Detailed,
            Emitter = "lsp-boundary-tests",
            QueueCapacity = 20_000,
        };
        var queue = new BoundedEventQueue(options.QueueCapacity);
        var observer = new CompilerObserver(
            options,
            new ObservabilityIdentity("lsp-boundary-tests"),
            new SystemObservabilityClock(),
            queue,
            EventCatalog.CreateKernelCatalog());
        return (observer, queue);
    }

    private static List<StampedEvent> Drain(BoundedEventQueue queue)
    {
        var events = new List<StampedEvent>();
        queue.DrainAll(events);
        return events;
    }

    private static string? Attribute(StampedEvent evt, string key)
    {
        if (evt.Event.Attributes is null)
        {
            return null;
        }

        foreach (var pair in evt.Event.Attributes)
        {
            if (pair.Key == key)
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static async Task WaitForFrameAsync(ServerHost server, Func<JsonElement, bool> matches, string label)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            foreach (var frame in server.GetServerFrames())
            {
                using var document = JsonDocument.Parse(frame);
                if (document.RootElement.TryGetProperty("method", out _))
                {
                    continue;
                }

                if (matches(document.RootElement))
                {
                    return;
                }
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new Xunit.Sdk.XunitException($"Timed out waiting for {label}. Frames: {string.Join(" | ", server.GetServerFrames())}");
    }

    [Fact]
    public async Task Initialize_request_and_response_share_one_request_uid()
    {
        var (observer, queue) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);

        await session.Client.InitializeAsync().ConfigureAwait(false);

        var events = Drain(queue);
        var received = Assert.Single(events, e => e.Event.Event == "rpc.request.received");
        Assert.NotNull(received.Event.RequestUid);
        Assert.NotNull(received.Event.RpcRequestId);
        Assert.Equal("number", received.Event.RpcRequestId!.Value.Type);
        Assert.Equal("initialize", Attribute(received, "method"));

        var sent = Assert.Single(events, e => e.Event.Event == "rpc.response.sent");
        Assert.Equal(received.Event.RequestUid, sent.Event.RequestUid);
        Assert.Equal(received.Event.RpcRequestId, sent.Event.RpcRequestId);
        Assert.Equal("success", sent.Event.Outcome);

        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
        Assert.Equal(0, observer.Catalog!.UnknownReasons);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    [Fact]
    public async Task String_request_id_keeps_its_type_and_pairs_with_its_response()
    {
        var (observer, queue) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync().ConfigureAwait(false);
        Drain(queue);

        session.Client.SendRawJson(
            "{\"jsonrpc\":\"2.0\",\"id\":\"req-abc\",\"method\":\"cvolo/unknownRequest\",\"params\":null}");

        await WaitForFrameAsync(
            session.Server,
            element => element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == "req-abc",
            "string-id response").ConfigureAwait(false);

        var events = Drain(queue);
        var received = Assert.Single(events, e => e.Event.Event == "rpc.request.received");
        Assert.NotNull(received.Event.RpcRequestId);
        Assert.Equal("string", received.Event.RpcRequestId!.Value.Type);
        Assert.Equal("req-abc", received.Event.RpcRequestId!.Value.Value);

        var sent = Assert.Single(events, e => e.Event.Event == "rpc.response.sent");
        Assert.Equal(received.Event.RequestUid, sent.Event.RequestUid);
        Assert.Equal(received.Event.RpcRequestId, sent.Event.RpcRequestId);
        Assert.Equal("error", sent.Event.Outcome); // unknown method answers with a JSON-RPC error

        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    [Fact]
    public async Task Numeric_and_string_request_ids_with_same_digits_never_share_correlation()
    {
        var (observer, queue) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync().ConfigureAwait(false);
        Drain(queue);

        // Both legal JSON-RPC ID types have identical printable digits.
        // The transport must preserve their distinct UIDs, including when
        // responses arrive in a different order than the two requests.
        session.Client.SendRawJson(
            """{"jsonrpc":"2.0","id":731,"method":"cvolo/unknownRequest","params":null}""");
        session.Client.SendRawJson(
            """{"jsonrpc":"2.0","id":"731","method":"cvolo/unknownRequest","params":null}""");

        var events = new List<StampedEvent>();
        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(events);
            return events.Count(e => e.Event.Event == "rpc.request.received") == 2
                && events.Count(e => e.Event.Event == "rpc.response.sent") == 2;
        }, "paired numeric and string request id responses");

        var received = events.Where(e => e.Event.Event == "rpc.request.received").ToArray();
        var sent = events.Where(e => e.Event.Event == "rpc.response.sent").ToArray();
        Assert.Equal(2, received.Select(e => e.Event.RequestUid)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(received, e => e.Event.RpcRequestId!.Value.Type == "number");
        Assert.Contains(received, e => e.Event.RpcRequestId!.Value.Type == "string");
        foreach (var request in received)
        {
            var response = Assert.Single(sent, e => e.Event.RequestUid == request.Event.RequestUid);
            Assert.Equal(request.Event.RpcRequestId, response.Event.RpcRequestId);
        }

        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    [Fact]
    public async Task Concurrent_requests_keep_distinct_uids_and_pair_one_to_one()
    {
        var (observer, queue) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync().ConfigureAwait(false);
        Drain(queue);

        var first = session.Client.TryUnknownRequestAsync();
        var second = session.Client.TryUnknownRequestAsync();
        await Task.WhenAll(first, second).ConfigureAwait(false);

        var events = Drain(queue);
        var received = events.Where(e => e.Event.Event == "rpc.request.received").ToList();
        var sent = events.Where(e => e.Event.Event == "rpc.response.sent").ToList();

        Assert.Equal(2, received.Count);
        Assert.Equal(2, sent.Count);
        Assert.Equal(2, received.Select(e => e.Event.RequestUid).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, sent.Select(e => e.Event.RequestUid).Distinct(StringComparer.Ordinal).Count());

        foreach (var request in received)
        {
            var response = Assert.Single(sent, e => e.Event.RequestUid == request.Event.RequestUid);
            Assert.Equal(request.Event.RpcRequestId, response.Event.RpcRequestId);
        }

        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.UnknownOutcomes);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    [Fact]
    public async Task Request_uids_include_real_server_instance_and_never_collide_across_sessions()
    {
        var (firstObserver, firstQueue) = CreateObserver();
        var (secondObserver, secondQueue) = CreateObserver();
        Assert.NotEqual(firstObserver.Identity.ServerInstanceId, secondObserver.Identity.ServerInstanceId);

        using var firstSession = ProtocolSession.Start(observer: firstObserver);
        using var secondSession = ProtocolSession.Start(observer: secondObserver);

        // Both clients start at numeric RPC id 1. Their internal request identities
        // must still be distinct when traces are merged into one diagnostic bundle.
        await Task.WhenAll(
            firstSession.Client.InitializeAsync(),
            secondSession.Client.InitializeAsync());

        var firstEvents = Drain(firstQueue);
        var secondEvents = Drain(secondQueue);
        var firstReceived = Assert.Single(firstEvents, e => e.Event.Event == "rpc.request.received");
        var secondReceived = Assert.Single(secondEvents, e => e.Event.Event == "rpc.request.received");

        Assert.NotNull(firstReceived.Event.RequestUid);
        Assert.NotNull(secondReceived.Event.RequestUid);
        Assert.StartsWith(firstObserver.Identity.ServerInstanceId + "/rpc/", firstReceived.Event.RequestUid);
        Assert.StartsWith(secondObserver.Identity.ServerInstanceId + "/rpc/", secondReceived.Event.RequestUid);
        Assert.NotEqual(firstReceived.Event.RequestUid, secondReceived.Event.RequestUid);
        Assert.Equal(firstReceived.Event.RpcRequestId, secondReceived.Event.RpcRequestId);

        var firstSent = Assert.Single(firstEvents, e => e.Event.Event == "rpc.response.sent");
        var secondSent = Assert.Single(secondEvents, e => e.Event.Event == "rpc.response.sent");
        Assert.Equal(firstReceived.Event.RequestUid, firstSent.Event.RequestUid);
        Assert.Equal(secondReceived.Event.RequestUid, secondSent.Event.RequestUid);
        Assert.Equal(0, firstObserver.Catalog!.MissingFields);
        Assert.Equal(0, secondObserver.Catalog!.MissingFields);
    }

    [Fact]
    public async Task Request_after_enabling_recording_has_real_server_scoped_uid()
    {
        var options = new ObservabilityOptions
        {
            Mode = ObservabilityMode.Off,
            Emitter = "lsp-boundary-tests",
            QueueCapacity = 20_000,
        };
        var queue = new BoundedEventQueue(options.QueueCapacity);
        var observer = new CompilerObserver(
            options,
            queue: queue,
            catalog: EventCatalog.CreateKernelCatalog());

        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync();
        Assert.Empty(Drain(queue));

        observer.SetMode(ObservabilityMode.Detailed);
        await session.Client.TryUnknownRequestAsync();

        // Transport response completion can precede the observer's
        // response-sent callback. Await the actual terminal telemetry event
        // rather than racing immediate queue collection.
        var events = new List<StampedEvent>();
        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(events);
            return events.Any(e => e.Event.Event == "rpc.request.received")
                && events.Any(e => e.Event.Event == "rpc.response.sent");
        }, "request and response telemetry after mode activation");

        var received = Assert.Single(events, e => e.Event.Event == "rpc.request.received");
        var sent = Assert.Single(events, e => e.Event.Event == "rpc.response.sent");

        Assert.NotNull(received.Event.RequestUid);
        Assert.StartsWith(observer.Identity.ServerInstanceId + "/rpc/", received.Event.RequestUid);
        Assert.Equal(received.Event.RequestUid, sent.Event.RequestUid);
        Assert.Equal(received.Event.RpcRequestId, sent.Event.RpcRequestId);
        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }

    [Fact]
    public async Task Concurrent_hover_and_definition_keep_their_actual_rpc_request_uids()
    {
        var (observer, queue) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync();
        Drain(queue);

        // Valid URI, unopened document: no compiler/tooling dependency is needed
        // to prove the two requests are distinguishable at both boundaries.
        var uri = new Uri(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "obs-rpc-unopened.cvl")));
        await Task.WhenAll(
            session.Client.HoverAsync(uri, 0, 0),
            session.Client.DefinitionAsync(uri, 0, 0));

        var events = new List<StampedEvent>();
        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(events);
            return events.Count(e => e.Event.Event == "rpc.response.sent") >= 2
                && events.Any(e => e.Event.Event == "feature.request.started")
                && events.Any(e => e.Event.Event == "request.started" && e.Event.Stage == "definition");
        }, "two real feature requests and corresponding RPC responses");

        var hoverRpc = Assert.Single(events, e =>
            e.Event.Event == "rpc.request.received" && Attribute(e, "method") == "textDocument/hover");
        var definitionRpc = Assert.Single(events, e =>
            e.Event.Event == "rpc.request.received" && Attribute(e, "method") == "textDocument/definition");
        Assert.NotNull(hoverRpc.Event.RequestUid);
        Assert.NotNull(definitionRpc.Event.RequestUid);
        Assert.NotEqual(hoverRpc.Event.RequestUid, definitionRpc.Event.RequestUid);

        var hoverStart = Assert.Single(events, e =>
            e.Event.Event == "feature.request.started" && e.Event.Stage == "hover");
        var definitionStart = Assert.Single(events, e =>
            e.Event.Event == "request.started" && e.Event.Stage == "definition");
        Assert.Equal(hoverRpc.Event.RequestUid, hoverStart.Event.RequestUid);
        Assert.Equal(definitionRpc.Event.RequestUid, definitionStart.Event.RequestUid);

        var hoverEnd = Assert.Single(events, e =>
            e.Event.Event == "feature.request.completed" && e.Event.Stage == "hover");
        var definitionEnd = Assert.Single(events, e =>
            e.Event.Event == "request.completed" && e.Event.Stage == "definition");
        Assert.Equal(hoverStart.Event.OperationId, hoverEnd.Event.OperationId);
        Assert.Equal(definitionStart.Event.OperationId, definitionEnd.Event.OperationId);
        Assert.Equal(hoverRpc.Event.RequestUid, hoverEnd.Event.RequestUid);
        Assert.Equal(definitionRpc.Event.RequestUid, definitionEnd.Event.RequestUid);
        Assert.All(events.Where(e => e.Event.Event == "rpc.response.sent"), sent =>
            Assert.Contains(events, received => received.Event.Event == "rpc.request.received"
                && received.Event.RequestUid == sent.Event.RequestUid));

        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog!.MissingFields);
    }


    [Fact]
    public async Task Unknown_request_and_notification_methods_cannot_leak_untrusted_text_into_trace()
    {
        var (observer, queue) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync().ConfigureAwait(false);
        Drain(queue);

        const string secret = "PRIVATE_SOURCE_TOKEN_NEVER_LOG_THIS";
        // The wire must still answer the original method, even though the
        // metadata-only trace is forbidden from storing its untrusted name.
        session.Client.SendRawJson(
            $$"""{"jsonrpc":"2.0","id":9804,"method":"textDocument/{{secret}}","params":null}""");
        await WaitForFrameAsync(
            session.Server,
            element => element.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 9804,
            "unknown-method error response").ConfigureAwait(false);

        session.Client.SendRawJson(
            $$"""{"jsonrpc":"2.0","method":"window/{{secret}}","params":null}""");

        var events = new List<StampedEvent>();
        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(events);
            return events.Any(e => e.Event.Event == "rpc.response.sent"
                    && e.Event.RequestUid is not null)
                && events.Any(e => e.Event.Event == "rpc.notification.received"
                    && Attribute(e, "method") == "unrecognized_method");
        }, "private request and notification boundary events");

        var request = Assert.Single(events, e =>
            e.Event.Event == "rpc.request.received" && Attribute(e, "method") == "unrecognized_method");
        var response = Assert.Single(events, e =>
            e.Event.Event == "rpc.response.sent" && e.Event.RequestUid == request.Event.RequestUid);
        Assert.Equal(request.Event.RpcRequestId, response.Event.RpcRequestId);
        Assert.Equal("error", response.Event.Outcome);

        // Both incoming method names were attacker-owned; neither may
        // appear in any structured telemetry attribute.
        Assert.DoesNotContain(events, e => e.Event.Attributes?.Any(a =>
            a.Value.Contains(secret, StringComparison.Ordinal)) == true);
        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog.MissingFields);
    }


    [Fact]
    public async Task Numeric_and_string_ids_preserve_real_feature_context_during_overlapping_backend_calls()
    {
        using var workspace = TestWorkspace.CreateProject(["a.cvl"]);
        var backend = new BlockingBackend();
        var (observer, queue) = CreateObserver();
        var store = new DocumentStore(backend, new RecordingCoreLogger(), observer: observer);
        using var session = ProtocolSession.Start(storeFactory: () => store, observer: observer);

        await session.Client.InitializeAsync().ConfigureAwait(false);
        await session.Client.NotifyInitializedAsync().ConfigureAwait(false);
        var uri = workspace.DocumentUri("a.cvl");
        var coreUri = DocumentUri.Create(workspace.PathOf("a.cvl"));
        await session.Client.NotifyDidOpenAsync(uri, "cvolo", 1, "int main() { return 0; }\\n");
        await session.Client.WaitUntilAsync(() => store.TryGet(coreUri, out _), "open test document");
        Drain(queue);

        // The first real feature handler must remain in the backend while
        // StreamJsonRpc admits the second request. No FIFO dispatch matching.
        var wireUri = JsonSerializer.Serialize(uri.AbsoluteUri);
        backend.ArmNavigation();
        var events = new List<StampedEvent>();
        try
        {
            session.Client.SendRawJson(
                $"""{"jsonrpc":"2.0","id":731,"method":"textDocument/definition","params":{"textDocument":{"uri":{{wireUri}}},"position":{"line":0,"character":4}}}""");
            Assert.True(backend.WaitUntilNavigationEntered(TimeSpan.FromSeconds(10)),
                "definition must enter the blocked backend");

            session.Client.SendRawJson(
                $"""{"jsonrpc":"2.0","id":"731","method":"textDocument/hover","params":{"textDocument":{"uri":{{wireUri}}},"position":{"line":0,"character":4}}}""");

            await session.Client.WaitUntilAsync(() =>
            {
                queue.DrainAll(events);
                return events.Any(e => e.Event.Event == "request.started" && e.Event.Stage == "definition")
                    && events.Any(e => e.Event.Event == "feature.request.started" && e.Event.Stage == "hover");
            }, "both handlers must start before the backend is released");
        }
        finally
        {
            backend.ReleaseNavigation();
        }

        await WaitForFrameAsync(session.Server,
            e => e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                && id.GetInt32() == 731, "numeric definition response");
        await WaitForFrameAsync(session.Server,
            e => e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && id.GetString() == "731", "string hover response");

        await session.Client.WaitUntilAsync(() =>
        {
            queue.DrainAll(events);
            return events.Count(e => e.Event.Event == "rpc.response.sent") == 2
                && events.Any(e => e.Event.Event == "request.completed" && e.Event.Stage == "definition")
                && events.Any(e => e.Event.Event == "feature.request.completed" && e.Event.Stage == "hover");
        }, "paired concurrent feature responses");

        var definitionRpc = Assert.Single(events, e => e.Event.Event == "rpc.request.received"
            && Attribute(e, "method") == "textDocument/definition");
        var hoverRpc = Assert.Single(events, e => e.Event.Event == "rpc.request.received"
            && Attribute(e, "method") == "textDocument/hover");
        Assert.Equal("number", definitionRpc.Event.RpcRequestId!.Value.Type);
        Assert.Equal("string", hoverRpc.Event.RpcRequestId!.Value.Type);
        Assert.Equal("731", definitionRpc.Event.RpcRequestId!.Value.Value);
        Assert.Equal("731", hoverRpc.Event.RpcRequestId!.Value.Value);
        Assert.NotEqual(definitionRpc.Event.RequestUid, hoverRpc.Event.RequestUid);

        var definitionStart = Assert.Single(events, e =>
            e.Event.Event == "request.started" && e.Event.Stage == "definition");
        var hoverStart = Assert.Single(events, e =>
            e.Event.Event == "feature.request.started" && e.Event.Stage == "hover");
        Assert.Equal(definitionRpc.Event.RequestUid, definitionStart.Event.RequestUid);
        Assert.Equal(hoverRpc.Event.RequestUid, hoverStart.Event.RequestUid);

        foreach (var received in new[] { definitionRpc, hoverRpc })
        {
            var response = Assert.Single(events, e =>
                e.Event.Event == "rpc.response.sent" && e.Event.RequestUid == received.Event.RequestUid);
            Assert.Equal(received.Event.RpcRequestId, response.Event.RpcRequestId);
            Assert.Equal("success", response.Event.Outcome);
        }

        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog.MissingFields);
    }

    [Fact]
    public async Task Instrumentation_adds_no_bytes_to_the_protocol_stream()
    {
        var (observer, _) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync().ConfigureAwait(false);

        var frames = session.Server.GetServerFrames();
        Assert.NotEmpty(frames);
        foreach (var frame in frames)
        {
            using var document = JsonDocument.Parse(frame);
            var root = document.RootElement;
            Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
            Assert.True(
                root.TryGetProperty("id", out _) || root.TryGetProperty("method", out _),
                $"frame is not a JSON-RPC message: {frame}");
        }
    }
}
