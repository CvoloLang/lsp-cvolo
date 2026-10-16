using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public class LifecycleTests
{
    [Fact]
    public async Task Initialize_ReturnsLspHandshakeWithIncrementalTextSync()
    {
        using var session = ProtocolSession.Start();
        InitializeResponse response = await session.Client
            .InitializeAsync(processId: null)
            .WithTimeout("initialize");

        Assert.Equal("cvolo-language-server", response.ServerInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(response.ServerInfo.Version));

        var raw = JObject.Parse(Assert.Single(session.Server.GetServerFrames()));
        Assert.Equal("2.0", raw["jsonrpc"]?.Value<string>());
        var capabilities = (JObject)raw["result"]!["capabilities"]!;

        var sync = (JObject)capabilities["textDocumentSync"]!;
        Assert.Equal(true, sync["openClose"]?.Value<bool>());
        Assert.Equal(2, sync["change"]?.Value<int>());
        Assert.Null(sync["save"]);

        var completion = (JObject)capabilities["completionProvider"]!;
        Assert.True(completion["resolveProvider"]!.Value<bool>());
        Assert.Contains(".", completion["triggerCharacters"]!.Values<string>());
        Assert.Contains("~", completion["triggerCharacters"]!.Values<string>());
        Assert.Contains("{", completion["triggerCharacters"]!.Values<string>());
        Assert.Contains(",", completion["triggerCharacters"]!.Values<string>());

        var signatureHelp = (JObject)capabilities["signatureHelpProvider"]!;
        Assert.Contains("(", signatureHelp["triggerCharacters"]!.Values<string>());
        Assert.Contains(",", signatureHelp["triggerCharacters"]!.Values<string>());

        Assert.Equal(true, capabilities["hoverProvider"]?.Value<bool>());
        Assert.Equal(true, capabilities["definitionProvider"]?.Value<bool>());
        Assert.Equal(true, capabilities["typeDefinitionProvider"]?.Value<bool>());
        Assert.Equal(true, capabilities["implementationProvider"]?.Value<bool>());
        Assert.Equal(true, capabilities["typeHierarchyProvider"]?.Value<bool>());
        Assert.Equal(true, capabilities["referencesProvider"]?.Value<bool>());
        Assert.Equal(true, capabilities["renameProvider"]?.Value<bool>());
        Assert.Equal(true, capabilities["documentSymbolProvider"]?.Value<bool>());

        // Unimplemented navigation capabilities remain absent (LSP-4 §7.1).
        Assert.Null(capabilities["workspaceSymbolProvider"]);
        Assert.Null(capabilities["declarationProvider"]);
        Assert.Null(capabilities["semanticTokensProvider"]);
        Assert.Null(capabilities["codeActionProvider"]);
        Assert.Null(capabilities["documentFormattingProvider"]);

        Assert.Equal("cvolo-language-server", raw["result"]!["serverInfo"]!["name"]!.Value<string>());
        Assert.Equal(response.ServerInfo.Version, raw["result"]!["serverInfo"]!["version"]!.Value<string>());

        await session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    [Fact]
    public async Task Initialize_NullProcessId_StartsNoWatcher()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync(processId: null).WithTimeout("initialize");

        Assert.Equal(0, session.Watcher.WatchCount);
    }

    [Fact]
    public async Task Initialize_WithProcessId_StartsWatcher()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync(processId: 4242).WithTimeout("initialize");

        Assert.Equal(1, session.Watcher.WatchCount);
        Assert.Equal(4242, session.Watcher.WatchedProcessId);
        Assert.True(session.Watcher.IsWatching);
    }

    [Fact]
    public async Task DoubleInitialize_ReturnsInvalidRequest_WithoutResettingState()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync().WithTimeout("first initialize");

        RemoteRpcException error = await session.Client
            .ExpectErrorAsync(Methods.InitializeName, new InitializeParams())
            .WithTimeout("second initialize");

        Assert.Equal(JsonRpcErrorCode.InvalidRequest, error.ErrorCode);

        var shutdown = await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.Null(shutdown);
        Assert.False(session.Server.Termination.AbnormalRequested);
    }

    [Fact]
    public async Task ShutdownThenExit_ExitsCleanly_AndDisarmsWatcher()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync(processId: 5150).WithTimeout("initialize");
        Assert.False(session.Watcher.WatchToken.IsCancellationRequested);

        var shutdown = await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.Null(shutdown);

        Assert.True(session.Watcher.WatchToken.IsCancellationRequested, "shutdown must disarm the watcher");
        Assert.False(session.Server.Termination.Task.IsCompleted, "server must stay alive until exit");

        await session.Client.NotifyExitAsync().WithTimeout("exit");
        var normal = await session.Server.Termination.Task.WithTimeout("termination");
        Assert.True(normal, "exit after shutdown must be a clean exit (code 0)");
    }

    [Fact]
    public async Task ExitWithoutShutdown_IsAbnormal()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync().WithTimeout("initialize");

        await session.Client.NotifyExitAsync().WithTimeout("exit");

        var normal = await session.Server.Termination.Task.WithTimeout("termination");
        Assert.False(normal, "exit without shutdown must be abnormal (code 1)");
        Assert.True(session.Server.Termination.AbnormalRequested);
        Assert.False(session.Server.Termination.NormalRequested);
    }

    [Fact]
    public async Task ShutdownBeforeInitialize_ReturnsServerNotInitialized()
    {
        using var session = ProtocolSession.Start();

        RemoteRpcException error = await session.Client
            .ExpectErrorAsync(Methods.ShutdownName, null)
            .WithTimeout("shutdown before initialize");

        Assert.Equal(-32002, (int?)error.ErrorCode);
        Assert.False(session.Server.Termination.IsRequested);

        InitializeResponse response = await session.Client.InitializeAsync().WithTimeout("initialize");
        Assert.NotNull(response);
    }

    [Fact]
    public async Task ClientDeathDuringShutdown_WatcherCancellationWins_ExitClean()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync(processId: 6001).WithTimeout("initialize");

        await session.Client.ShutdownAsync().WithTimeout("shutdown");
        Assert.True(session.Watcher.WatchToken.IsCancellationRequested);

        session.Watcher.SimulateClientDeath();

        Assert.False(session.Server.Termination.AbnormalRequested, "disarmed watcher must not force an abnormal exit");

        await session.Client.NotifyExitAsync().WithTimeout("exit");
        var normal = await session.Server.Termination.Task.WithTimeout("termination");
        Assert.True(normal);
    }

    [Fact]
    public async Task ClientDeathWithoutShutdown_ForcesAbnormalExit_AndIsIdempotent()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync(processId: 6002).WithTimeout("initialize");

        session.Watcher.SimulateClientDeath();
        session.Watcher.SimulateClientDeath();

        Assert.True(session.Server.Termination.AbnormalRequested);
        Assert.False(session.Server.Termination.NormalRequested);
        Assert.False(session.Server.Termination.TryRequestNormal(), "gate is first-wins; abnormal must stick");

        await session.Client.NotifyExitAsync().WithTimeout("exit");
        var normal = await session.Server.Termination.Task.WithTimeout("termination");
        Assert.False(normal, "a prior client-death termination must win over exit (code 1)");
    }
}
