using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;
using Xunit;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Deterministic coverage of the textDocument/inlayHint transport: the requested range is honoured
/// exactly, positions are carried verbatim, a position that no longer exists is dropped instead of
/// clamped, and the usual staleness, cancellation and failure containment applies (§62, §84).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class InlayHintHandlerTests : IDisposable
{
    private const string Text = "int main() { return 0; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public InlayHintHandlerTests()
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

    private static LspRange Line(int start, int end) => new()
    {
        Start = new Position { Line = 0, Character = start },
        End = new Position { Line = 0, Character = end },
    };

    private async Task StartAsync()
    {
        await _session.Client.InitializeAsync().WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync(int version = 1, string text = Text)
    {
        await _session.Client.NotifyDidOpenAsync(Uri, "cvolo", version, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(() => _store.TryGet(coreUri, out _), "didOpen applied");
    }

    [Fact]
    public async Task Hints_CarryTheCompilerPositionLabelPaddingAndCategory()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedInlayHints =
        [
            new BackendInlayHint(4, BackendInlayHintKind.Type, "int", PaddingLeft: true),
            new BackendInlayHint(9, BackendInlayHintKind.Parameter, "value"),
            new BackendInlayHint(12, BackendInlayHintKind.ReceiverMutability, "mut", PaddingLeft: true, PaddingRight: true),
            new BackendInlayHint(20, BackendInlayHintKind.EnumValue, "Color.Red"),
            new BackendInlayHint(21, BackendInlayHintKind.GenericArgument, "i32"),
        ];

        JArray? hints = await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("inlayHint");

        Assert.Equal(5, hints!.Count);
        Assert.Equal(4, hints[0]!["position"]!["character"]!.Value<int>());
        Assert.Equal("int", hints[0]!["label"]!.Value<string>());
        Assert.True(hints[0]!["paddingLeft"]!.Value<bool>());
        Assert.False(hints[0]!["paddingRight"]!.Value<bool>());
        Assert.Equal("type", hints[0]!["kind"]!.Value<string>());
        Assert.Equal("parameter", hints[1]!["kind"]!.Value<string>());
        Assert.Equal("receiverMutability", hints[2]!["kind"]!.Value<string>());
        Assert.True(hints[2]!["paddingRight"]!.Value<bool>());
        Assert.Equal("enumValue", hints[3]!["kind"]!.Value<string>());
        Assert.Equal("genericArgument", hints[4]!["kind"]!.Value<string>());
    }

    [Theory]
    [InlineData("decimal", "offset 4, size 4, align 4, pad 3 before")]
    [InlineData("hex", "offset 0x04, size 0x04, align 0x04, pad 0x03 before")]
    [InlineData("decimalAndHex", "offset 4 (0x04), size 4 (0x04), align 4 (0x04), pad 3 (0x03) before")]
    public async Task AFieldLayoutHint_UsesTheSameOffsetNotationAsTheFieldLens(string format, string expected)
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedInlayHints =
        [
            new BackendInlayHint(
                23,
                BackendInlayHintKind.Layout,
                "offset 4, size 4, align 4, pad 3 before",
                PaddingLeft: true,
                FieldLayout: new BackendFieldLayoutInfo("Header", "Length", 4, 4, 4, 3)),
        ];

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.layout.offsetFormat"] = format })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();

        JArray? hints = await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("inlayHint");

        JToken hint = Assert.Single(hints!);
        Assert.Equal(expected, hint["label"]!.Value<string>());
        Assert.Equal("layout", hint["kind"]!.Value<string>());
        Assert.True(hint["paddingLeft"]!.Value<bool>());
    }

    [Fact]
    public async Task AFieldWithNoPrecedingPadding_ShowsNoPaddingFact()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedInlayHints =
        [
            new BackendInlayHint(
                4,
                BackendInlayHintKind.Layout,
                "offset 0, size 8, align 8",
                PaddingLeft: true,
                FieldLayout: new BackendFieldLayoutInfo("Header", "Kind", 0, 8, 8, 0)),
        ];

        JArray? hints = await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("inlayHint");

        Assert.Equal("offset 0, size 8, align 8", Assert.Single(hints!)["label"]!.Value<string>());
    }

    [Fact]
    public async Task Hints_AreOrderedByPosition()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedInlayHints =
        [
            new BackendInlayHint(20, BackendInlayHintKind.Type, "last"),
            new BackendInlayHint(4, BackendInlayHintKind.Type, "first"),
        ];

        JArray? hints = await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("inlayHint");

        Assert.Equal(
            ["first", "last"],
            hints!.Select(hint => hint["label"]!.Value<string>()).ToArray());
    }

    [Fact]
    public async Task HintOutsideTheCapturedText_IsDropped_NeverClamped()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedInlayHints =
        [
            new BackendInlayHint(4, BackendInlayHintKind.Type, "int"),
            new BackendInlayHint(9000, BackendInlayHintKind.Type, "beyond the end"),
            new BackendInlayHint(-2, BackendInlayHintKind.Type, "before the start"),
        ];

        JArray? hints = await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("inlayHint");

        Assert.Equal("int", Assert.Single(hints!)["label"]!.Value<string>());
    }

    [Fact]
    public async Task RequestedRange_IsForwardedExactly_AndNotWidened()
    {
        await StartAsync();
        await OpenAsync();

        await _session.Client.InlayHintsAsync(Uri, Line(5, 9)).WithTimeout("inlayHint");

        (TextSpan range, BackendInlayHintOptions? _) = _backend.LastInlayHintRequest;
        Assert.Equal(5, range.Start);
        Assert.Equal(4, range.Length);
    }

    [Fact]
    public async Task RangeThatDoesNotMapToTheText_YieldsNoHints()
    {
        await StartAsync();
        await OpenAsync();
        var range = new LspRange
        {
            Start = new Position { Line = 0, Character = 0 },
            End = new Position { Line = 9, Character = 0 },
        };

        Assert.Null(await _session.Client.InlayHintsAsync(Uri, range).WithTimeout("inlayHint"));
        Assert.Equal(0, _backend.InlayHintCalls);
    }

    [Fact]
    public async Task UnopenedDocument_YieldsNoHints()
    {
        await StartAsync();

        Assert.Null(await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("inlayHint"));
        Assert.Equal(0, _backend.InlayHintCalls);
    }

    [Fact]
    public async Task Settings_AreForwardedToTheBackend_AndChangedLive()
    {
        await StartAsync();
        await OpenAsync();

        await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("inlayHint");

        (_, BackendInlayHintOptions? options) = _backend.LastInlayHintRequest;
        Assert.NotNull(options);
        Assert.True(options!.Types);
        Assert.True(options.Parameters);
        Assert.True(options.ReceiverMutability);
        Assert.False(options.Layout);
        Assert.False(options.EnumValues);
        Assert.False(options.GenericArguments);

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject
            {
                ["cvolo.inlayHints.layout"] = true,
                ["cvolo.inlayHints.enumValues"] = true,
                ["cvolo.inlayHints.types"] = false,
            })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();
        await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("inlayHint after configuration");

        (_, options) = _backend.LastInlayHintRequest;
        Assert.NotNull(options);
        Assert.False(options!.Types);
        Assert.True(options.Layout);
        Assert.True(options.EnumValues);
        Assert.True(options.Parameters);
    }

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightHints()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedInlayHints = [new BackendInlayHint(4, BackendInlayHintKind.Type, "int")];

        _backend.ArmEditorIntelligence();
        Task<JArray?> inFlight = _session.Client.InlayHintsAsync(Uri, Line(0, 24));
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri, 2, new TextDocumentContentChangeEvent { Text = "int main() { return 1; }\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _store.TryGet(coreUri, out var state) && state.Version.Value == 2,
            "document advanced to v2");
        _backend.ReleaseEditorIntelligence();

        Assert.Null(await inFlight.WithTimeout("stale inlayHint"));
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedInlayHints = [new BackendInlayHint(4, BackendInlayHintKind.Type, "int")];

        _backend.ArmEditorIntelligence();
        using var cts = new CancellationTokenSource();
        Task<JArray?> inFlight = _session.Client.InlayHintsWithTokenAsync(Uri, Line(0, 24), cts.Token);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        cts.Cancel();
        await _session.Client.WaitUntilAsync(_session.Client.HasSentCancelRequest, "cancel request sent");
        await _session.Client.DrainNotificationsAsync();
        _backend.ReleaseEditorIntelligence();

        try
        {
            JArray? result = await inFlight.WithTimeout("cancelled inlayHint");
            Assert.Fail($"Expected cancellation, but inlayHint returned {result?.Count} hints.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed and no stale success is returned.
        }

        Assert.NotNull(await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("next inlayHint"));
    }

    [Fact]
    public async Task BackendException_IsContained_AndNextRequestSucceeds()
    {
        await StartAsync();
        await OpenAsync();

        _backend.ThrowOnEditorIntelligence = true;
        Assert.Null(await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("failing inlayHint"));

        _backend.ThrowOnEditorIntelligence = false;
        _backend.CannedInlayHints = [new BackendInlayHint(4, BackendInlayHintKind.Type, "int")];
        JArray? hints = await _session.Client.InlayHintsAsync(Uri, Line(0, 24)).WithTimeout("next inlayHint");
        Assert.Single(hints!);
    }
}
