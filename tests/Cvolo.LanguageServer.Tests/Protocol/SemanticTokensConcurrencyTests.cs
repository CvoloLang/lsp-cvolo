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
/// Deterministic semantic-token validation, staleness, cancellation, failure and refresh coverage
/// using an explicit blocking fake backend (§20, §22, §23, §26, §30.10–30.25).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class SemanticTokensConcurrencyTests : IDisposable
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

    public SemanticTokensConcurrencyTests()
    {
        _workspace = TestWorkspace.CreateProject(["a.cvl", "b.cvl"]);
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

    private static InitializeRequestParams Capabilities(bool refresh = true)
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
                Workspace = new WorkspaceClientCapabilitiesPayload
                {
                    SemanticTokens = new SemanticTokensWorkspaceClientCapabilitiesPayload { RefreshSupport = refresh },
                },
            },
        };
    }

    private async Task StartAsync(bool refresh = true)
    {
        await _session.Client.InitializeWithAsync(Capabilities(refresh)).WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync(string name, int version, string text)
    {
        await _session.Client.NotifyDidOpenAsync(Uri(name), "cvolo", version, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf(name));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private static BackendSemanticToken Token(int start, int length, BackendSymbolKind kind = BackendSymbolKind.Function, BackendSemanticTokenModifiers modifiers = BackendSemanticTokenModifiers.Declaration)
        => new(new TextSpan(start, length), kind, modifiers);

    /// <summary>
    /// Asserts the last error the server actually wrote to the wire: the serialized
    /// JSON-RPC code and the <c>error.data</c> fields (<c>cvoloKind</c>, <c>retryable</c>,
    /// <c>reason</c>), not the in-process exception.
    /// </summary>
    private void AssertTypedWireError(string expectedKind, string? expectedReason)
    {
        JObject? error = _session.Server.LastJsonRpcError();
        Assert.NotNull(error);
        Assert.Equal(-32803, (int?)error!["code"]);

        JObject data = Assert.IsType<JObject>(error["data"]);
        Assert.Equal(expectedKind, (string?)data["cvoloKind"]);
        Assert.True((bool?)data["retryable"]);
        Assert.Equal(expectedReason, (string?)data["reason"]);
        Assert.Null(data["retryAfterMs"]);
    }

    [Fact]
    public async Task MalformedAndOverlappingTokens_AreRejectedWithoutCorruptingTheResponse()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int Twice(int value) { return 0; }\n");
        _backend.CannedSemanticTokens = new BackendSemanticTokenResult(
        [
            Token(4, 5),            // valid: "Twice"
            Token(0, 9999),         // out of range -> skipped
            Token(4, 3),            // overlaps the first -> skipped
            Token(16, 5, BackendSymbolKind.Parameter), // valid: "value"
        ]);

        JObject? result = await _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("semanticTokens");

        Assert.NotNull(result);
        var data = result!["data"]!.Values<int>().ToArray();
        Assert.Equal(10, data.Length); // exactly two tokens
        Assert.Equal(4, data[1]);      // "Twice" at char 4
        Assert.Equal(12, data[6]);     // "value" delta from char 4 to char 16
    }

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightTokens()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");
        _backend.CannedSemanticTokens = new BackendSemanticTokenResult([Token(4, 4)]);

        _backend.ArmNavigation();
        Task<JObject?> inFlight = _session.Client.SemanticTokensAsync(Uri("a.cvl"));
        Assert.True(_backend.WaitUntilNavigationEntered(TimeSpan.FromSeconds(5)), "backend semantic tokens should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri("a.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out var state) && state.Version.Value == 2, "a.cvl v2");
        _backend.ReleaseNavigation();

        // A typed server error carrying CvoloErrorData surfaces on the client as the base
        // RemoteInvocationException (data-bearing errors are not the type-less RemoteRpcException).
        RemoteInvocationException error = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => inFlight.WithTimeout("stale semantic tokens"));
        Assert.Equal(-32803, (int?)error.ErrorCode);
        AssertTypedWireError(CvoloErrorData.SupersededByQueuedInput, expectedReason: null);
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");
        _backend.CannedSemanticTokens = new BackendSemanticTokenResult([Token(4, 4)]);

        _backend.ArmNavigation();
        using var cts = new CancellationTokenSource();
        Task<JObject?> inFlight = _session.Client.SemanticTokensWithTokenAsync(Uri("a.cvl"), cts.Token);
        Assert.True(_backend.WaitUntilNavigationEntered(TimeSpan.FromSeconds(5)), "backend semantic tokens should be entered");

        cts.Cancel();
        await _session.Client.WaitUntilAsync(_session.Client.HasSentCancelRequest, "cancel request sent");
        await _session.Client.DrainNotificationsAsync();
        await Task.Delay(100);
        _backend.ReleaseNavigation();

        try
        {
            JObject? result = await inFlight.WithTimeout("cancelled semantic tokens");
            Assert.Fail($"Expected cancellation, but got {result}");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
        }

        Assert.True(_session.Client.HasSentCancelRequest(), "the client should have sent $/cancelRequest");
        Assert.NotNull(await _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("next semantic tokens"));
    }

    [Fact]
    public async Task BackendException_IsContained_AndNextRequestSucceeds()
    {
        await StartAsync();
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");

        _backend.ThrowOnNavigation = true;
        RemoteInvocationException error = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("failing semantic tokens"));
        Assert.Equal(-32803, (int?)error.ErrorCode);
        AssertTypedWireError(CvoloErrorData.BackendUnavailable, CvoloErrorData.BackendFailure);

        _backend.ThrowOnNavigation = false;
        _backend.CannedSemanticTokens = new BackendSemanticTokenResult([Token(4, 4)]);
        Assert.NotNull(await _session.Client.SemanticTokensAsync(Uri("a.cvl")).WithTimeout("next semantic tokens"));
    }

    [Fact]
    public async Task DidChange_WithRefreshSupport_SendsNoRefreshRequest()
    {
        await StartAsync(refresh: true);
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");

        _session.Server.ResetServerFrames();
        await _session.Client.NotifyDidChangeAsync(Uri("a.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });

        // The refresh coordinator coalesces on a 350ms quiet period. Wait past it so a regression that
        // reintroduced a per-edit global refresh would be observed instead of racing the assertion.
        await Task.Delay(450);
        await _session.Client.DrainNotificationsAsync();

        Assert.DoesNotContain(
            _session.Server.GetServerFrames(),
            frame => frame.Contains("workspace/semanticTokens/refresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DidChange_WithoutRefreshSupport_SendsNoRefreshRequest()
    {
        await StartAsync(refresh: false);
        await OpenAsync("a.cvl", 1, "int main() { return 0; }\n");

        _session.Server.ResetServerFrames();
        await _session.Client.NotifyDidChangeAsync(Uri("a.cvl"), 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        await _session.Client.DrainNotificationsAsync();

        Assert.DoesNotContain(
            _session.Server.GetServerFrames(),
            frame => frame.Contains("workspace/semanticTokens/refresh", StringComparison.Ordinal));
    }
}
