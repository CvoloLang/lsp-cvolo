using System.Diagnostics;
using System.Text.Json;
using Cvolo.LanguageServer.Core;
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
