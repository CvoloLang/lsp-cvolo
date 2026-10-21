using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Logging;
using Cvolo.Observability.Abstractions;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>In-process server + client pair over a duplex transport.</summary>
internal sealed class ProtocolSession : IDisposable
{
    public DuplexTestTransport Transport { get; }
    public ServerHost Server { get; }
    public TestClient Client { get; }
    public FakeClientProcessWatcher Watcher { get; }

    private ProtocolSession(DuplexTestTransport transport, ServerHost server, TestClient client, FakeClientProcessWatcher watcher)
    {
        Transport = transport;
        Server = server;
        Client = client;
        Watcher = watcher;
    }

    public static ProtocolSession Start(ILspLogger? logger = null, FakeClientProcessWatcher? watcher = null, Action<JsonRpc>? configureRpc = null, Func<DocumentStore>? storeFactory = null, ICompilerObserver? observer = null)
    {
        DuplexTestTransport transport = new();
        FakeClientProcessWatcher effectiveWatcher = watcher ?? new FakeClientProcessWatcher();
        var server = ServerHost.Start(transport, logger, effectiveWatcher, configureRpc, storeFactory, observer);
        TestClient client = new(transport);
        return new ProtocolSession(transport, server, client, effectiveWatcher);
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        Transport.Dispose();
    }
}