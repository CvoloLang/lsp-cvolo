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

    private static async Task WithServerAsync(string text, Func<ServerProcess, string, Task> body)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string mainPath = Path.Combine(workspace, "main.cvl");
        File.WriteAllText(mainPath, text);

        try
        {
            using var server = ServerProcess.Start("--stdio");
            var folderUri = new Uri(workspace).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}]}}"));
            await server.WaitForResponseAsync(1).WithTimeout("initialize response");
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            var uri = new Uri(mainPath).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{Escape(text)}\"}}}}"));

            await body(server, uri);

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
