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
/// Deterministic coverage of the <c>cvolo/typeLayout</c> request that backs the read-only layout
/// view. Every number and every classification leaves the server exactly as the compiler stated it,
/// because the client is allowed to format the table but not to compute it (§19, §20, §22, §57).
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class TypeLayoutHandlerTests : IDisposable
{
    private const string Text = "struct Point { int x; }\n";

    private readonly TestWorkspace _workspace;
    private readonly BlockingBackend _backend;
    private readonly DocumentStore _store;
    private readonly ProtocolSession _session;

    public TypeLayoutHandlerTests()
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

    private static BackendTypeLayoutInspection Record() => new(
        "Point",
        "x64-pc-windows-msvc",
        24,
        8,
        13,
        11,
        Stride: null,
        ElementCount: null,
        ElementSize: null,
        ElementAlignment: null,
        [
            new BackendTypeLayoutMember("x", "int32", 0, 4, 4),
            new BackendTypeLayoutMember("y", "int32", 4, 4, 4),
            new BackendTypeLayoutMember("id", "uint64", 16, 8, 8),
        ],
        [new BackendTypeLayoutPadding(8, 8, BackendTypeLayoutPaddingKind.Internal)]);

    [Fact]
    public void HoverLayout_ListsFieldSizesOnSeparateLinesWithoutOffsets()
    {
        var layout = new BackendTypeLayoutInspection(
            "App.Window", "x86_64-pc-windows-msvc",
            24, 8, 24, 0,
            null, null, null, null,
            [
                new BackendTypeLayoutMember("a", "int[]", 0, 16, 8),
                new BackendTypeLayoutMember("glfwWindow", "void*", 16, 8, 8),
            ],
            []);
        var result = HoverHandler.FormatCompactLayout(layout);

        Assert.Equal("24B · align 8B\n\na: 16B  \nglfwWindow: 8B", result);
    }

    [Fact]
    public void HoverLayout_PlainTextDoesNotLeakMarkdownDecorations()
    {
        var layout = new BackendTypeLayoutInspection(
            "App.Window", "x86_64-pc-windows-msvc",
            24, 8, 24, 0,
            null, null, null, null,
            [
                new BackendTypeLayoutMember("a", "int[]", 0, 16, 8),
                new BackendTypeLayoutMember("glfwWindow", "void*", 16, 8, 8),
            ],
            []);

        Assert.Equal("24B · align 8B\n\na: 16B\nglfwWindow: 8B",
            HoverHandler.FormatCompactLayout(layout, markdown: false));
    }

    [Fact]
    public void HoverLayout_LimitsLongListsWithoutInventingOffsets()
    {
        var fields = Enumerable.Range(0, 8)
            .Select(i => new BackendTypeLayoutMember($"field{i}", "int", i * 4, 4, 4))
            .ToArray();
        var layout = new BackendTypeLayoutInspection(
            "ManyFields", "x64", 32, 4, 32, 0,
            null, null, null, null, fields, []);
        var hover = HoverHandler.FormatCompactLayout(layout, markdown: false);

        Assert.NotNull(hover);
        Assert.Contains("field0: 4B", hover);
        Assert.Contains("field5: 4B", hover);
        Assert.DoesNotContain("field6:", hover);
        Assert.Contains("… +2 more fields", hover);
        Assert.DoesNotContain("+0:", hover);
    }

    [Fact]
    public void HoverLayout_OmitsUnknownFactsAndHonorsPadding()
    {
        Assert.Null(HoverHandler.FormatCompactLayout(null));
        var layout = new BackendTypeLayoutInspection(
            "App.Payload", "x86_64-pc-windows-msvc",
            16, 8, 9, 7,
            null, null, null, null,
            [new BackendTypeLayoutMember("value", "byte", 0, 1, 1)],
            []);
        Assert.Equal("16B · align 8B · padding 7B\n\nvalue: 1B",
            HoverHandler.FormatCompactLayout(layout));
    }

    [Fact]
    public async Task Layout_ReportsTheCompilerNumbersAndTheTargetIdentity()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedTypeLayout = Record();

        JObject? layout = await _session.Client.TypeLayoutJsonAsync(Uri, 0, 7).WithTimeout("cvolo/typeLayout");

        Assert.NotNull(layout);
        Assert.Equal("Point", layout!["typeDisplay"]!.Value<string>());
        Assert.Equal("x64-pc-windows-msvc", layout["targetDisplay"]!.Value<string>());
        Assert.Equal(24, layout["size"]!.Value<long>());
        Assert.Equal(8, layout["alignment"]!.Value<long>());
        Assert.Equal(13, layout["payloadSize"]!.Value<long>());
        Assert.Equal(11, layout["paddingSize"]!.Value<long>());

        JArray members = Assert.IsType<JArray>(layout["members"]!);
        Assert.Equal(3, members.Count);
        Assert.Equal("x", members[0]!["name"]!.Value<string>());
        Assert.Equal("int32", members[0]!["typeDisplay"]!.Value<string>());
        Assert.Equal(0, members[0]!["offset"]!.Value<long>());
        Assert.Equal(4, members[0]!["size"]!.Value<long>());
        Assert.Equal(4, members[0]!["alignment"]!.Value<long>());
        Assert.Equal("id", members[2]!["name"]!.Value<string>());
        Assert.Equal(16, members[2]!["offset"]!.Value<long>());
    }

    [Fact]
    public async Task Padding_KeepsTheCompilersOwnClassification()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedTypeLayout = new BackendTypeLayoutInspection(
            "Point",
            "x64-pc-windows-msvc",
            24,
            8,
            13,
            11,
            null,
            null,
            null,
            null,
            [],
            [
                new BackendTypeLayoutPadding(8, 8, BackendTypeLayoutPaddingKind.Internal),
                new BackendTypeLayoutPadding(24, 3, BackendTypeLayoutPaddingKind.Tail),
            ]);

        JObject? layout = await _session.Client.TypeLayoutJsonAsync(Uri, 0, 7).WithTimeout("cvolo/typeLayout");

        JArray padding = Assert.IsType<JArray>(layout!["padding"]!);
        Assert.Equal(2, padding.Count);
        Assert.Equal("internal", padding[0]!["kind"]!.Value<string>());
        Assert.Equal(8, padding[0]!["offset"]!.Value<long>());
        Assert.Equal(8, padding[0]!["size"]!.Value<long>());
        Assert.Equal("tail", padding[1]!["kind"]!.Value<string>());
        Assert.Equal(24, padding[1]!["offset"]!.Value<long>());
    }

    [Fact]
    public async Task ArrayLayout_CarriesTheElementFactsTheCompilerReported()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedTypeLayout = new BackendTypeLayoutInspection(
            "int32[4]",
            "x64-pc-linux-gnu",
            16,
            4,
            16,
            0,
            4,
            4,
            4,
            4,
            [],
            []);

        JObject? layout = await _session.Client.TypeLayoutJsonAsync(Uri, 0, 7).WithTimeout("cvolo/typeLayout");

        Assert.Equal(4, layout!["stride"]!.Value<long>());
        Assert.Equal(4, layout["elementCount"]!.Value<long>());
        Assert.Equal(4, layout["elementSize"]!.Value<long>());
        Assert.Equal(4, layout["elementAlignment"]!.Value<long>());
    }

    [Fact]
    public async Task RecordLayout_OmitsTheElementFactsItDoesNotHave()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedTypeLayout = Record();

        JObject? layout = await _session.Client.TypeLayoutJsonAsync(Uri, 0, 7).WithTimeout("cvolo/typeLayout");

        // An absent element count is absent, not zero: zero is a fact the compiler did not state.
        Assert.Null(layout!["stride"]);
        Assert.Null(layout["elementCount"]);
        Assert.Null(layout["elementSize"]);
        Assert.Null(layout["elementAlignment"]);
    }

    [Fact]
    public async Task NoLayoutAtThePosition_YieldsNothing()
    {
        await StartAsync();
        await OpenAsync();

        Assert.Null(await _session.Client.TypeLayoutAsync(Uri, 0, 7).WithTimeout("cvolo/typeLayout"));
        Assert.Equal(1, _backend.TypeLayoutCalls);
    }

    [Fact]
    public async Task PositionThatDoesNotMapToTheText_YieldsNothing()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedTypeLayout = Record();

        Assert.Null(await _session.Client.TypeLayoutAsync(Uri, 9, 0).WithTimeout("cvolo/typeLayout"));
        Assert.Equal(0, _backend.TypeLayoutCalls);
    }

    [Fact]
    public async Task UnopenedDocument_YieldsNoLayout()
    {
        await StartAsync();
        _backend.CannedTypeLayout = Record();

        Assert.Null(await _session.Client.TypeLayoutAsync(Uri, 0, 7).WithTimeout("cvolo/typeLayout"));
        Assert.Equal(0, _backend.TypeLayoutCalls);
    }

    [Fact]
    public async Task SameDocumentEdit_DiscardsInFlightLayout()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedTypeLayout = Record();

        _backend.ArmEditorIntelligence();
        Task<TypeLayoutResponse?> inFlight = _session.Client.TypeLayoutAsync(Uri, 0, 7);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        await _session.Client.NotifyDidChangeAsync(Uri, 2, new TextDocumentContentChangeEvent { Text = "struct Point { int y; }\n" });
        var coreUri = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _store.TryGet(coreUri, out var state) && state.Version.Value == 2,
            "document advanced to v2");
        _backend.ReleaseEditorIntelligence();

        Assert.Null(await inFlight.WithTimeout("stale cvolo/typeLayout"));
    }

    [Fact]
    public async Task Cancellation_IsObserved_AndSessionSurvives()
    {
        await StartAsync();
        await OpenAsync();
        _backend.CannedTypeLayout = Record();

        _backend.ArmEditorIntelligence();
        using var cts = new CancellationTokenSource();
        Task<TypeLayoutResponse?> inFlight = _session.Client.TypeLayoutWithTokenAsync(Uri, 0, 7, cts.Token);
        Assert.True(_backend.WaitUntilEditorIntelligenceEntered(TimeSpan.FromSeconds(5)), "backend should be entered");

        cts.Cancel();
        await _session.Client.WaitUntilAsync(_session.Client.HasSentCancelRequest, "cancel request sent");
        await _session.Client.DrainNotificationsAsync();
        _backend.ReleaseEditorIntelligence();

        try
        {
            TypeLayoutResponse? result = await inFlight.WithTimeout("cancelled cvolo/typeLayout");
            Assert.Fail($"Expected cancellation, but the layout request returned {result?.TypeDisplay}.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or RemoteRpcException)
        {
            // Expected: cancellation is observed and no stale success is returned.
        }

        Assert.NotNull(await _session.Client.TypeLayoutAsync(Uri, 0, 7).WithTimeout("next cvolo/typeLayout"));
    }

    [Fact]
    public async Task MemberNavigation_IsMappedToTheResolvedSourceLocations()
    {
        await StartAsync();
        await OpenAsync();

        // The compiler resolves every target; the handler only turns the compiler's character spans
        // into line/character locations using the text captured with the layout. Nothing here is
        // derived from the displayed table (§19, §39.3).
        var document = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        _backend.CannedTypeLayout = new BackendTypeLayoutInspection(
            "Point",
            "x64-pc-windows-msvc",
            8,
            4,
            8,
            0,
            null,
            null,
            null,
            null,
            [
                new BackendTypeLayoutMember(
                    "x",
                    "int32",
                    0,
                    4,
                    4,
                    new BackendTypeLayoutMemberNavigation(
                        "int32 Point.x",
                        "How far the point lies from the origin.",
                        new BackendDefinitionTarget(document, new TextSpan(20, 1), new TextSpan(20, 1)),
                        null,
                        null)),
            ],
            [],
            Definition: new BackendDefinitionTarget(document, new TextSpan(7, 5), new TextSpan(7, 5)),
            DocumentTexts: new Dictionary<DocumentUri, string> { [document] = Text });

        JObject? layout = await _session.Client.TypeLayoutJsonAsync(Uri, 0, 7).WithTimeout("cvolo/typeLayout");

        JToken definition = layout!["definition"]!;
        Assert.Equal(Uri.AbsoluteUri, definition["uri"]!.Value<string>());
        Assert.Equal(0, definition["range"]!["start"]!["line"]!.Value<int>());
        Assert.Equal(7, definition["range"]!["start"]!["character"]!.Value<int>());
        Assert.Equal(12, definition["range"]!["end"]!["character"]!.Value<int>());

        JToken navigation = layout["members"]![0]!["navigation"]!;
        Assert.Equal("int32 Point.x", navigation["signature"]!.Value<string>());
        Assert.Equal("How far the point lies from the origin.", navigation["documentation"]!.Value<string>());

        JToken field = navigation["definition"]!;
        Assert.Equal(20, field["range"]!["start"]!["character"]!.Value<int>());
        Assert.Null(navigation["typeDefinition"]);
        Assert.Null(navigation["nestedLayout"]);
    }

    [Fact]
    public async Task ANavigationTargetWhoseTextWasNotCaptured_IsDroppedNotClamped()
    {
        await StartAsync();
        await OpenAsync();

        // A target document the layout did not bring text for cannot be turned into a location, so the
        // link is omitted rather than pointing at a guessed position.
        var document = DocumentUri.Create(_workspace.PathOf("a.cvl"));
        _backend.CannedTypeLayout = new BackendTypeLayoutInspection(
            "Point",
            "x64-pc-windows-msvc",
            8,
            4,
            8,
            0,
            null,
            null,
            null,
            null,
            [
                new BackendTypeLayoutMember(
                    "x",
                    "int32",
                    0,
                    4,
                    4,
                    new BackendTypeLayoutMemberNavigation(
                        "int32 Point.x",
                        null,
                        new BackendDefinitionTarget(document, new TextSpan(20, 1), new TextSpan(20, 1)),
                        null,
                        null)),
            ],
            []);

        JObject? layout = await _session.Client.TypeLayoutJsonAsync(Uri, 0, 7).WithTimeout("cvolo/typeLayout");

        Assert.Null(layout!["definition"]);
        Assert.Null(layout["members"]![0]!["navigation"]!["definition"]);
        Assert.Equal("int32 Point.x", layout["members"]![0]!["navigation"]!["signature"]!.Value<string>());
    }

    [Fact]
    public async Task RefreshingBySubject_KeepsTheCompilerNumbersAndAsksForTheSameType()
    {
        await StartAsync();
        await OpenAsync();

        // The client stores the subject the first response handed out and re-asks with it. The server
        // re-resolves it against the current snapshot; the handler does not need a position (§28, §42).
        _backend.CannedSubjectLayout = Record() with { Subject = "Point" };

        JObject? layout = await _session.Client.TypeLayoutBySubjectJsonAsync(Uri, "Point").WithTimeout("cvolo/typeLayout by subject");

        Assert.NotNull(layout);
        Assert.Equal("Point", layout!["subject"]!.Value<string>());
        Assert.Equal(24, layout["size"]!.Value<long>());
        Assert.Equal(8, layout["alignment"]!.Value<long>());
        Assert.Equal("Point", _backend.LastTypeLayoutSubject);
    }

    [Fact]
    public async Task ASubjectThatNoLongerNamesAType_IsUnavailableRatherThanStale()
    {
        await StartAsync();
        await OpenAsync();

        // The type was renamed or removed, so the server resolves nothing and the client shows the
        // unavailable state instead of numbers that no longer describe the program (§30).
        _backend.CannedSubjectLayout = null;

        Assert.Null(await _session.Client.TypeLayoutBySubjectAsync(Uri, "Renamed").WithTimeout("cvolo/typeLayout by subject"));
    }

    [Fact]
    public async Task ASubjectRequestWithoutAPosition_StillRequiresTheDocumentThatAnchorsIt()
    {
        await StartAsync();
        await OpenAsync();

        // The subject is only meaningful inside a project, so the request still names the document
        // whose snapshot resolves it (§42).
        _backend.CannedSubjectLayout = Record() with { Subject = "Point" };

        JObject? layout = await _session.Client.TypeLayoutBySubjectJsonAsync(Uri, "Point").WithTimeout("cvolo/typeLayout by subject");

        Assert.NotNull(layout);
        Assert.Equal("Point", layout!["typeDisplay"]!.Value<string>());
    }

    [Fact]
    public async Task BackendException_IsContained_AndNextRequestSucceeds()
    {
        await StartAsync();
        await OpenAsync();

        _backend.ThrowOnEditorIntelligence = true;
        Assert.Null(await _session.Client.TypeLayoutAsync(Uri, 0, 7).WithTimeout("failing cvolo/typeLayout"));

        _backend.ThrowOnEditorIntelligence = false;
        _backend.CannedTypeLayout = Record();
        Assert.NotNull(await _session.Client.TypeLayoutAsync(Uri, 0, 7).WithTimeout("next cvolo/typeLayout"));
    }
}
