using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Scheduling;
using Cvolo.LanguageServer.Cli;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Logging;
using Cvolo.LanguageServer.Observability;
using Cvolo.LanguageServer.Protocol;
using Cvolo.Observability.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;
using LanguageServerImpl = Cvolo.LanguageServer.Protocol.LanguageServer;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>
/// Hosts a real <see cref="LanguageServer"/> over an in-process transport with
/// the same wiring the executable uses, so protocol tests exercise production
/// behaviour without spawning a process.
/// </summary>
internal sealed class ServerHost : IDisposable
{
    private readonly HeaderDelimitedMessageHandler _handler;
    private readonly RpcObservability _observability;
    private readonly JsonRpc _rpc;
    private readonly LanguageServerImpl _server;
    private readonly RecordingStream _serverOutput;
    private bool _disposed;

    public TerminationRequest Termination { get; }

    /// <summary>Boundary observer shared with the hosted server, for trace assertions.</summary>
    public RpcObservability Observability => _observability;

    /// <summary>The observer the hosted server was created with.</summary>
    public ICompilerObserver Observer => _server.Observer;

    /// <summary>Test-only view of whether the hosted server accepted 'initialized'.</summary>
    public bool InitializedReceived => _server.InitializedReceived;

    /// <summary>The session-scoped document store (created on first use).</summary>
    public DocumentStore Store => _server.Store;

    /// <summary>The diagnostic notification sink used by the hosted server.</summary>
    public DiagnosticSink Diagnostics => _server.Diagnostics;

    private ServerHost(JsonRpc rpc, LanguageServerImpl server, HeaderDelimitedMessageHandler handler, RpcObservability observability, RecordingStream serverOutput, TerminationRequest termination)
    {
        _rpc = rpc;
        _server = server;
        _handler = handler;
        _observability = observability;
        _serverOutput = serverOutput;
        Termination = termination;
    }

    public static ServerHost Start(DuplexTestTransport transport, ILspLogger? logger = null, IClientProcessWatcher? watcher = null, Action<JsonRpc>? configureRpc = null, Func<DocumentStore>? storeFactory = null, ICompilerObserver? observer = null, LspObservabilitySession? recordingSession = null)
    {
        TerminationRequest termination = new();
        LanguageServerImpl server = new(termination, logger ?? new NullLspLogger(), watcher ?? new FakeClientProcessWatcher(), storeFactory, observer, recordingSession);
        RecordingStream serverOutput = new(transport.ServerOutput);
        JsonMessageFormatter formatter = new();
        formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();
        formatter.JsonSerializer.NullValueHandling = NullValueHandling.Ignore;
        formatter.JsonSerializer.Converters.Add(new UriJsonConverter());
        RpcObservability rpcObservability = new(server.Observer);
        IngressPipeline ingress = new();
        ingress.DocumentIngressAdmitted = server.ObserveAssignedDocumentIngress;
        server.Ingress = ingress;
        HeaderDelimitedMessageHandler handler = new ObservedHeaderDelimitedHandler(
            serverOutput, transport.ServerInput, formatter, rpcObservability,
            () => server.HasPendingRejectedInitializeResponse,
            server.OnRejectedInitializeResponseFlushed,
            server.ObserveTransportDocumentNotification,
            ingress.ObserveInboundRpc);
        JsonRpc rpc = new(handler);
        rpcObservability.Attach(rpc);
        rpc.SynchronizationContext = new IngressSynchronizationContext(ingress);
        server.Diagnostics.Attach(rpc);
        server.Refresh.Attach(rpc);
        server.EditorIntelligenceRefresh.Attach(rpc);
        AddTarget(rpc, server);
        AddTarget(rpc, server.Sync);
        AddTarget(rpc, server.Completion);
        AddTarget(rpc, server.Hover);
        AddTarget(rpc, server.Definition);
        AddTarget(rpc, server.TypeDefinition);
        AddTarget(rpc, server.Implementation);
        AddTarget(rpc, server.PrepareTypeHierarchy);
        AddTarget(rpc, server.TypeHierarchySupertypes);
        AddTarget(rpc, server.TypeHierarchySubtypes);
        AddTarget(rpc, server.DocumentSymbols);
        AddTarget(rpc, server.SemanticTokens);
        AddTarget(rpc, server.References);
        AddTarget(rpc, server.Rename);
        AddTarget(rpc, server.SignatureHelp);
        AddTarget(rpc, server.CodeActions);
        AddTarget(rpc, server.CodeLenses);
        AddTarget(rpc, server.InlayHints);
        AddTarget(rpc, server.DocumentHighlights);
        AddTarget(rpc, server.FoldingRanges);
        AddTarget(rpc, server.SelectionRanges);
        AddTarget(rpc, server.TypeLayout);
        AddTarget(rpc, server.Configuration);
        AddTarget(rpc, server.AnchoredLens);
        configureRpc?.Invoke(rpc);
        rpc.StartListening();

        return new ServerHost(rpc, server, handler, rpcObservability, serverOutput, termination);
    }

    private static void AddTarget(JsonRpc rpc, object target)
    {
        rpc.AddLocalRpcTarget(target, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
    }

    public IReadOnlyList<string> GetServerFrames()
    {
        return FrameParser.ParseAll(_serverOutput.RecordedBytes);
    }

    public void ResetServerFrames()
    {
        _serverOutput.ResetRecording();
    }

    /// <summary>
    /// Returns the last JSON-RPC error object the server wrote to the wire, or
    /// <c>null</c> when no error frame has been recorded. This inspects the actual
    /// serialized payload (code and data), not an in-process exception.
    /// </summary>
    public JObject? LastJsonRpcError()
    {
        IReadOnlyList<string> frames = GetServerFrames();
        for (int i = frames.Count - 1; i >= 0; i--)
        {
            JObject? parsed;
            try
            {
                parsed = JObject.Parse(frames[i]);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed["error"] is JObject error)
            {
                return error;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rpc.Dispose();
        _server.Dispose();
        _handler.DisposeAsync().GetAwaiter().GetResult();
        _observability.Dispose();
    }
}
