using System.Collections.Concurrent;
using System.Text.Json;
using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Stage 0/3 experiment: how a JSON-RPC request id can be observed at the protocol boundary and
/// whether the invoked handler can see an ambient identity for its own request. StreamJsonRpc
/// 2.25.29 exposes no request context to a registered target, so the boundary decorator below is
/// the proposed mechanism: it records every incoming request id and the formatter callbacks report
/// every response id. The assertions pin down the observable facts this implementation relies on:
/// numeric and string ids keep their JSON type on the wire, the response callback pairs each
/// response with the request id it answers, and handler code cannot read the boundary identity
/// from an ambient slot, so correlation must be explicit.
/// </summary>
public class RequestIdCorrelationExperimentTests
{
    private static readonly AsyncLocal<RequestId?> AmbientRequestId = new();

    private static void SendRaw(DuplexTestTransport transport, string jsonMessage)
    {
        var payload = FrameParser.EncodeFrame(jsonMessage);
        transport.ClientOutput.Write(payload, 0, payload.Length);
        transport.ClientOutput.Flush();
    }

    private static async Task<JObject> WaitForResponseAsync(
        RecordingStream serverOutput,
        string expectedIdText,
        TimeSpan timeout,
        string? diagnostics = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = FrameParser.ParseAll(serverOutput.RecordedBytes)
                .Select(JObject.Parse)
                .FirstOrDefault(frame =>
                    frame["method"] is null
                    && (frame["result"] is not null || frame["error"] is not null)
                    && string.Equals(frame["id"]?.ToString(), expectedIdText, StringComparison.Ordinal));
            if (response is not null)
                return response;
            await Task.Delay(10);
        }

        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for the response to id {expectedIdText}; got frames: {string.Join(" | ", FrameParser.ParseAll(serverOutput.RecordedBytes))}; {diagnostics}");
    }

    private static (JsonRpc Rpc, RecordingStream Output, ConcurrentQueue<RequestId> Received, ConcurrentQueue<(RequestId Id, bool Success)> Sent, ObservingHeaderDelimitedHandler Handler)
        StartServer(DuplexTestTransport transport, ProbeTarget target)
    {
        var serverOutput = new RecordingStream(transport.ServerOutput);
        var formatter = new JsonMessageFormatter
        {
            JsonSerializer =
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
            },
        };

        var handler = new ObservingHeaderDelimitedHandler(serverOutput, transport.ServerInput, formatter);

        var rpc = new JsonRpc(handler);
        var received = handler.Received;
        var sent = new ConcurrentQueue<(RequestId Id, bool Success)>();
        if (rpc is IJsonRpcFormatterCallbacks callbacks)
        {
            callbacks.ResponseSent += (_, args) => sent.Enqueue((args.RequestId, args.IsSuccessfulResponse));
        }

        rpc.AddLocalRpcTarget(target, new JsonRpcTargetOptions
        {
            MethodNameTransform = name => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..],
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.StartListening();
        return (rpc, serverOutput, received, sent, handler);
    }

    [Fact]
    public async Task Numeric_id_keeps_its_wire_type_and_is_visible_at_the_boundary()
    {
        using var transport = new DuplexTestTransport();
        var server = StartServer(transport, new ProbeTarget());
        try
        {
            SendRaw(transport, """{"jsonrpc":"2.0","id":42,"method":"describe","params":{"tag":"numeric"}}""");
            var response = await WaitForResponseAsync(server.Output, "42", TimeSpan.FromSeconds(15),
                $"readCalls={server.Handler.ReadCalls} received={string.Join(',', server.Received)}");

            Assert.Equal(JTokenType.Integer, response["id"]!.Type);
            Assert.Equal(42L, response["id"]!.Value<long>());
            Assert.Equal("numeric", response["result"]!["tag"]!.Value<string>());

            // The boundary decorator saw the request id with the same wire type the client sent.
            Assert.Contains(new RequestId(42), server.Received);

            // The formatter callback reports the response with its request id and success flag.
            Assert.Contains(server.Sent, pair => pair.Id == new RequestId(42) && pair.Success);
        }
        finally
        {
            server.Rpc.Dispose();
        }
    }

    [Fact]
    public async Task String_id_keeps_its_wire_type_and_is_visible_at_the_boundary()
    {
        using var transport = new DuplexTestTransport();
        var server = StartServer(transport, new ProbeTarget());
        try
        {
            SendRaw(transport, """{"jsonrpc":"2.0","id":"req-abc","method":"describe","params":{"tag":"string"}}""");
            var response = await WaitForResponseAsync(server.Output, "req-abc", TimeSpan.FromSeconds(15),
                $"readCalls={server.Handler.ReadCalls} received={string.Join(',', server.Received)}");

            Assert.Equal(JTokenType.String, response["id"]!.Type);
            Assert.Equal("req-abc", response["id"]!.Value<string>());
            Assert.Equal("string", response["result"]!["tag"]!.Value<string>());

            Assert.Contains(new RequestId("req-abc"), server.Received);
            Assert.Contains(server.Sent, pair => pair.Id == new RequestId("req-abc") && pair.Success);
        }
        finally
        {
            server.Rpc.Dispose();
        }
    }

    [Fact]
    public async Task Sequential_numeric_and_string_requests_keep_their_own_ids()
    {
        // Back-to-back raw frames written before the first response are only partially read by this
        // ad-hoc decorated transport pair, so the experiment pins the sequential contract here: two
        // requests answered one after the other each keep their own id and payload. Concurrent id
        // isolation is verified through the real protocol harness (TestClient with concurrent
        // requests), not through this experiment.
        using var transport = new DuplexTestTransport();
        var server = StartServer(transport, new ProbeTarget());
        try
        {
            SendRaw(transport, """{"jsonrpc":"2.0","id":7,"method":"describe","params":{"tag":"seven"}}""");
            var first = await WaitForResponseAsync(server.Output, "7", TimeSpan.FromSeconds(15),
                $"readCalls={server.Handler.ReadCalls} received={string.Join(',', server.Received)}");
            Assert.Equal(7L, first["id"]!.Value<long>());
            Assert.Equal("seven", first["result"]!["tag"]!.Value<string>());

            SendRaw(transport, """{"jsonrpc":"2.0","id":"s-7","method":"describe","params":{"tag":"text-seven"}}""");
            var second = await WaitForResponseAsync(server.Output, "s-7", TimeSpan.FromSeconds(15),
                $"readCalls={server.Handler.ReadCalls} received={string.Join(',', server.Received)}");
            Assert.Equal("s-7", second["id"]!.Value<string>());
            Assert.Equal("text-seven", second["result"]!["tag"]!.Value<string>());

            Assert.Equal(2, server.Received.Count);
            Assert.Contains(server.Sent, pair => pair.Id == new RequestId(7) && pair.Success);
            Assert.Contains(server.Sent, pair => pair.Id == new RequestId("s-7") && pair.Success);
        }
        finally
        {
            server.Rpc.Dispose();
        }
    }

    [Fact]
    public async Task Plain_handler_accepts_a_second_request_after_the_first_response()
    {
        // Control for the decorator experiment: without the boundary decorator, two sequential raw
        // requests on the same connection must both be answered. If this passes while the decorated
        // variant does not, the decorator itself is dropping the read loop and must not ship.
        using var transport = new DuplexTestTransport();
        var serverOutput = new RecordingStream(transport.ServerOutput);
        var formatter = new JsonMessageFormatter
        {
            JsonSerializer =
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
            },
        };
        var handler = new HeaderDelimitedMessageHandler(serverOutput, transport.ServerInput, formatter);
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(new ProbeTarget(), new JsonRpcTargetOptions
        {
            MethodNameTransform = name => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..],
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.StartListening();
        try
        {
            SendRaw(transport, """{"jsonrpc":"2.0","id":1,"method":"describe","params":{"tag":"first"}}""");
            var first = await WaitForResponseAsync(serverOutput, "1", TimeSpan.FromSeconds(15));

            SendRaw(transport, """{"jsonrpc":"2.0","id":2,"method":"describe","params":{"tag":"second"}}""");
            var second = await WaitForResponseAsync(serverOutput, "2", TimeSpan.FromSeconds(15));

            Assert.Equal("first", first["result"]!["tag"]!.Value<string>());
            Assert.Equal("second", second["result"]!["tag"]!.Value<string>());
        }
        finally
        {
            rpc.Dispose();
        }
    }

    [Fact]
    public async Task Derived_observing_handler_accepts_a_second_request()
    {
        // Decisive control: the production-shaped boundary observer (a subclass of the real
        // header-delimited handler) must keep serving requests after the first response, unlike a
        // wrapper that bypasses the base-class read bookkeeping.
        using var transport = new DuplexTestTransport();
        var serverOutput = new RecordingStream(transport.ServerOutput);
        var formatter = new JsonMessageFormatter
        {
            JsonSerializer =
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
            },
        };
        var handler = new ObservingHeaderDelimitedHandler(serverOutput, transport.ServerInput, formatter);
        var rpc = new JsonRpc(handler);
        rpc.AddLocalRpcTarget(new ProbeTarget(), new JsonRpcTargetOptions
        {
            MethodNameTransform = name => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..],
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.StartListening();
        try
        {
            SendRaw(transport, """{"jsonrpc":"2.0","id":1,"method":"describe","params":{"tag":"first"}}""");
            await WaitForResponseAsync(serverOutput, "1", TimeSpan.FromSeconds(15));

            SendRaw(transport, """{"jsonrpc":"2.0","id":2,"method":"describe","params":{"tag":"second"}}""");
            var second = await WaitForResponseAsync(serverOutput, "2", TimeSpan.FromSeconds(15));
            Assert.Equal("second", second["result"]!["tag"]!.Value<string>());
        }
        finally
        {
            rpc.Dispose();
        }
    }

    [Fact]
    public async Task Handler_cannot_observe_the_ambient_request_id_through_read_async_async_local()
    {
        // Documents the experiment outcome that drives the correlation design: whatever the
        // decorator records in ReadAsync does NOT flow into the dispatched handler's execution
        // context, so request-scoped identity needs an explicit mechanism.
        using var transport = new DuplexTestTransport();
        var server = StartServer(transport, new ProbeTarget());
        try
        {
            SendRaw(transport, """{"jsonrpc":"2.0","id":9,"method":"describe","params":{"tag":"probe"}}""");
            var response = await WaitForResponseAsync(server.Output, "9", TimeSpan.FromSeconds(15));

            Assert.Equal("none", response["result"]!["ambient"]!.Value<string>());
        }
        finally
        {
            server.Rpc.Dispose();
        }
    }

    /// <summary>
    /// Boundary observer: derives from the real header-delimited handler and records every
    /// incoming request id. Deriving (rather than wrapping another <c>IJsonRpcMessageHandler</c>)
    /// matters: a wrapper only ever saw the first message because it bypassed the base-class
    /// read/write bookkeeping the JsonRpc read loop depends on. The derived handler keeps the
    /// exact production framing behaviour and adds observation through <c>ReadCoreAsync</c>.
    /// </summary>
    private sealed class ObservingHeaderDelimitedHandler : HeaderDelimitedMessageHandler
    {
        public ObservingHeaderDelimitedHandler(Stream writeStream, Stream readStream, IJsonRpcMessageFormatter formatter)
            : base(writeStream, readStream, formatter)
        {
        }

        public ConcurrentQueue<RequestId> Received { get; } = new();

        public int ReadCalls;

        protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
        {
            var message = await base.ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref ReadCalls);
            if (message is JsonRpcRequest request)
            {
                Received.Enqueue(request.RequestId);
                AmbientRequestId.Value = request.RequestId;
            }

            return message;
        }
    }

    /// <summary>RPC target: reports what the handler could see for its own request.</summary>
    public sealed class ProbeTarget
    {
        public DescribeResult Describe(DescribeRequest request)
            => new(request.Tag, AmbientRequestId.Value?.ToString() ?? "none");
    }

    public sealed record DescribeRequest(string Tag);

    public sealed record DescribeResult(string Tag, string? Ambient);
}
