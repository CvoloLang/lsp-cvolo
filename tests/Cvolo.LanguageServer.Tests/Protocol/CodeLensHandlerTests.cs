using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Deterministic coverage of the textDocument/codeLens transport: ranges, command arguments, the
/// presentation order, the refusal to invent a position, and the usual staleness, cancellation and
/// failure containment (§83).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class CodeLensHandlerTests : IDisposable
{
    private const string Text = "int main() { return 0; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public CodeLensHandlerTests()
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

    private async Task StartAsync(bool anchoredLens = true)
    {
        if (anchoredLens)
        {
            await _session.Client.InitializeWithAnchoredLensAsync().WithTimeout("initialize");
        }
        else
        {
            await _session.Client.InitializeAsync().WithTimeout("initialize");
        }

        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync(int version = 1, string text = Text)
    {
        await _session.Client.NotifyDidOpenAsync(Uri, "cvolo", version, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    private static BackendCodeLensInfo References(int start, int length, string title) =>
        new(
            new TextSpan(start, length),
            BackendCodeLensKind.References,
            title,
            new BackendCodeLensCommand("cvolo.showReferences", [new BackendCodeLensPositionArgument(start)]));

    [Fact]
    public async Task Lenses_MapRangeAndCarryAnOpaqueAnchoredCommand()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [References(4, 4, "3 references")];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        JToken lens = Assert.Single(lenses!);
        Assert.Equal(0, lens["range"]!["start"]!["line"]!.Value<int>());
        Assert.Equal(4, lens["range"]!["start"]!["character"]!.Value<int>());
        Assert.Equal(0, lens["range"]!["end"]!["line"]!.Value<int>());
        Assert.Equal(8, lens["range"]!["end"]!["character"]!.Value<int>());

        Assert.Equal("3 references", lens["command"]!["title"]!.Value<string>());
        Assert.Equal("cvolo.executeAnchoredLens", lens["command"]!["command"]!.Value<string>());

        // The clickable lens carries a single opaque request token, never a positional
        // (uri, line, character) triple that could go stale between paint and click. The URI it
        // names must still be the one the client understands: a bare local path parses as a "d:"
        // scheme and cannot be resolved.
        JArray arguments = Assert.IsType<JArray>(lens["command"]!["arguments"]!);
        JObject request = Assert.IsType<JObject>(Assert.Single(arguments));
        Assert.Equal(3, request["schemaVersion"]!.Value<int>());
        Assert.Equal(Uri.AbsoluteUri, request["wireUri"]!.Value<string>());
        Assert.StartsWith("file:///", request["wireUri"]!.Value<string>(), StringComparison.Ordinal);
        Assert.Equal(_workspace.PathOf("a.cvl"), Uri.LocalPath);
        Assert.False(string.IsNullOrEmpty(request["lensToken"]!.Value<string>()));
        Assert.Null(request["line"]);
        Assert.Null(request["character"]);
    }

    [Fact]
    public async Task Lenses_AreOrderedReferencesThenLayoutThenNativeInterop()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            new BackendCodeLensInfo(new TextSpan(4, 4), BackendCodeLensKind.NativeInterop, "native"),
            new BackendCodeLensInfo(new TextSpan(4, 4), BackendCodeLensKind.Layout, "size 24 B"),
            References(4, 4, "1 reference"),
        ];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        Assert.Equal(
            ["1 reference", "size 24 B", "native"],
            lenses!.Select(lens => lens["command"]!["title"]!.Value<string>()).ToArray());
        Assert.Equal(
            ["cvolo.executeAnchoredLens", string.Empty, string.Empty],
            lenses!.Select(lens => lens["command"]!["command"]!.Value<string>()).ToArray());
    }

    [Fact]
    public async Task Lenses_AreOrderedByPositionRegardlessOfBackendEnumerationOrder()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            References(18, 1, "2 references"),
            References(4, 4, "3 references"),
        ];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        Assert.Equal(
            [4, 18],
            lenses!.Select(lens => lens["range"]!["start"]!["character"]!.Value<int>()).ToArray());
    }

    [Fact]
    public async Task LensWithoutACommand_StillCarriesTheCompilerText()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            new BackendCodeLensInfo(new TextSpan(4, 4), BackendCodeLensKind.NativeInterop, "import kernel32 | CreateFileW"),
        ];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        JToken lens = Assert.Single(lenses!);
        Assert.Equal("import kernel32 | CreateFileW", lens["command"]!["title"]!.Value<string>());
        Assert.Equal(string.Empty, lens["command"]!["command"]!.Value<string>());
    }

    [Fact]
    public async Task LensOutsideTheCapturedText_IsDropped_RangeIsNeverClamped()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            References(4, 4, "3 references"),
            References(9000, 4, "0 references"),
            References(-3, 2, "negative"),
        ];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        Assert.Equal("3 references", Assert.Single(lenses!)["command"]!["title"]!.Value<string>());
    }

    [Fact]
    public async Task AReferencesLens_IsAnchoredByItsRangeStart_NotTheBackendPositionArgument()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            new BackendCodeLensInfo(
                new TextSpan(4, 4),
                BackendCodeLensKind.References,
                "3 references",
                new BackendCodeLensCommand("cvolo.showReferences", [new BackendCodeLensPositionArgument(9000)])),
        ];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        JToken lens = Assert.Single(lenses!);
        Assert.Equal("3 references", lens["command"]!["title"]!.Value<string>());

        // The position the backend supplied (9000) is not a wire position any more. The lens is
        // anchored to its verified range start, and the server re-resolves at execution time, so an
        // out-of-range backend argument can no longer decide clickability.
        Assert.Equal("cvolo.executeAnchoredLens", lens["command"]!["command"]!.Value<string>());
    }

    [Fact]
    public async Task WithoutTheAnchoredLensCapability_LensesAreInertText_AndNeverTheLegacyCommand()
    {
        await StartAsync(anchoredLens: false);
        await OpenAsync();
        _backend.CannedCodeLenses = [References(4, 4, "3 references")];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        JToken lens = Assert.Single(lenses!);
        Assert.Equal("3 references", lens["command"]!["title"]!.Value<string>());
        Assert.Equal(string.Empty, lens["command"]!["command"]!.Value<string>());
        Assert.Empty(Assert.IsType<JArray>(lens["command"]!["arguments"]!));
    }

    [Fact]
    public async Task UnopenedDocument_YieldsNoLenses()
    {
        await StartAsync();

        Assert.Null(await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens"));
        Assert.Equal(0, _backend.CodeLensCalls);
    }

    [Fact]
    public async Task Settings_AreForwardedToTheBackend()
    {
        await StartAsync();
        await OpenAsync();

        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        BackendCodeLensOptions? options = _backend.LastCodeLensOptions;
        Assert.NotNull(options);
        Assert.True(options!.References);
        Assert.True(options.Layout);
        Assert.False(options.Members);
        Assert.True(options.NativeInterop);

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject
            {
                ["cvolo.codeLens.references"] = false,
                ["cvolo.codeLens.members"] = true,
            })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();
        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens after configuration");

        options = _backend.LastCodeLensOptions;
        Assert.NotNull(options);
        Assert.False(options!.References);
        Assert.True(options.Layout);
        Assert.True(options.Members);
        Assert.True(options.NativeInterop);
    }

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightLenses()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [References(4, 4, "3 references")];

        _backend.ArmEditorIntelligence();
        Task<JArray?> inFlight = _session.Client.CodeLensAsync(Uri);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri, 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _store.TryGet(coreUri, out var state) && state.Version.Value == 2,
            "document advanced to v2");
        _backend.ReleaseEditorIntelligence();

        Assert.Null(await inFlight.WithTimeout("stale codeLens"));
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [References(4, 4, "3 references")];

        _backend.ArmEditorIntelligence();
        using var cts = new CancellationTokenSource();
        Task<JArray?> inFlight = _session.Client.CodeLensWithTokenAsync(Uri, cts.Token);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        cts.Cancel();
        await _session.Client.WaitUntilAsync(_session.Client.HasSentCancelRequest, "cancel request sent");
        await _session.Client.DrainNotificationsAsync();
        _backend.ReleaseEditorIntelligence();

        try
        {
            JArray? result = await inFlight.WithTimeout("cancelled codeLens");
            Assert.Fail($"Expected cancellation, but codeLens returned {result?.Count} lenses.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed and no stale success is returned.
        }

        Assert.NotNull(await _session.Client.CodeLensAsync(Uri).WithTimeout("next codeLens"));
    }

    [Fact]
    public async Task BackendException_IsContained_AndNextRequestSucceeds()
    {
        await StartAsync();
        await OpenAsync();

        _backend.ThrowOnEditorIntelligence = true;
        Assert.Null(await _session.Client.CodeLensAsync(Uri).WithTimeout("failing codeLens"));

        _backend.ThrowOnEditorIntelligence = false;
        _backend.CannedCodeLenses = [References(4, 4, "3 references")];
        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("next codeLens");
        Assert.Single(lenses!);
    }

    private static BackendCodeLensInfo Field(int start, int length, long offset, long size, long alignment, long paddingBefore) =>
        new(
            new TextSpan(start, length),
            BackendCodeLensKind.Layout,
            $"offset {offset}B, size {size}B, align {alignment}B",
            new BackendCodeLensCommand("cvolo.showTypeLayout", [new BackendCodeLensPositionArgument(start)]),
            new BackendFieldLayoutInfo("Header", "Length", offset, size, alignment, paddingBefore));

    [Fact]
    public async Task AFieldLayoutLens_ShowsTheCompilerNumbersAndOpensTheTypeLayout()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [Field(18, 6, 4, 4, 4, 3)];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        JToken lens = Assert.Single(lenses!);
        Assert.Equal("offset 4B, size 4B, align 4B, pad 3B before", lens["command"]!["title"]!.Value<string>());

        // A field is not a type, so the action opens the layout of the type that stores it, anchored
        // at the field's own declaration. The client is handed an opaque token, never a parsed name
        // or a raw position.
        Assert.Equal("cvolo.executeAnchoredLens", lens["command"]!["command"]!.Value<string>());
        JArray arguments = Assert.IsType<JArray>(lens["command"]!["arguments"]!);
        JObject request = Assert.IsType<JObject>(Assert.Single(arguments));
        Assert.Equal(3, request["schemaVersion"]!.Value<int>());
        Assert.Equal(Uri.AbsoluteUri, request["wireUri"]!.Value<string>());
        Assert.False(string.IsNullOrEmpty(request["lensToken"]!.Value<string>()));
    }

    [Fact]
    public async Task AFieldWithNoPrecedingPadding_ShowsNoPaddingFact()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [Field(4, 4, 0, 8, 8, 0)];

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        Assert.Equal("offset 0B, size 8B, align 8B", Assert.Single(lenses!)["command"]!["title"]!.Value<string>());
    }

    [Fact]
    public async Task TheFieldLensGateAndSubFlags_ReachTheBackend()
    {
        await StartAsync();
        await OpenAsync();

        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");
        Assert.NotNull(_backend.LastCodeLensOptions);
        Assert.False(_backend.LastCodeLensOptions!.Members);
        Assert.False(_backend.LastCodeLensOptions.FieldLayout);

        // The master gate opens both per-field kinds at once, and each sub-flag can still veto one.
        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject
            {
                ["cvolo.codeLens.fields"] = true,
                ["cvolo.codeLens.fieldReferences"] = true,
                ["cvolo.codeLens.fieldLayout"] = true,
            })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();
        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens after configuration");
        Assert.True(_backend.LastCodeLensOptions!.Members);
        Assert.True(_backend.LastCodeLensOptions.FieldLayout);

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.codeLens.fields"] = false })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();
        await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens after the gate closes");
        Assert.False(_backend.LastCodeLensOptions!.Members);
        Assert.False(_backend.LastCodeLensOptions.FieldLayout);
    }

    [Theory]
    [InlineData("decimal", "offset 4B, size 4B, align 4B, pad 3B before")]
    [InlineData("hex", "offset 0x04, size 0x04, align 0x04, pad 0x03 before")]
    [InlineData("decimalAndHex", "offset 4B (0x04), size 4B (0x04), align 4B (0x04), pad 3B (0x03) before")]
    public async Task TheOffsetFormatRewritesTheFieldLensNotationWithoutChangingANumber(string format, string expected)
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [Field(18, 6, 4, 4, 4, 3)];

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.layout.offsetFormat"] = format })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");
        Assert.Equal(expected, Assert.Single(lenses!)["command"]!["title"]!.Value<string>());
    }

    [Fact]
    public async Task AnUnknownOffsetFormat_KeepsTheCurrentNotation()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses = [Field(18, 6, 4, 4, 4, 3)];

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.layout.offsetFormat"] = "hexadecimal" })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");
        Assert.Equal(
            "offset 4B, size 4B, align 4B, pad 3B before",
            Assert.Single(lenses!)["command"]!["title"]!.Value<string>());
    }

    [Fact]
    public async Task ATypeLayoutLens_KeepsTheCompilerTitleRegardlessOfTheOffsetFormat()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedCodeLenses =
        [
            new BackendCodeLensInfo(new TextSpan(4, 4), BackendCodeLensKind.Layout, "size 8B, align 4B, padding 3B"),
        ];

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.layout.offsetFormat"] = "hex" })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();

        JArray? lenses = await _session.Client.CodeLensAsync(Uri).WithTimeout("codeLens");

        // Only offsets have a notation. A size, an alignment and a padding total stay decimal byte
        // counts, so the type summary is unchanged by the setting.
        Assert.Equal("size 8B, align 4B, padding 3B", Assert.Single(lenses!)["command"]!["title"]!.Value<string>());
    }
}
