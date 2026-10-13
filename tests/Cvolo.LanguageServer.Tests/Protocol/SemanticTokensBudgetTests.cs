using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// The interactive semantic-token budget is a boundary the editor can move at runtime
/// (§16, §18, §64, §65): a document at the budget is served, one over is refused with a typed
/// resource-limit error, and the refusal happens before the compiler backend is ever invoked.
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class SemanticTokensBudgetTests : IDisposable
{
    private static readonly string[] CanonicalTypes =
    [
        "namespace", "type", "struct", "enum", "interface", "typeParameter",
        "parameter", "variable", "property", "enumMember", "function", "method", "operator",
    ];

    private static readonly string[] CanonicalModifiers = ["declaration", "readonly", "static"];

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public SemanticTokensBudgetTests()
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

    private Uri Uri(string name) => _workspace.DocumentUri(name);

    private static InitializeRequestParams Capabilities()
    {
        return new InitializeRequestParams
        {
            Capabilities = new ClientCapabilitiesPayload
            {
                TextDocument = new TextDocumentClientCapabilitiesPayload
                {
                    SemanticTokens = new SemanticTokensClientCapabilitiesPayload
                    {
                        Requests = new SemanticTokensRequestsPayload { Full = true },
                        TokenTypes = CanonicalTypes,
                        TokenModifiers = CanonicalModifiers,
                        Formats = ["relative"],
                        AugmentsSyntaxTokens = true,
                    },
                },
            },
        };
    }

    private async Task StartAsync()
    {
        await _session.Client.InitializeWithAsync(Capabilities()).WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task ConfigureBudgetAsync(int maxInteractiveLength)
    {
        await _session.Client.NotifyDidChangeConfigurationAsync(
            new JObject { ["cvolo.semanticTokens.maxInteractiveLength"] = maxInteractiveLength });
        await _session.Client.DrainNotificationsAsync();
    }

    private async Task OpenAsync(string name, int version, string text)
    {
        await _session.Client.NotifyDidOpenAsync(Uri(name), "cvolo", version, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf(name));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private async Task ChangeAsync(string name, int version, string text)
    {
        await _session.Client.NotifyDidChangeAsync(Uri(name), version, new TextDocumentContentChangeEvent { Text = text });
        var coreUri = DocumentUri.Create(_workspace.PathOf(name));
        await _session.Client.WaitUntilAsync(
            () => _store.TryGet(coreUri, out var state) && state.Version.Value == version,
            $"{name} v{version}");
    }

    private static BackendSemanticTokenResult Tokens() =>
        new([new BackendSemanticToken(new TextSpan(0, 4), BackendSymbolKind.Function, BackendSemanticTokenModifiers.Declaration)]);

    /// <summary>
    /// Asserts the serialized refusal the server wrote to the wire carries the typed
    /// resource-limit data an editor can key off, not just the in-process exception.
    /// </summary>
    private void AssertResourceLimitWireError()
    {
        JObject? error = _session.Server.LastJsonRpcError();
        Assert.NotNull(error);
        Assert.Equal(-32803, (int?)error!["code"]);

        JObject data = Assert.IsType<JObject>(error["data"]);
        Assert.Equal(CvoloErrorData.BackendUnavailable, (string?)data["cvoloKind"]);
        Assert.True((bool?)data["retryable"]);
        Assert.Equal(CvoloErrorData.ResourceLimit, (string?)data["reason"]);
        Assert.Null(data["retryAfterMs"]);
    }

    [Fact]
    public async Task ADocumentAtTheBudget_IsServed_AndOneCharacterOver_IsRefused()
    {
        await StartAsync();
        await ConfigureBudgetAsync(64);
        _backend.CannedSemanticTokens = Tokens();

        await OpenAsync("a.cvl", 1, new string('x', 64));
        JObject? served = await _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("at the budget");
        Assert.NotNull(served);
        Assert.Equal(5, served!["data"]!.Values<int>().Count());

        await ChangeAsync("a.cvl", 2, new string('x', 65));
        RemoteInvocationException error = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("over the budget"));
        Assert.Equal(-32803, (int?)error.ErrorCode);
        AssertResourceLimitWireError();
    }

    [Fact]
    public async Task RaisingTheBudgetAtRuntime_MakesAPreviouslyRefusedDocumentServable()
    {
        await StartAsync();
        await ConfigureBudgetAsync(16);
        _backend.CannedSemanticTokens = Tokens();

        await OpenAsync("a.cvl", 1, new string('x', 32));

        RemoteInvocationException error = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("refused under the small budget"));
        Assert.Equal(-32803, (int?)error.ErrorCode);

        // The same open document, without a reload, is servable after the budget grows.
        await ConfigureBudgetAsync(1024);
        Assert.NotNull(await _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("served after the raise"));
    }

    [Fact]
    public async Task LoweringTheBudgetAtRuntime_RefusesTheSameDocumentWithoutAReload()
    {
        await StartAsync();
        _backend.CannedSemanticTokens = Tokens();

        await OpenAsync("a.cvl", 1, new string('x', 2048));
        Assert.NotNull(await _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("served under the default"));

        await ConfigureBudgetAsync(16);
        RemoteInvocationException error = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("refused after the lower"));
        Assert.Equal(-32803, (int?)error.ErrorCode);
    }

    [Fact]
    public async Task AnOverBudgetDocument_IsRefused_BeforeTheBackendIsInvoked()
    {
        await StartAsync();
        await ConfigureBudgetAsync(16);
        _backend.CannedSemanticTokens = Tokens();

        await OpenAsync("a.cvl", 1, new string('x', 512));
        int callsBefore = _backend.SemanticTokensCalls;

        RemoteInvocationException error = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("over budget"));
        Assert.Equal(-32803, (int?)error.ErrorCode);
        AssertResourceLimitWireError();

        Assert.Equal(callsBefore, _backend.SemanticTokensCalls);
    }
}
