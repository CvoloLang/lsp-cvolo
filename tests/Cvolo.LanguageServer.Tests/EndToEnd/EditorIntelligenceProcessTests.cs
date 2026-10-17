using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

public class EditorIntelligenceProcessTests
{
    [Fact]
    public async Task Completion_OverStdio_OffersTypeNamesAndKeepsNullOutOfSafePointerSlots()
    {
        const string text =
            "struct Window { void* Ptr; int Width; }\n" +
            "int main()\n" +
            "{\n" +
            "    Window w = Window { Ptr =  };\n" +
            "    dou\n" +
            "}\n";

        await WithServerAsync(text, async (server, uri) =>
        {
            int typeOffset = text.IndexOf("    dou", StringComparison.Ordinal) + "    dou".Length;
            var typeItems = await CompletionAtAsync(server, uri, text, typeOffset, 2);
            Assert.Contains(typeItems, item => (string?)item["label"] == "double");

            int valueOffset = text.IndexOf("Ptr = ", StringComparison.Ordinal) + "Ptr = ".Length;
            var valueItems = await CompletionAtAsync(server, uri, text, valueOffset, 4);
            var valueLabels = valueItems.Select(item => (string?)item["label"]).ToList();
            Assert.True(valueLabels.Contains("default"), "value labels: " + string.Join(",", valueLabels));
            Assert.DoesNotContain("null", valueLabels);
        });
    }

    [Fact]
    public async Task Completion_OverStdio_OffersDoubleForTheDoPrefixAtStatementStart()
    {
        const string text =
            "int main()\n" +
            "{\n" +
            "    do\n" +
            "}\n";

        await WithServerAsync(text, async (server, uri) =>
        {
            int offset = text.IndexOf("    do", StringComparison.Ordinal) + "    do".Length;
            var items = await CompletionAtAsync(server, uri, text, offset, 2);
            Assert.Contains(items, item => (string?)item["label"] == "double");
        });
    }

    [Fact]
    public async Task SemanticTokens_OverStdio_EmitsTheExternModifierForARealisticClient()
    {
        const string text =
            "extern int getchar();\n" +
            "int main()\n" +
            "{\n" +
            "    return getchar();\n" +
            "}\n";

        await WithServerAsync(text, RealisticClientCapabilities, async (server, uri, initialize) =>
        {
            var legend = initialize["result"]?["capabilities"]?["semanticTokensProvider"]?["legend"]?["tokenModifiers"] as JArray;
            Assert.NotNull(legend);
            var modifiers = legend!.Select(token => (string)token!).ToList();
            int externBit = modifiers.IndexOf("cvoloExternFunction");
            Assert.True(externBit >= 0, "advertised modifiers: " + string.Join(",", modifiers));

            server.SendJson(JsonRpcFrames.Request(3, "textDocument/semanticTokens/full",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}"));
            JObject response = await server.WaitForResponseAsync(3).WithTimeout("semantic tokens response");
            var data = response["result"]?["data"] as JArray;
            Assert.NotNull(data);

            var tokens = DecodeTokens(data!);
            foreach ((int line, int start) in new[] { (0, 11), (3, 11) })
            {
                var token = Assert.Single(tokens, t => t.Line == line && t.Start == start && t.Length == 7);
                Assert.True((token.Modifiers & (1 << externBit)) != 0,
                    $"token at {line}:{start} did not carry cvoloExternFunction (modifiers={token.Modifiers})");
            }
        });
    }

    [Fact]
    public async Task InlayHint_OverStdio_DoesNotInventAnIntTypeForAnIncompleteExplicitDeclaration()
    {
        const string text =
            "struct Window { int Width; }\n" +
            "int main()\n" +
            "{\n" +
            "    Window windo = Window\n" +
            "    {\n" +
            "        Width = default\n" +
            "    };\n" +
            "    Window windo\n" +
            "    var probe = Window { Width = 1 };\n" +
            "    return 0;\n" +
            "}\n";

        await WithServerAsync(text, async (server, uri) =>
        {
            var lastLine = text.Split('\n').Length - 1;
            server.SendJson(JsonRpcFrames.Request(3, "textDocument/inlayHint",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"range\":{{\"start\":{{\"line\":0,\"character\":0}},\"end\":{{\"line\":{lastLine},\"character\":0}}}}}}"));
            JObject response = await server.WaitForResponseAsync(3).WithTimeout("inlayHint response");

            var hints = response["result"] as JArray ?? [];
            var labels = hints.Select(hint => (string?)hint["label"]).ToList();
            Assert.Contains(": Window", labels);
            Assert.DoesNotContain(": int", labels);
        });
    }

    // The token capabilities a current VS Code release sends: the standard token types plus only the
    // ten standard token modifiers. The custom presentation modifiers are deliberately absent, which is
    // exactly the negotiation the server must still honour.
    private const string RealisticClientCapabilities =
        """
        {"textDocument":{"semanticTokens":{"dynamicRegistration":false,"requests":{"range":true,"full":{"delta":true}},"tokenTypes":["namespace","type","class","enum","interface","struct","typeParameter","parameter","variable","property","enumMember","event","function","method","macro","keyword","modifier","comment","string","number","regexp","operator","decorator"],"tokenModifiers":["declaration","definition","readonly","static","deprecated","abstract","async","modification","documentation","defaultLibrary"],"formats":["relative"],"overlappingTokenSupport":false,"multilineTokenSupport":false,"serverCancelSupport":true,"augmentsSyntaxTokens":true}}}
        """;

    private static async Task WithServerAsync(string text, Func<ServerProcess, string, Task> body) =>
        await WithServerAsync(text, capabilities: null, (server, uri, _) => body(server, uri));

    private static async Task WithServerAsync(
        string text,
        string? capabilities,
        Func<ServerProcess, string, JObject, Task> body)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string mainPath = Path.Combine(workspace, "main.cvl");
        File.WriteAllText(mainPath, text);

        try
        {
            using var server = ServerProcess.Start("--stdio");
            var folderUri = new Uri(workspace).AbsoluteUri;
            string capabilityJson = capabilities is null ? "" : $",\"capabilities\":{capabilities}";
            server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{Environment.ProcessId}{capabilityJson},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}]}}"));
            JObject initialize = await server.WaitForResponseAsync(1).WithTimeout("initialize response");
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            var uri = new Uri(mainPath).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{Escape(text)}\"}}}}"));

            await body(server, uri, initialize);

            server.SendJson(JsonRpcFrames.Request(99, "shutdown", "null"));
            await server.WaitForResponseAsync(99).WithTimeout("shutdown response");
            server.SendJson(JsonRpcFrames.Notification("exit", "null"));
            int exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
            Assert.Equal(0, exitCode);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static List<(int Line, int Start, int Length, int Modifiers)> DecodeTokens(JArray data)
    {
        var tokens = new List<(int Line, int Start, int Length, int Modifiers)>();
        int line = 0;
        int start = 0;
        for (var i = 0; i + 4 < data.Count; i += 5)
        {
            int deltaLine = data[i]!.Value<int>();
            int deltaStart = data[i + 1]!.Value<int>();
            int length = data[i + 2]!.Value<int>();
            int modifiers = data[i + 4]!.Value<int>();
            line += deltaLine;
            start = deltaLine == 0 ? start + deltaStart : deltaStart;
            tokens.Add((line, start, length, modifiers));
        }

        return tokens;
    }

    private static async Task<List<JToken>> CompletionAtAsync(ServerProcess server, string uri, string text, int offset, int id)
    {
        (int line, int character) = OffsetToPosition(text, offset);
        server.SendJson(JsonRpcFrames.Request(id, "textDocument/completion",
            $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}"));
        JObject response = await server.WaitForResponseAsync(id).WithTimeout("completion response");
        var items = response["result"]?["items"] as JArray;
        Assert.NotNull(items);
        return items!.Cast<JToken>().ToList();
    }

    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static (int Line, int Character) OffsetToPosition(string text, int offset)
    {
        var line = 0;
        var character = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                character = 0;
            }
            else if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
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
}
