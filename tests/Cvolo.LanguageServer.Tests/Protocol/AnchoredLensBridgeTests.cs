using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// The anchored CodeLens v3 bridge is opaque and session-bound (§21, §39): a token that was not minted
/// by this server, was minted for another session, was already resolved into an action, or whose
/// document has since changed or closed is refused rather than resolved into a guessed position.
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class AnchoredLensBridgeTests : IDisposable
{
    private const string Text = "int main() { return 0; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public AnchoredLensBridgeTests()
    {
        _workspace = TestWorkspace.CreateProject(["a.cvl"]);
        _backend = new BlockingBackend();
        _store = new DocumentStore(_backend, new RecordingCoreLogger());
        _session = ProtocolSession.Start(storeFactory: () => _store);
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private Uri Uri => _workspace.DocumentUri("a.cvl");

    private DocumentUri CoreUri => DocumentUri.Create(_workspace.PathOf("a.cvl"));

    private async Task StartAsync(ProtocolSession? session = null)
    {
        ProtocolSession target = session ?? _session;
        await target.Client.InitializeWithAnchoredLensAsync().WithTimeout("initialize");
        await target.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync(ProtocolSession? session = null, int version = 1, string text = Text)
    {
        ProtocolSession target = session ?? _session;
        await target.Client.NotifyDidOpenAsync(Uri, "cvolo", version, text).WithTimeout("didOpen");
        await target.Client.WaitUntilAsync(() => target.Server.Store.TryGet(CoreUri, out _), "didOpen applied");
    }

    /// <summary>Requests the lenses, returns the single opaque lens request the command carries.</summary>
    private async Task<JObject> MintLensRequestAsync(ProtocolSession? session = null)
    {
        ProtocolSession target = session ?? _session;
        JArray? lenses = await target.Client.CodeLensAsync(Uri).WithTimeout("codeLens");
        JToken lens = Assert.Single(lenses!);
        JArray arguments = Assert.IsType<JArray>(lens["command"]!["arguments"]!);
        return Assert.IsType<JObject>(Assert.Single(arguments));
    }

    private static JObject RequestFor(string lensToken, Uri uri) => new()
    {
        ["schemaVersion"] = 3,
        ["lensToken"] = lensToken,
        ["wireUri"] = uri.AbsoluteUri,
    };

    private static BackendCodeLensInfo References(int start, int length, string title) =>
        new(
            new TextSpan(start, length),
            BackendCodeLensKind.References,
            title,
            new BackendCodeLensCommand("cvolo.showReferences", [new BackendCodeLensPositionArgument(start)]));

    private sealed class FakeSymbolHandle : BackendSymbolHandle;

    [Fact]
    public async Task AnUnknownLensToken_ResolvesToUnavailable()
    {
        await StartAsync();
        await OpenAsync();

        AnchoredActionResultV3 result = await _session.Client
            .ExecuteAnchoredLensAsync(RequestFor("not-a-real-token", Uri))
            .WithTimeout("executeCommand");

        Assert.Equal("Unavailable", result.Kind);
        Assert.Null(result.ActionToken);
    }

    [Fact]
    public async Task AnActionToken_CannotBeReplayedAsALensToken()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedSymbol = new BackendSymbolInfo(
            new FakeSymbolHandle(), new TextSpan(0, 4), "main", BackendSymbolKind.Function, "main");
        _backend.CannedCodeLenses = [References(4, 4, "1 reference")];

        JObject lensRequest = await MintLensRequestAsync();
        AnchoredActionResultV3 resolved = await _session.Client
            .ExecuteAnchoredLensAsync(lensRequest)
            .WithTimeout("resolve");

        Assert.Equal("ShowReferences", resolved.Kind);
        Assert.False(string.IsNullOrEmpty(resolved.ActionToken));

        // The resolved action token must not be usable as a fresh lens; only a minted lens record
        // (IsAction == false) may be resolved.
        AnchoredActionResultV3 replay = await _session.Client
            .ExecuteAnchoredLensAsync(RequestFor(resolved.ActionToken!, Uri))
            .WithTimeout("replay");

        Assert.Equal("Unavailable", replay.Kind);
    }

    [Fact]
    public async Task ALensToken_IsRefusedAfterTheDocumentChanges()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [References(4, 4, "1 reference")];

        JObject lensRequest = await MintLensRequestAsync();

        await _session.Client.NotifyDidChangeAsync(Uri, 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        await _session.Client.WaitUntilAsync(
            () => _store.TryGet(CoreUri, out var state) && state.Version.Value == 2, "a.cvl v2");

        AnchoredActionResultV3 result = await _session.Client
            .ExecuteAnchoredLensAsync(lensRequest)
            .WithTimeout("stale executeCommand");

        Assert.Equal("Unavailable", result.Kind);
    }

    [Fact]
    public async Task ALensToken_IsRefusedAfterTheDocumentCloses()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [References(4, 4, "1 reference")];

        JObject lensRequest = await MintLensRequestAsync();

        await _session.Client.NotifyDidCloseAsync(Uri).WithTimeout("didClose");
        await _session.Client.WaitUntilAsync(() => !_store.TryGet(CoreUri, out _), "a.cvl closed");

        AnchoredActionResultV3 result = await _session.Client
            .ExecuteAnchoredLensAsync(lensRequest)
            .WithTimeout("closed executeCommand");

        Assert.Equal("Unavailable", result.Kind);
    }

    [Fact]
    public async Task ALensToken_FromAnotherSession_IsRefused()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [References(4, 4, "1 reference")];

        JObject lensRequest = await MintLensRequestAsync();
        Assert.False(string.IsNullOrEmpty((string?)lensRequest["lensToken"]));

        // A reconnect creates a fresh server with a fresh registry: a token minted in the previous
        // session carries no meaning in the new one and must not be honored.
        var freshStore = new DocumentStore(_backend, new RecordingCoreLogger());
        using ProtocolSession reconnect = ProtocolSession.Start(storeFactory: () => freshStore);
        await StartAsync(reconnect);
        await OpenAsync(reconnect);

        AnchoredActionResultV3 result = await reconnect.Client
            .ExecuteAnchoredLensAsync(lensRequest)
            .WithTimeout("reconnected executeCommand");

        Assert.Equal("Unavailable", result.Kind);
    }
}
