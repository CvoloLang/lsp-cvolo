using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;


namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// A decoration setting is presentation, not semantics: changing one takes effect through a
/// configuration notification and a refresh, never through a restart of the session, and the client is
/// only asked to refresh when it said it can (§59, §60, §64, §65).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class EditorIntelligenceConfigurationTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public EditorIntelligenceConfigurationTests()
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

    private static InitializeRequestParams Capabilities(bool codeLensRefresh, bool inlayHintRefresh) => new()
    {
        Capabilities = new ClientCapabilitiesPayload
        {
            Workspace = new WorkspaceClientCapabilitiesPayload
            {
                CodeLens = new CodeLensWorkspaceClientCapabilitiesPayload { RefreshSupport = codeLensRefresh },
                InlayHint = new InlayHintWorkspaceClientCapabilitiesPayload { RefreshSupport = inlayHintRefresh },
            },
        },
    };

    private async Task StartAsync(bool codeLensRefresh = true, bool inlayHintRefresh = true)
    {
        await _session.Client
            .InitializeWithAsync(Capabilities(codeLensRefresh, inlayHintRefresh))
            .WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync()
    {
        await _session.Client.NotifyDidOpenAsync(Uri, "cvolo", 1, "int main() { return 0; }\n").WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private bool SawRefreshRequest(string method) =>
        _session.Server.GetServerFrames().Any(frame => frame.Contains(method, StringComparison.Ordinal));

    private async Task WaitForRefreshRequestAsync(string method)
    {
        await _session.Client.WaitUntilAsync(
            () => SawRefreshRequest(method),
            $"the {method} request");
    }

    [Fact]
    public async Task DidChangeConfiguration_AsksTheClientToRefreshLensesAndHints()
    {
        await StartAsync();
        await OpenAsync();
        _session.Server.ResetServerFrames();

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.codeLens.references"] = false })
            .WithTimeout("didChangeConfiguration");

        await WaitForRefreshRequestAsync("workspace/codeLens/refresh");
        await WaitForRefreshRequestAsync("workspace/inlayHint/refresh");
    }

    [Fact]
    public async Task DidChangeConfiguration_WithoutClientSupport_AsksForNoRefresh()
    {
        await StartAsync(codeLensRefresh: false, inlayHintRefresh: false);
        await OpenAsync();
        _session.Server.ResetServerFrames();

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.codeLens.references"] = false })
            .WithTimeout("didChangeConfiguration");

        // Notifications are fire-and-forget and StreamJsonRpc may schedule
        // the following CodeLens request before didChangeConfiguration has
        // executed. A single RPC is not an acknowledged configuration barrier.
        // Retry a bounded *observable* CodeLens request until the effective
        // settings reach the backend; do not weaken the negative refresh checks.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens after configuration");
            if (_backend.LastCodeLensOptions?.References == false)
                break;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await _session.Client.DrainNotificationsAsync();

        Assert.NotNull(_backend.LastCodeLensOptions);
        Assert.False(_backend.LastCodeLensOptions.References);
        Assert.False(SawRefreshRequest("workspace/codeLens/refresh"));
        Assert.False(SawRefreshRequest("workspace/inlayHint/refresh"));
    }

    [Fact]
    public async Task DidChangeConfiguration_WithoutASettingsPayload_IsIgnored()
    {
        await StartAsync();
        await OpenAsync();
        _session.Server.ResetServerFrames();

        await _session.Client.NotifyDidChangeConfigurationAsync(null).WithTimeout("didChangeConfiguration");

        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens after configuration");
        await _session.Client.DrainNotificationsAsync();

        Assert.True(_backend.LastCodeLensOptions!.References);
        Assert.False(SawRefreshRequest("workspace/codeLens/refresh"));
    }

    [Fact]
    public async Task DidChangeConfiguration_ThatChangesNothing_SendsNoRefresh()
    {
        await StartAsync();
        await OpenAsync();
        _session.Server.ResetServerFrames();

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject
            {
                ["cvolo.codeLens.references"] = true,
                ["cvolo.codeLens.layout"] = true,
                ["cvolo.inlayHints.types"] = true,
            })
            .WithTimeout("didChangeConfiguration");

        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens after configuration");
        await _session.Client.DrainNotificationsAsync();

        Assert.False(SawRefreshRequest("workspace/codeLens/refresh"));
        Assert.False(SawRefreshRequest("workspace/inlayHint/refresh"));
    }

    [Fact]
    public async Task MalformedSettingValue_KeepsTheCurrentSetting_AndSendsNoRefresh()
    {
        await StartAsync();
        await OpenAsync();
        _session.Server.ResetServerFrames();

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.codeLens.references"] = "off" })
            .WithTimeout("didChangeConfiguration");

        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens after configuration");
        await _session.Client.DrainNotificationsAsync();

        // A value that is not a boolean is not a preference the user expressed.
        Assert.True(_backend.LastCodeLensOptions!.References);
        Assert.False(SawRefreshRequest("workspace/codeLens/refresh"));
    }

    [Fact]
    public async Task UnrelatedSettings_AreIgnored()
    {
        await StartAsync();
        await OpenAsync();
        _session.Server.ResetServerFrames();

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["editor.fontSize"] = 14 })
            .WithTimeout("didChangeConfiguration");

        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens after configuration");
        await _session.Client.DrainNotificationsAsync();

        Assert.True(_backend.LastCodeLensOptions!.References);
        Assert.False(SawRefreshRequest("workspace/codeLens/refresh"));
    }

    [Fact]
    public async Task AChangeToOneDecoration_RefreshesExactlyTheOnesTheClientSupports()
    {
        // One settings notification produces one refresh pass; within that pass each decoration is
        // asked for only if the client advertised support for it.
        await StartAsync(codeLensRefresh: true, inlayHintRefresh: false);
        await OpenAsync();
        _session.Server.ResetServerFrames();

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.inlayHints.types"] = false })
            .WithTimeout("didChangeConfiguration");

        await WaitForRefreshRequestAsync("workspace/codeLens/refresh");
        await _session.Client.InlayHintsAsync(
            Uri,
            new LspRange
            {
                Start = new Position { Line = 0, Character = 0 },
                End = new Position { Line = 0, Character = 24 },
            })
            .WithTimeout("inlayHint after configuration");
        await _session.Client.DrainNotificationsAsync();

        Assert.False(SawRefreshRequest("workspace/inlayHint/refresh"));
    }
}
