using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// End-to-end protocol coverage for hover, definition and document symbols
/// against the real Cvolo-backed store (§28.2, §28.5–28.9, §28.13–28.15).
/// </summary>
public class NavigationHandlerTests : IDisposable
{
    private const string MainSource =
        "struct Point { public int x; }\n" +
        "int Helper(int value) { return value + value; }\n" +
        "int main() {\n" +
        "    val Point p = Point { x: 1 };\n" +
        "    return Helper(p.x);\n" +
        "}\n";

    private const string LibSource = "int LibHelper() { return 7; }\n";

    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public NavigationHandlerTests()
    {
        _workspace = TestWorkspace.CreateProject(
            ["main.cvl", "lib.cvl"],
            relative => relative == "main.cvl" ? MainSource : LibSource);
        _session = ProtocolSession.Start();
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private async Task InitializeAsync(string[]? hoverFormats = null, bool hierarchical = false)
    {
        await _session.Client
            .InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath, hoverContentFormat: hoverFormats, hierarchicalSymbols: hierarchical)
            .WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
    }

    private async Task OpenMainAsync(string text = MainSource)
    {
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.WaitUntilAsync(() => _session.Server.Store.TryGet(coreUri, out _), "didOpen applied");
    }

    private static (int Line, int Character) Position(string source, string needle, int delta = 0)
    {
        int index = source.IndexOf(needle, StringComparison.Ordinal) + delta;
        Assert.True(index >= 0, $"'{needle}' was not found in the fixture.");

        var line = 0;
        var character = 0;
        for (var i = 0; i < index; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                character = 0;
            }
            else if (source[i] == '\r')
            {
                if (i + 1 < source.Length && source[i + 1] == '\n')
                {
                    i++;
                }

                line++;
                character = 0;
            }
            else
            {
                character++;
            }
        }

        return (line, character);
    }

    [Fact]
    public async Task Hover_OnFunctionCall_ReturnsSignatureWithExactRange()
    {
        await InitializeAsync();
        await OpenMainAsync();

        var (line, character) = Position(MainSource, "Helper(p.x)");
        Hover? hover = await _session.Client.HoverAsync(_workspace.DocumentUri("main.cvl"), line, character).WithTimeout("hover");

        Assert.NotNull(hover);
        MarkupContent contents = (MarkupContent)hover!.Contents.Value!;
        Assert.Equal(MarkupKind.PlainText, contents.Kind);
        Assert.Contains("Helper", contents.Value);
        Assert.Contains("int", contents.Value);
        Assert.Equal(line, hover.Range.Start.Line);
        Assert.Equal(character, hover.Range.Start.Character);
        Assert.Equal(character + "Helper".Length, hover.Range.End.Character);
    }

    [Fact]
    public async Task Hover_OnParameter_ShowsDeclaredType()
    {
        await InitializeAsync();
        await OpenMainAsync();

        var (line, character) = Position(MainSource, "value + value");
        Hover? hover = await _session.Client.HoverAsync(_workspace.DocumentUri("main.cvl"), line, character).WithTimeout("hover");

        Assert.NotNull(hover);
        Assert.Equal("int value", ((MarkupContent)hover!.Contents.Value!).Value);
    }

    [Fact]
    public async Task Hover_OnVarLocal_ShowsInferredType()
    {
        const string source =
            "struct Window { public int Width; }\n" +
            "int main() {\n" +
            "    var window = Window { Width: 1 };\n" +
            "    return window.Width;\n" +
            "}\n";

        await InitializeAsync();
        await OpenMainAsync(source);

        var (line, character) = Position(source, "window.Width");
        Hover? hover = await _session.Client.HoverAsync(_workspace.DocumentUri("main.cvl"), line, character).WithTimeout("hover");

        Assert.NotNull(hover);
        Assert.Equal("Window window", ((MarkupContent)hover!.Contents.Value!).Value);
    }

    [Fact]
    public async Task Hover_MarkdownClient_UsesCvoloFence()
    {
        await InitializeAsync(hoverFormats: ["markdown", "plaintext"]);
        await OpenMainAsync();

        var (line, character) = Position(MainSource, "Helper(p.x)");
        Hover? hover = await _session.Client.HoverAsync(_workspace.DocumentUri("main.cvl"), line, character).WithTimeout("hover");

        Assert.NotNull(hover);
        MarkupContent contents = (MarkupContent)hover!.Contents.Value!;
        Assert.Equal(MarkupKind.Markdown, contents.Kind);
        Assert.StartsWith("```cvolo\n", contents.Value);
        Assert.EndsWith("```", contents.Value);
    }

    [Fact]
    public async Task Hover_OnWhitespace_ReturnsNull()
    {
        await InitializeAsync();
        await OpenMainAsync();

        Hover? hover = await _session.Client.HoverAsync(_workspace.DocumentUri("main.cvl"), 2, 0).WithTimeout("hover");

        Assert.Null(hover);
    }

    [Fact]
    public async Task Definition_OnCall_NavigatesToDeclarationName()
    {
        await InitializeAsync();
        await OpenMainAsync();

        var (line, character) = Position(MainSource, "Helper(p.x)");
        Location[]? locations = await _session.Client.DefinitionAsync(_workspace.DocumentUri("main.cvl"), line, character).WithTimeout("definition");

        Assert.NotNull(locations);
        Location location = Assert.Single(locations!);
        Assert.Equal(new Uri(_workspace.PathOf("main.cvl")), location.Uri);
        Assert.Equal(1, location.Range.Start.Line);
        Assert.Equal(4, location.Range.Start.Character);
        Assert.Equal(4 + "Helper".Length, location.Range.End.Character);
    }

    [Fact]
    public async Task Definition_CrossFile_ToClosedDocument()
    {
        const string mainWithCall = "int main() { return LibHelper(); }\n";
        await InitializeAsync();
        await OpenMainAsync(mainWithCall);

        var (line, character) = Position(mainWithCall, "LibHelper()");
        Location[]? locations = await _session.Client.DefinitionAsync(_workspace.DocumentUri("main.cvl"), line, character).WithTimeout("definition");

        Assert.NotNull(locations);
        Location location = Assert.Single(locations!);
        Assert.Equal(new Uri(_workspace.PathOf("lib.cvl")), location.Uri);
        Assert.Equal("LibHelper", LibSource.Substring(4, "LibHelper".Length));
        Assert.Equal(4, location.Range.Start.Character);
        Assert.Equal(4 + "LibHelper".Length, location.Range.End.Character);
    }

    [Fact]
    public async Task Definition_OverlayTarget_UsesEditedSpan()
    {
        const string mainWithCall = "int main() { return LibHelper(); }\n";
        await InitializeAsync();
        await OpenMainAsync(mainWithCall);

        // Open lib.cvl with a shifted declaration and save it as an unsaved overlay.
        const string shiftedLib = "\n\nint LibHelper() { return 9; }\n";
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("lib.cvl"), "cvolo", 1, shiftedLib).WithTimeout("didOpen lib");
        var libCoreUri = DocumentUri.Create(_workspace.PathOf("lib.cvl"));
        await _session.Client.WaitUntilAsync(() => _session.Server.Store.TryGet(libCoreUri, out _), "lib didOpen applied");

        var (line, character) = Position(mainWithCall, "LibHelper()");
        Location[]? locations = await _session.Client.DefinitionAsync(_workspace.DocumentUri("main.cvl"), line, character).WithTimeout("definition");

        Assert.NotNull(locations);
        Location location = Assert.Single(locations!);
        Assert.Equal(new Uri(_workspace.PathOf("lib.cvl")), location.Uri);
        Assert.Equal(2, location.Range.Start.Line);
        Assert.Equal(4, location.Range.Start.Character);
    }

    [Fact]
    public async Task DocumentSymbols_Hierarchical_IncludesDeclarationsExcludesLocals()
    {
        await InitializeAsync(hierarchical: true);
        await OpenMainAsync();

        JArray? symbols = await _session.Client.DocumentSymbolAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("documentSymbol");

        Assert.NotNull(symbols);
        var names = symbols!.Select(symbol => symbol["name"]!.Value<string>()).ToArray();
        Assert.Contains("Point", names);
        Assert.Contains("Helper", names);
        Assert.Contains("main", names);
        Assert.DoesNotContain("p", names);

        var point = symbols.First(symbol => symbol["name"]!.Value<string>() == "Point");
        var pointChildren = (JArray)point["children"]!;
        Assert.Contains(pointChildren, child => child["name"]!.Value<string>() == "x");

        // Selection range lies inside the declaration range.
        var range = (JObject)point["range"]!;
        var selection = (JObject)point["selectionRange"]!;
        Assert.True(range["start"]!["line"]!.Value<int>() <= selection["start"]!["line"]!.Value<int>());
    }

    [Fact]
    public async Task DocumentSymbols_FlatFallback_ReturnsSymbolInformation()
    {
        await InitializeAsync(hierarchical: false);
        await OpenMainAsync();

        JArray? symbols = await _session.Client.DocumentSymbolAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("documentSymbol");

        Assert.NotNull(symbols);
        var first = (JObject)symbols![0];
        Assert.NotNull(first["location"]);
        Assert.NotNull(first["kind"]);
        Assert.Null(first["selectionRange"]);
    }

    [Fact]
    public async Task DocumentSymbols_IncompleteSource_KeepsValidDeclarations()
    {
        // A recoverable analysis error elsewhere must not erase unrelated valid
        // declaration symbols (§22.2, §28.15).
        const string incomplete = "struct Point { public int x; }\nint main() { return Missing; }\n";
        await InitializeAsync(hierarchical: true);
        await OpenMainAsync(incomplete);

        JArray? symbols = await _session.Client.DocumentSymbolAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("documentSymbol");

        Assert.NotNull(symbols);
        Assert.Contains(symbols!, symbol => symbol["name"]!.Value<string>() == "Point");
        Assert.Contains(symbols!, symbol => symbol["name"]!.Value<string>() == "main");
    }

    [Fact]
    public async Task DocumentSymbols_NamelessDeclaration_IsOmittedNotSent()
    {
        // The client rejects the whole outline if any symbol has a falsy name, so a half-typed
        // declaration without a name is left out instead of breaking every symbol (§19.5).
        const string source = "int () { return 0; }\nstruct Point { public int x; }\nint main() { return 0; }\n";
        await InitializeAsync(hierarchical: true);
        await OpenMainAsync(source);

        JArray? symbols = await _session.Client.DocumentSymbolAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("documentSymbol");

        Assert.NotNull(symbols);
        Assert.Contains(symbols!, symbol => symbol["name"]!.Value<string>() == "Point");
        Assert.DoesNotContain(symbols!, symbol => string.IsNullOrWhiteSpace(symbol["name"]!.Value<string>()));
    }

    [Fact]
    public async Task Hover_WithDocComment_IncludesDocumentation()
    {
        const string source =
            "/// <summary>\n" +
            "/// Doubles the value.\n" +
            "/// </summary>\n" +
            "int Twice(int value) { return value + value; }\n" +
            "int main() { return Twice(1); }\n";
        await InitializeAsync(hoverFormats: ["markdown"]);
        await OpenMainAsync(source);

        var (line, character) = Position(source, "Twice(1)");
        Hover? hover = await _session.Client.HoverAsync(_workspace.DocumentUri("main.cvl"), line, character).WithTimeout("hover");

        Assert.NotNull(hover);
        MarkupContent contents = (MarkupContent)hover!.Contents.Value!;
        Assert.Contains("Doubles the value.", contents.Value);
    }

    [Fact]
    public async Task Definition_UnopenedDocument_ReturnsNull()
    {
        await InitializeAsync();

        Location[]? locations = await _session.Client.DefinitionAsync(_workspace.DocumentUri("main.cvl"), 0, 0).WithTimeout("definition");

        Assert.Null(locations);
    }
}
