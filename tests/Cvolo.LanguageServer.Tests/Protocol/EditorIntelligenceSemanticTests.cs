using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Editor intelligence over the real Cvolo backend. The fake-backend suites pin the transport rules;
/// these tests only assert that the compiler's own facts survive the whole chain unchanged: a zero
/// count stays a zero count, a compact layout summary carries the compiler's numbers, and a
/// declaration name is still the position a client is given for a follow-up command.
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class EditorIntelligenceSemanticTests : IDisposable
{
    private const string Source =
        "public struct Header\n" +
        "{\n" +
        "    public byte Kind;\n" +
        "    public int Length;\n" +
        "}\n" +
        "\n" +
        "public struct Pair\n" +
        "{\n" +
        "    public int A;\n" +
        "    public int B;\n" +
        "}\n" +
        "\n" +
        "public struct Vec\n" +
        "{\n" +
        "    public int X;\n" +
        "    public int Y;\n" +
        "}\n" +
        "\n" +
        "public extension Vec\n" +
        "{\n" +
        "    public int .Sum(int left, int right)\n" +
        "    {\n" +
        "        return left + right;\n" +
        "    }\n" +
        "}\n" +
        "\n" +
        "int main()\n" +
        "{\n" +
        "    // A plain source comment\n" +
        "    // spanning two lines.\n" +
        "    val Vec v = Vec { X: 1, Y: 2 };\n" +
        "    val int a = 2;\n" +
        "    val b = 3;\n" +
        "    val int total = Vec.Sum(a, b);\n" +
        "    val int once = Twice(total);\n" +
        "    val int twice = Twice(once);\n" +
        "    return twice;\n" +
        "}\n" +
        "\n" +
        "int Twice(int value)\n" +
        "{\n" +
        "    return value * 2;\n" +
        "}\n" +
        "\n" +
        "public interface IWidget\n" +
        "{\n" +
        "    int Size();\n" +
        "}\n" +
        "\n" +
        "public struct Button\n" +
        "{\n" +
        "    public int Width;\n" +
        "}\n" +
        "\n" +
        "public extension Button : IWidget\n" +
        "{\n" +
        "    public int Size()\n" +
        "    {\n" +
        "        return Width;\n" +
        "    }\n" +
        "}\n" +
        "\n" +
        "public interface IBaseWidget\n" +
        "{\n" +
        "    void Print();\n" +
        "}\n" +
        "\n" +
        "public interface IChildWidget : IBaseWidget\n" +
        "{\n" +
        "    void Click();\n" +
        "}\n";

    private static readonly string[] Lines = Source.Split('\n');

    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public EditorIntelligenceSemanticTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"], _ => Source);
        _session = ProtocolSession.Start();
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private Uri Uri => _workspace.DocumentUri("main.cvl");

    private static int At(string needle) => Source.IndexOf(needle, StringComparison.Ordinal);

    private static int LineOf(int offset) => Source[..offset].Count(c => c == '\n');

    private static int CharacterOf(int offset) => offset - (Source.LastIndexOf('\n', offset - 1) + 1);

    private static (int Line, int Character) PositionOf(int offset) => (LineOf(offset), CharacterOf(offset));

    private static int OffsetOf(int line, int character)
    {
        var offset = 0;
        for (var i = 0; i < line; i++)
        {
            offset += Lines[i].Length + 1;
        }

        return offset + character;
    }

    private static int StartOf(JToken range) =>
        OffsetOf(range["start"]!["line"]!.Value<int>(), range["start"]!["character"]!.Value<int>());

    private static int EndOf(JToken range) =>
        OffsetOf(range["end"]!["line"]!.Value<int>(), range["end"]!["character"]!.Value<int>());

    private static string TextOf(JToken range)
    {
        var start = StartOf(range);
        return Source[start..(start + (EndOf(range) - start))];
    }

    private static JToken Title(JToken lens) => lens["command"]!["title"]!;

    private static int CountLevels(JToken chain)
    {
        var levels = 0;
        for (var level = chain; level is not null && level.Type == JTokenType.Object; level = level["parent"])
        {
            levels++;
        }

        return levels;
    }

    private async Task StartAsync()
    {
        await _session.Client
            .InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath, hoverContentFormat: null, hierarchicalSymbols: false)
            .WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenAsync()
    {
        await _session.Client.NotifyDidOpenAsync(Uri, "cvolo", 1, Source).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.WaitUntilAsync(
            () => _session.Server.Store.TryGet(coreUri, out _),
            "didOpen applied",
            TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Re-issues a request until the answer the compiler eventually computes shows up. A freshly
    /// opened document is analyzed in the background, so a request that arrives before binding has
    /// finished legitimately answers from an earlier snapshot; a bounded retry keeps the assertions
    /// about the final state instead of about the order in which the server finished opening.
    /// </summary>
    private async Task<T> EventuallyAsync<T>(Func<Task<T?>> request, Func<T, bool> settled, string label)
        where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        T? last = null;
        do
        {
            last = await request().WithTimeout(label);
            if (last is not null && settled(last))
            {
                return last;
            }

            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Fail($"the compiler never reported {label}; the last answer was {last}");
        return null!;
    }

    private static bool HasLensOn(JArray lenses, string name, string title) =>
        lenses.Any(lens => TextOf(lens["range"]!) == name && Title(lens).Value<string>() == title);

    private static JToken LensOn(JArray lenses, string name, string title)
    {
        JToken? found = lenses.FirstOrDefault(lens =>
            TextOf(lens["range"]!) == name && Title(lens).Value<string>() == title);
        string seen = string.Join(
            " | ",
            lenses.Select(lens => $"'{TextOf(lens["range"]!)}' => '{Title(lens).Value<string>()}'"));
        Assert.True(found is not null, $"no '{title}' lens on '{name}'; the document has: {seen}");
        return found!;
    }

    private async Task<JArray> LensesAsync() => await EventuallyAsync(
        () => _session.Client.CodeLensAsync(Uri),
        lenses => HasLensOn(lenses, "Header", "0 references") && HasLensOn(lenses, "Twice", "2 references"),
        "codeLens");

    [Fact]
    public async Task ReferenceLenses_KeepZeroSingularAndPluralCounts()
    {
        await StartAsync();
        await OpenAsync();

        JArray lenses = await LensesAsync();

        // A zero count is a real answer and stays visible; the declaration itself is not a reference,
        // and the lens range is the declared name so a client can ask about exactly that name.
        JToken header = LensOn(lenses, "Header", "0 references");
        var headerName = PositionOf(At("public struct Header") + "public struct ".Length);
        Assert.Equal(headerName.Line, header["range"]!["start"]!["line"]!.Value<int>());
        Assert.Equal(headerName.Character, header["range"]!["start"]!["character"]!.Value<int>());
        Assert.Equal(headerName.Line, header["range"]!["end"]!["line"]!.Value<int>());
        Assert.Equal(headerName.Character + "Header".Length, header["range"]!["end"]!["character"]!.Value<int>());

        // Singular and plural are the compiler's presentation, not a client-side guess.
        Assert.NotNull(LensOn(lenses, "Sum", "1 reference"));
        Assert.NotNull(LensOn(lenses, "Twice", "2 references"));
        Assert.NotNull(LensOn(lenses, "Vec", "4 references"));
    }

    [Fact]
    public async Task ReferenceLens_CarriesTheShowReferencesCommandAtTheDeclaredName()
    {
        await StartAsync();
        await OpenAsync();

        JToken lens = LensOn(await LensesAsync(), "Twice", "2 references");

        Assert.Equal("cvolo.showReferences", lens["command"]!["command"]!.Value<string>());
        JArray arguments = Assert.IsType<JArray>(lens["command"]!["arguments"]!);

        // The editor resolves this document before it can run the reference provider, so it must be
        // the URI the client sent rather than a local path, which would parse as a "d:" scheme.
        Assert.Equal(Uri.AbsoluteUri, arguments[0]!.Value<string>());

        var expected = PositionOf(At("int Twice(int value)") + "int ".Length);
        Assert.Equal(expected.Line, arguments[1]!["line"]!.Value<int>());
        Assert.Equal(expected.Character, arguments[1]!["character"]!.Value<int>());
    }

    [Fact]
    public async Task LayoutLens_ShowsTheCompilerSizeAlignmentAndPadding()
    {
        await StartAsync();
        await OpenAsync();

        JArray lenses = await LensesAsync();

        // `byte Kind` + `int Length` is 5 payload bytes that the target aligns into 8, so the summary
        // must name the padding the compiler inserted rather than a size the client guessed.
        JToken header = LensOn(lenses, "Header", "size 8B, align 4B, padding 3B");
        Assert.Equal("cvolo.showTypeLayout", header["command"]!["command"]!.Value<string>());
        var expected = PositionOf(At("public struct Header") + "public struct ".Length);
        var arguments = Assert.IsType<JArray>(header["command"]!["arguments"]!);
        Assert.Equal(expected.Line, arguments[1]!["line"]!.Value<int>());
        Assert.Equal(expected.Character, arguments[1]!["character"]!.Value<int>());

        // Zero padding is reported explicitly rather than hidden.
        Assert.NotNull(LensOn(lenses, "Pair", "size 8B, align 4B, padding 0B"));
    }

    private async Task EnableFieldDecorationsAsync()
    {
        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject
            {
                ["cvolo.codeLens.fields"] = true,
                ["cvolo.codeLens.fieldReferences"] = true,
                ["cvolo.codeLens.fieldLayout"] = true,
                ["cvolo.inlayHints.layout"] = true,
            })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();
    }

    [Fact]
    public async Task FieldLenses_AreAbsentUntilTheyAreAskedFor()
    {
        await StartAsync();
        await OpenAsync();

        JArray lenses = await LensesAsync();

        // One line per field is a deliberate choice, so nothing about a field changes until the
        // reader asks for it. The declaration-level lenses are unaffected either way.
        Assert.DoesNotContain(lenses, lens => TextOf(lens["range"]!) is "Kind" or "Length");
        Assert.NotNull(LensOn(lenses, "Header", "0 references"));
    }

    [Fact]
    public async Task FieldReferenceAndFieldLayoutLensesShareTheFieldLineInOrder()
    {
        await StartAsync();
        await OpenAsync();
        await EnableFieldDecorationsAsync();

        JArray lenses = await EventuallyAsync(
            () => _session.Client.CodeLensAsync(Uri),
            answer => HasLensOn(answer, "Length", "offset 4B, size 4B, align 4B, pad 3B before"),
            "field codeLens");

        // `Length` is never referenced, so the count stays a visible zero rather than being hidden,
        // and the compiler's storage facts sit beside it on the same line.
        JToken length = LensOn(lenses, "Length", "offset 4B, size 4B, align 4B, pad 3B before");
        Assert.NotNull(LensOn(lenses, "Length", "0 references"));
        Assert.Equal("cvolo.showTypeLayout", length["command"]!["command"]!.Value<string>());

        var expected = PositionOf(At("public int Length;") + "public int ".Length);
        JArray arguments = Assert.IsType<JArray>(length["command"]!["arguments"]!);
        Assert.Equal(expected.Line, arguments[1]!["line"]!.Value<int>());
        Assert.Equal(expected.Character, arguments[1]!["character"]!.Value<int>());

        // The first field starts the type, so it has no padding before it and says nothing about one.
        Assert.NotNull(LensOn(lenses, "Kind", "offset 0B, size 1B, align 1B"));

        // The order is deterministic: references, then layout, on the same anchored range.
        var onLength = lenses
            .Where(lens => TextOf(lens["range"]!) == "Length")
            .Select(lens => Title(lens).Value<string>())
            .ToArray();
        Assert.Equal(["0 references", "offset 4B, size 4B, align 4B, pad 3B before"], onLength);
    }

    [Fact]
    public async Task AFieldLayoutLensClick_ResolvesTheLayoutOfTheTypeThatStoresTheField()
    {
        await StartAsync();
        await OpenAsync();

        // The field is not a type, so the position the lens carries is resolved to the type that
        // stores it. That is what the view opened by a field lens click shows.
        var position = PositionOf(At("public int Length;") + "public int ".Length);
        TypeLayoutResponse? layout = await EventuallyAsync(
            () => _session.Client.TypeLayoutAsync(Uri, position.Line, position.Character),
            answer => answer is not null && answer.TypeDisplay == "Header",
            "cvolo/typeLayout at a field");

        Assert.NotNull(layout);
        Assert.Equal("Header", layout!.TypeDisplay);
        Assert.Equal(8, layout.Size);
        Assert.Equal(new[] { "Kind", "Length" }, layout.Members.Select(member => member.Name));
    }

    [Fact]
    public async Task FieldLayoutInlayHint_ShowsTheOffsetSizeAlignmentAndPrecedingPadding()
    {
        await StartAsync();
        await OpenAsync();
        await EnableFieldDecorationsAsync();

        JArray hints = await EventuallyAsync(
            () => _session.Client.InlayHintsAsync(Uri, new LspRange
            {
                Start = new Position { Line = 0, Character = 0 },
                End = new Position { Line = 5, Character = 0 },
            }),
            answer => answer.Any(hint => hint["kind"]!.Value<string>() == "layout" && hint["label"]!.Value<string>()!.Contains("pad", StringComparison.Ordinal)),
            "field inlayHint");

        // The hint is presentation only: it sits just after the declaration, changes nothing in the
        // source, and repeats the padding that sits *before* the field, never the tail padding.
        JToken length = hints.First(hint =>
            hint["kind"]!.Value<string>() == "layout"
            && hint["label"]!.Value<string>()!.StartsWith("offset 4,", StringComparison.Ordinal));
        Assert.Equal("offset 4, size 4, align 4, pad 3 before", length["label"]!.Value<string>());
        Assert.True(length["paddingLeft"]!.Value<bool>());

        var expected = PositionOf(At("public int Length;") + "public int Length;".Length);
        Assert.Equal(expected.Line, length["position"]!["line"]!.Value<int>());
        Assert.Equal(expected.Character, length["position"]!["character"]!.Value<int>());

        Assert.Contains(
            hints,
            hint => hint["kind"]!.Value<string>() == "layout" && hint["label"]!.Value<string>() == "offset 0, size 1, align 1");
    }

    [Fact]
    public async Task TheOffsetFormatSetting_RewritesTheCompilerFieldNumbersWithoutChangingThem()
    {
        await StartAsync();
        await OpenAsync();
        await EnableFieldDecorationsAsync();

        await _session.Client
            .NotifyDidChangeConfigurationAsync(new JObject { ["cvolo.layout.offsetFormat"] = "hex" })
            .WithTimeout("didChangeConfiguration");
        await _session.Client.DrainNotificationsAsync();

        JArray lenses = await EventuallyAsync(
            () => _session.Client.CodeLensAsync(Uri),
            answer => HasLensOn(answer, "Length", "offset 0x04, size 0x04, align 0x04, pad 0x03 before"),
            "hex field codeLens");

        // Same compiler numbers, another notation. The type summary is untouched because a size, an
        // alignment and a padding total are byte counts rather than offsets.
        Assert.NotNull(LensOn(lenses, "Header", "size 8B, align 4B, padding 3B"));
    }

    [Fact]
    public async Task TypeLayout_ReportsTheMembersOffsetsAndInternalPaddingOfTheTypeAtTheCursor()
    {
        await StartAsync();
        await OpenAsync();

        var position = PositionOf(At("public struct Header") + "public struct ".Length);
        TypeLayoutResponse? layout = await EventuallyAsync(
            () => _session.Client.TypeLayoutAsync(Uri, position.Line, position.Character),
            answer => answer is not null && answer.TypeDisplay == "Header",
            "cvolo/typeLayout");

        Assert.NotNull(layout);
        Assert.Equal("Header", layout!.TypeDisplay);
        Assert.False(string.IsNullOrEmpty(layout.TargetDisplay));
        Assert.Equal(8, layout.Size);
        Assert.Equal(4, layout.Alignment);
        Assert.Equal(5, layout.PayloadSize);
        Assert.Equal(3, layout.PaddingSize);
        Assert.Equal(new[] { "Kind", "Length" }, layout.Members.Select(member => member.Name));
        Assert.Equal(new long[] { 0, 4 }, layout.Members.Select(member => member.Offset));
        Assert.Equal(new long[] { 1, 4 }, layout.Members.Select(member => member.Size));
        var padding = Assert.Single(layout.Padding);
        Assert.Equal(1, padding.Offset);
        Assert.Equal(3, padding.Size);
        Assert.Equal("internal", padding.Kind);
    }

    [Fact]
    public async Task TypeLayout_CarriesTheCompilerResolvedNavigationForTheTypeAndItsFields()
    {
        await StartAsync();
        await OpenAsync();

        var position = PositionOf(At("public struct Header") + "public struct ".Length);
        TypeLayoutResponse? layout = await EventuallyAsync(
            () => _session.Client.TypeLayoutAsync(Uri, position.Line, position.Character),
            answer => answer is not null
                && answer.Definition is not null
                && answer.Members.All(member => member.Navigation is not null),
            "cvolo/typeLayout navigation");

        // The type name navigates to its own declaration; the compiler resolved the target, so the
        // client follows it without resolving any name itself.
        Assert.NotNull(layout!.Definition);
        var declared = PositionOf(At("public struct Header") + "public struct ".Length);
        Assert.Equal(declared.Line, layout.Definition!.Range.Start.Line);
        Assert.Equal(declared.Character, layout.Definition.Range.Start.Character);
        Assert.Equal(declared.Character + "Header".Length, layout.Definition.Range.End.Character);

        // Each field row navigates to the field's own declaration and carries its rendered signature.
        var kind = layout.Members[0];
        Assert.EndsWith(".Kind", kind.Navigation!.Signature);
        var kindDeclared = PositionOf(At("public byte Kind;") + "public byte ".Length);
        Assert.Equal(kindDeclared.Line, kind.Navigation.Definition!.Range.Start.Line);
        Assert.Equal(kindDeclared.Character, kind.Navigation.Definition.Range.Start.Character);

        // A primitive field type is not a source declaration, so there is no type target, and a byte
        // is not something the compiler lays out as a type of its own, so there is no nested layout.
        Assert.Null(kind.Navigation.TypeDefinition);
        Assert.Null(kind.Navigation.NestedLayout);
    }

    [Fact]
    public async Task TypeLayout_AtAPositionThatBindsToNoType_IsNull()
    {
        await StartAsync();
        await OpenAsync();

        // A position that binds to no type contributes nothing rather than a host-layout guess.
        var position = PositionOf(At("return value * 2;") + "return value * ".Length);
        TypeLayoutResponse? layout = await _session.Client
            .TypeLayoutAsync(Uri, position.Line, position.Character)
            .WithTimeout("cvolo/typeLayout");

        Assert.Null(layout);
    }

    [Fact]
    public async Task TypeLayout_ResolvesAKnownValueAndAnInferredLocalToTheirCompilerType()
    {
        await StartAsync();
        await OpenAsync();

        // A value the compiler knows by declaration, and one it only knows by inference, both answer
        // with the type the compiler resolved. The editor never guesses from the declaration text.
        var v = PositionOf(At("val Vec v = Vec") + "val Vec ".Length);
        TypeLayoutResponse? vec = await EventuallyAsync(
            () => _session.Client.TypeLayoutAsync(Uri, v.Line, v.Character),
            answer => answer is not null && answer.TypeDisplay == "Vec",
            "cvolo/typeLayout at a known value");
        Assert.NotNull(vec);
        Assert.Equal(new[] { "X", "Y" }, vec!.Members.Select(member => member.Name));

        var b = PositionOf(At("val b = 3;") + "val ".Length);
        TypeLayoutResponse? inferred = await EventuallyAsync(
            () => _session.Client.TypeLayoutAsync(Uri, b.Line, b.Character),
            answer => answer is not null && answer.TypeDisplay == "int",
            "cvolo/typeLayout at an inferred local");
        Assert.NotNull(inferred);
        Assert.Equal(4, inferred!.Size);
    }

    [Fact]
    public async Task AnOpenLayoutRefreshesBySubjectAfterTheTypeChanges()
    {
        await StartAsync();
        await OpenAsync();

        var position = PositionOf(At("public struct Header") + "public struct ".Length);
        TypeLayoutResponse? opened = await EventuallyAsync(
            () => _session.Client.TypeLayoutAsync(Uri, position.Line, position.Character),
            answer => answer is not null && answer.TypeDisplay == "Header",
            "cvolo/typeLayout");
        Assert.NotNull(opened);
        Assert.Equal("Header", opened!.Subject);
        Assert.Equal(8, opened.Size);

        // The reader edits the type. An open view keeps only the subject, so it re-asks and is given
        // the compiler's new numbers rather than the ones it was first shown (§28, §29, §55).
        var edited = Source.Replace(
            "    public int Length;\n",
            "    public int Length;\n    public int Extra;\n",
            StringComparison.Ordinal);
        await _session.Client
            .NotifyDidChangeAsync(Uri, 2, new TextDocumentContentChangeEvent { Text = edited })
            .WithTimeout("didChange");

        TypeLayoutResponse? refreshed = await EventuallyAsync(
            () => _session.Client.TypeLayoutBySubjectAsync(Uri, "Header"),
            answer => answer is not null && answer.Size == 12,
            "refreshed cvolo/typeLayout by subject");
        Assert.NotNull(refreshed);
        Assert.Equal("Header", refreshed!.Subject);
        Assert.Equal(new[] { "Kind", "Length", "Extra" }, refreshed.Members.Select(member => member.Name));
    }

    [Fact]
    public async Task InlayHints_ReportInferredTypesAndTheParameterAnArgumentBindsTo()
    {
        await StartAsync();
        await OpenAsync();

        var range = new LspRange
        {
            Start = new Position { Line = 0, Character = 0 },
            End = new Position { Line = Lines.Length - 1, Character = Lines[^1].Length },
        };
        JArray hints = await EventuallyAsync(
            () => _session.Client.InlayHintsAsync(Uri, range),
            answer => answer is not null && answer.Any(hint => hint["label"]!.Value<string>() == ": int"),
            "inlayHint");

        JToken typeHint = hints.First(hint => hint["label"]!.Value<string>() == ": int");
        Assert.Equal("type", typeHint["kind"]!.Value<string>());
        var expectedType = PositionOf(At("val b = 3;") + "val b".Length);
        Assert.Equal(expectedType.Line, typeHint["position"]!["line"]!.Value<int>());
        Assert.Equal(expectedType.Character, typeHint["position"]!["character"]!.Value<int>());

        // The label already carries its own trailing space, so the client must not add padding.
        JToken[] parameters = [.. hints.Where(hint => hint["kind"]?.Value<string>() == "parameter")];
        Assert.Equal(new[] { "left: ", "right: ", "value: ", "value: " }, parameters.Select(hint => hint["label"]!.Value<string>()));
        var expectedLeft = PositionOf(At("Sum(a, b)") + "Sum(".Length);
        var expectedRight = PositionOf(At("Sum(a, b)") + "Sum(a, ".Length);
        Assert.Equal(expectedLeft.Line, parameters[0]["position"]!["line"]!.Value<int>());
        Assert.Equal(expectedLeft.Character, parameters[0]["position"]!["character"]!.Value<int>());
        Assert.Equal(expectedRight.Line, parameters[1]["position"]!["line"]!.Value<int>());
        Assert.Equal(expectedRight.Character, parameters[1]["position"]!["character"]!.Value<int>());
        Assert.All(parameters, parameter =>
        {
            Assert.False(parameter["paddingLeft"]!.Value<bool>());
            Assert.False(parameter["paddingRight"]!.Value<bool>());
        });
    }

    [Fact]
    public async Task DocumentHighlights_AreTheOccurrencesOfOneBinding()
    {
        await StartAsync();
        await OpenAsync();

        var declaration = PositionOf(At("public struct Header") + "public struct ".Length);
        JArray declarationHighlights = await EventuallyAsync(
            () => _session.Client.DocumentHighlightsAsync(Uri, declaration.Line, declaration.Character),
            answer => answer is not null && answer.Count > 0,
            "documentHighlight");

        // Nothing uses `Header`, so the declaration is still highlighted, and a declaration has no
        // read/write shape; the protocol expresses that as a plain text highlight (no `kind`).
        JToken occurrence = Assert.Single(declarationHighlights);
        Assert.Equal("Header", TextOf(occurrence["range"]!));
        Assert.Null(occurrence["kind"]);

        var local = PositionOf(At("val int total") + "val int ".Length);
        JArray highlights = await EventuallyAsync(
            () => _session.Client.DocumentHighlightsAsync(Uri, local.Line, local.Character),
            answer => answer is not null && answer.Count == 2,
            "documentHighlight");

        Assert.All(highlights, occurrence => Assert.Equal("total", TextOf(occurrence["range"]!)));
        Assert.Null(highlights[0]!["kind"]);
        Assert.Equal(2, highlights[1]!["kind"]!.Value<int>());
    }

    [Fact]
    public async Task FoldingRanges_CoverDeclarationBodiesAndCommentBlocks()
    {
        await StartAsync();
        await OpenAsync();

        var commentLine = LineOf(At("// A plain source comment"));
        JArray folds = await EventuallyAsync(
            () => _session.Client.FoldingRangesAsync(Uri),
            answer => answer is not null
                && answer.Any(fold => fold["startLine"]!.Value<int>() == commentLine
                    && fold["kind"]?.Value<string>() == "comment"),
            "foldingRange");

        JToken comment = Assert.Single(
            folds,
            fold => fold["startLine"]!.Value<int>() == commentLine && fold["kind"]?.Value<string>() == "comment");
        Assert.Equal(commentLine + 1, comment["endLine"]!.Value<int>());

        // A region spans more than the line it starts on, and a one-line field declaration folds nothing.
        Assert.All(folds, fold => Assert.True(fold["endLine"]!.Value<int>() > fold["startLine"]!.Value<int>()));

        // A declaration's region is the body between its braces, not the signature above it.
        var headerLine = LineOf(At("public struct Header"));
        Assert.Contains(folds, fold => fold["startLine"]!.Value<int>() == headerLine + 1
            && fold["endLine"]!.Value<int>() == headerLine + 3);
        Assert.DoesNotContain(folds, fold => fold["startLine"]!.Value<int>() == headerLine + 2);

        var mainLine = LineOf(At("int main()"));
        Assert.Contains(folds, fold => fold["startLine"]!.Value<int>() == mainLine + 1
            && fold["endLine"]!.Value<int>() == mainLine + 10);
    }

    [Fact]
    public async Task SelectionRanges_GrowFromTheIdentifierOutToTheDeclaration()
    {
        await StartAsync();
        await OpenAsync();

        var caret = PositionOf(At("Twice(total)") + 1);
        JArray chains = await EventuallyAsync(
            () => _session.Client.SelectionRangesAsync(Uri, (caret.Line, caret.Character)),
            answer => answer is not null && answer.Count == 1 && CountLevels(answer[0]!) >= 3,
            "selectionRange");

        Assert.Equal(1, chains.Count);
        JArray levels = [];
        for (JToken? level = chains[0]!; level is not null && level.Type == JTokenType.Object; level = level["parent"])
        {
            levels.Add(level);
        }

        Assert.Equal("Twice", TextOf(levels[0]!["range"]!));
        Assert.Contains("Twice(total)", levels.Select(level => TextOf(level["range"]!)));
        var outermost = TextOf(levels[levels.Count - 1]!["range"]!);
        Assert.Contains("int main()", outermost, StringComparison.Ordinal);
        Assert.Contains("return twice;", outermost, StringComparison.Ordinal);

        for (var i = 1; i < levels.Count; i++)
        {
            Assert.True(StartOf(levels[i]!["range"]!) <= StartOf(levels[i - 1]!["range"]!), "a parent must start no later than its child");
            Assert.True(EndOf(levels[i]!["range"]!) >= EndOf(levels[i - 1]!["range"]!), "a parent must end no earlier than its child");
        }
    }

    [Fact]
    public async Task TypeDefinition_ResolvesTheDeclaredTypeWhileDefinitionKeepsTheDeclaration()
    {
        await StartAsync();
        await OpenAsync();

        // `val Vec v = Vec { X: 1, Y: 2 };` — the local is a name, not a type. The ordinary
        // definition stays on the local declaration; the type definition leaves it for the struct.
        var local = PositionOf(At("val Vec v = Vec") + "val Vec ".Length);
        var structLine = PositionOf(At("public struct Vec")).Line;

        Location[]? typeTargets = await EventuallyAsync(
            () => _session.Client.TypeDefinitionAsync(Uri, local.Line, local.Character),
            locations => locations is not null && locations.Length == 1 && locations[0].Range.Start.Line == structLine,
            "textDocument/typeDefinition");

        var typeTarget = Assert.Single(typeTargets!);
        Assert.Equal(structLine, typeTarget.Range.Start.Line);

        Location[]? definitions = await EventuallyAsync(
            () => _session.Client.DefinitionAsync(Uri, local.Line, local.Character),
            locations => locations is not null && locations.Length >= 1 && locations[0].Range.Start.Line == local.Line,
            "textDocument/definition");

        Assert.Equal(local.Line, definitions![0].Range.Start.Line);
    }

    [Fact]
    public async Task Implementation_ResolvesTheExtensionThatConforms()
    {
        await StartAsync();
        await OpenAsync();

        // On the interface name, Go to Implementations leaves the declaration for the extension
        // block that the compiler records as conforming, and lands on the extended type token.
        var iface = PositionOf(At("public interface IWidget") + "public interface ".Length);
        var extension = PositionOf(At("public extension Button") + "public extension ".Length);

        Location[]? targets = await EventuallyAsync(
            () => _session.Client.ImplementationAsync(Uri, iface.Line, iface.Character),
            locations => locations is not null && locations.Length >= 1 && locations[0].Range.Start.Line == extension.Line,
            "textDocument/implementation");

        var target = Assert.Single(targets!);
        Assert.Equal(extension.Line, target.Range.Start.Line);
        Assert.Equal(extension.Character, target.Range.Start.Character);
    }

    [Fact]
    public async Task TypeHierarchy_NamesTheContractAndResolvesItsDeclaredBase()
    {
        await StartAsync();
        await OpenAsync();

        // Prepare names the contract on the child interface, and the follow-up request re-resolves it
        // from the server-owned key, so the declared base interface comes back as a supertype.
        var child = PositionOf(At("public interface IChildWidget") + "public interface ".Length);
        var baseLine = PositionOf(At("public interface IBaseWidget") + "public interface ".Length).Line;

        JArray? prepared = await EventuallyAsync(
            () => _session.Client.PrepareTypeHierarchyAsync(Uri, child.Line, child.Character),
            items => items is not null && items.Count >= 1 && items[0]!["name"]!.Value<string>() == "IChildWidget",
            "textDocument/prepareTypeHierarchy");

        var item = Assert.Single(prepared!.Children());
        Assert.Equal("IChildWidget", item["data"]!.Value<string>());

        var payload = new TypeHierarchyItemPayload
        {
            Name = "IChildWidget",
            Kind = SymbolKind.Interface,
            Uri = Uri.AbsoluteUri,
            Data = "IChildWidget",
        };

        JArray? supertypes = await EventuallyAsync(
            () => _session.Client.TypeHierarchySupertypesAsync(payload),
            items => items is not null && items.Count >= 1 && items[0]!["name"]!.Value<string>() == "IBaseWidget",
            "typeHierarchy/supertypes");

        var parent = Assert.Single(supertypes!.Children());
        Assert.Equal("IBaseWidget", parent["name"]!.Value<string>());
        Assert.Equal(baseLine, parent["range"]!["start"]!["line"]!.Value<int>());
    }
}
