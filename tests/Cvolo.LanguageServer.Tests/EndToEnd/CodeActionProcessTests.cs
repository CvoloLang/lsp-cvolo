using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

public class CodeActionProcessTests
{
    [Fact]
    public async Task CodeAction_OverStdio_RealCompilerFixResolvesWithoutApplying_ThenDiagnosticsClear()
    {
        string fixtureSource = Path.Combine(FindRepoRoot(), "tests", "fixtures", "code-action");
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-codeaction-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(fixtureSource, workspace);

        try
        {
            using var server = ServerProcess.Start("--stdio");
            var folderUri = new Uri(workspace).AbsoluteUri;

            string capabilities =
                "{\"textDocument\":{" +
                "\"codeAction\":{\"codeActionLiteralSupport\":{\"codeActionKind\":{\"valueSet\":[\"quickfix\"]}}," +
                "\"dataSupport\":true,\"resolveSupport\":{\"properties\":[\"edit\"]}}}," +
                "\"workspace\":{\"workspaceEdit\":{\"documentChanges\":true}}}";
            server.SendJson(JsonRpcFrames.Request(1, "initialize",
                $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}],\"capabilities\":{capabilities}}}"));
            JObject initialize = await server.WaitForResponseAsync(1).WithTimeout("initialize response");

            JToken provider = initialize["result"]!["capabilities"]!["codeActionProvider"]!;
            Assert.Equal(true, provider["resolveProvider"]?.Value<bool>());
            Assert.Contains("quickfix", provider["codeActionKinds"]!.Values<string>());
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            string mainPath = Path.Combine(workspace, "main.cvl");
            string text = File.ReadAllText(mainPath);
            string uri = new Uri(mainPath).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{Escape(text)}\"}}}}"));

            (int line, int character) = OffsetToPosition(text, text.IndexOf("1.0", StringComparison.Ordinal));
            server.SendJson(JsonRpcFrames.Request(2, "textDocument/codeAction",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"range\":{{\"start\":{{\"line\":{line},\"character\":{character}}}," +
                $"\"end\":{{\"line\":{line},\"character\":{character + 3}}}}},\"context\":{{\"diagnostics\":[]}}}}"));
            JObject codeAction = await server.WaitForResponseAsync(2).WithTimeout("code action response");

            var actions = codeAction["result"] as JArray;
            Assert.NotNull(actions);
            JToken action = Assert.Single(actions!);
            Assert.Equal("quickfix", (string?)action["kind"]);
            string title = (string?)action["title"] ?? string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.Null(action["edit"]);
            Assert.NotNull(action["data"]);
            Assert.Equal("CVL1900", (string?)action["diagnostics"]?[0]?["code"]);

            server.SendJson(JsonRpcFrames.Request(3, "codeAction/resolve", action.ToString(Formatting.None)));
            JObject resolved = await server.WaitForResponseAsync(3).WithTimeout("code action resolve response");

            JToken edit = resolved["result"]!["edit"]!;
            Assert.Null(edit["changes"]);
            Assert.Equal("f", (string?)edit["documentChanges"]?[0]?["edits"]?[0]?["newText"]);
            Assert.Equal(character + 3, (int)edit["documentChanges"]![0]!["edits"]![0]!["range"]!["start"]!["character"]!);

            Assert.Contains("1.0;", File.ReadAllText(mainPath), StringComparison.Ordinal);
            Assert.DoesNotContain("1.0f;", File.ReadAllText(mainPath), StringComparison.Ordinal);

            string fixedText = text.Replace("1.0;", "1.0f;", StringComparison.Ordinal);
            server.SendJson(JsonRpcFrames.Notification("textDocument/didChange",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"version\":2}},\"contentChanges\":[{{\"text\":\"{Escape(fixedText)}\"}}]}}"));
            await WaitForPublishAsync(server, uri, list => list.Count >= 1 && list[^1].Count == 0).WithTimeout("diagnostics cleared after fix");

            server.SendJson(JsonRpcFrames.Request(4, "shutdown", "null"));
            await server.WaitForResponseAsync(4).WithTimeout("shutdown response");
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

    [Fact]
    public async Task CodeAction_OverStdio_AddUsingFixResolvesAndClearsDiagnostics()
    {
        string fixtureSource = Path.Combine(FindRepoRoot(), "tests", "fixtures", "code-action-using");
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-codeaction-using-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(fixtureSource, workspace);

        try
        {
            using var server = ServerProcess.Start("--stdio");
            var folderUri = new Uri(workspace).AbsoluteUri;

            string capabilities =
                "{\"textDocument\":{" +
                "\"codeAction\":{\"codeActionLiteralSupport\":{\"codeActionKind\":{\"valueSet\":[\"quickfix\"]}}," +
                "\"dataSupport\":true,\"resolveSupport\":{\"properties\":[\"edit\"]}}}," +
                "\"workspace\":{\"workspaceEdit\":{\"documentChanges\":true}}}";
            server.SendJson(JsonRpcFrames.Request(1, "initialize",
                $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}],\"capabilities\":{capabilities}}}"));
            await server.WaitForResponseAsync(1).WithTimeout("initialize response");
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            string mainPath = Path.Combine(workspace, "main.cvl");
            string text = File.ReadAllText(mainPath);
            string uri = new Uri(mainPath).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{Escape(text)}\"}}}}"));

            (int line, int character) = OffsetToPosition(text, text.IndexOf("Foo();", StringComparison.Ordinal));
            server.SendJson(JsonRpcFrames.Request(2, "textDocument/codeAction",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"range\":{{\"start\":{{\"line\":{line},\"character\":{character}}}," +
                $"\"end\":{{\"line\":{line},\"character\":{character + 6}}}}},\"context\":{{\"diagnostics\":[]}}}}"));
            JObject codeAction = await server.WaitForResponseAsync(2).WithTimeout("code action response");

            var actions = codeAction["result"] as JArray;
            Assert.NotNull(actions);
            JToken action = Assert.Single(actions!);
            Assert.Equal("quickfix", (string?)action["kind"]);
            Assert.Contains("using Widgets;", (string?)action["title"] ?? string.Empty, StringComparison.Ordinal);
            Assert.Null(action["edit"]);
            Assert.NotNull(action["data"]);
            Assert.Equal("CVL1078", (string?)action["diagnostics"]?[0]?["code"]);

            server.SendJson(JsonRpcFrames.Request(3, "codeAction/resolve", action.ToString(Formatting.None)));
            JObject resolved = await server.WaitForResponseAsync(3).WithTimeout("code action resolve response");
            string newText = (string?)resolved["result"]!["edit"]!["documentChanges"]?[0]?["edits"]?[0]?["newText"] ?? string.Empty;
            Assert.Contains("using Widgets;", newText, StringComparison.Ordinal);

            string fixedText = "using Widgets;\n" + text;
            server.SendJson(JsonRpcFrames.Notification("textDocument/didChange",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"version\":2}},\"contentChanges\":[{{\"text\":\"{Escape(fixedText)}\"}}]}}"));
            await WaitForPublishAsync(server, uri, list => list.Count >= 1 && list[^1].Count == 0).WithTimeout("diagnostics cleared after add using");

            server.SendJson(JsonRpcFrames.Request(4, "shutdown", "null"));
            await server.WaitForResponseAsync(4).WithTimeout("shutdown response");
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

    [Fact]
    public async Task CodeAction_OverStdio_MissingFieldFixInsertsDefault()
    {
        string fixtureSource = Path.Combine(FindRepoRoot(), "tests", "fixtures", "code-action-missing-field");
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-codeaction-missing-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(fixtureSource, workspace);

        try
        {
            using var server = ServerProcess.Start("--stdio");
            var folderUri = new Uri(workspace).AbsoluteUri;

            string capabilities =
                "{\"textDocument\":{" +
                "\"codeAction\":{\"codeActionLiteralSupport\":{\"codeActionKind\":{\"valueSet\":[\"quickfix\"]}}," +
                "\"dataSupport\":true,\"resolveSupport\":{\"properties\":[\"edit\"]}}}," +
                "\"workspace\":{\"workspaceEdit\":{\"documentChanges\":true}}}";
            server.SendJson(JsonRpcFrames.Request(1, "initialize",
                $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}],\"capabilities\":{capabilities}}}"));
            await server.WaitForResponseAsync(1).WithTimeout("initialize response");
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            string mainPath = Path.Combine(workspace, "main.cvl");
            string text = File.ReadAllText(mainPath);
            string uri = new Uri(mainPath).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{Escape(text)}\"}}}}"));

            // didOpen is a notification, not a completion barrier. A fast
            // codeAction can reach the server before the document was applied.
            // Wait for the real diagnostic publication for this URI before
            // asking the compiler to synthesize the missing-field fix.
            await WaitForPublishAsync(server, uri, list => list.Count >= 1)
                .WithTimeout("initial missing-field diagnostic publication");

            const string initializer = "Window { Width = 1 }";
            int start = text.IndexOf(initializer, StringComparison.Ordinal);
            (int line, int character) = OffsetToPosition(text, start);
            (int endLine, int endCharacter) = OffsetToPosition(text, start + initializer.Length);
            server.SendJson(JsonRpcFrames.Request(2, "textDocument/codeAction",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"range\":{{\"start\":{{\"line\":{line},\"character\":{character}}}," +
                $"\"end\":{{\"line\":{endLine},\"character\":{endCharacter}}}}},\"context\":{{\"diagnostics\":[]}}}}"));
            JObject codeAction = await server.WaitForResponseAsync(2).WithTimeout("code action response");

            var actions = codeAction["result"] as JArray;
            Assert.NotNull(actions);
            JToken action = Assert.Single(actions!);
            Assert.Equal("quickfix", (string?)action["kind"]);
            Assert.Equal("CVL4081", (string?)action["diagnostics"]?[0]?["code"]);
            Assert.NotNull(action["data"]);

            server.SendJson(JsonRpcFrames.Request(3, "codeAction/resolve", action.ToString(Formatting.None)));
            JObject resolved = await server.WaitForResponseAsync(3).WithTimeout("code action resolve response");
            string newText = (string?)resolved["result"]!["edit"]!["documentChanges"]?[0]?["edits"]?[0]?["newText"] ?? string.Empty;
            Assert.Contains("Height = default(int)", newText, StringComparison.Ordinal);

            server.SendJson(JsonRpcFrames.Request(4, "shutdown", "null"));
            await server.WaitForResponseAsync(4).WithTimeout("shutdown response");
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

    private static async Task<IReadOnlyList<PublishedDiagnostics>> WaitForPublishAsync(ServerProcess server, string uri, Func<IReadOnlyList<PublishedDiagnostics>, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var target = new Uri(uri);
        while (DateTime.UtcNow < deadline)
        {
            var published = DiagnosticFrames.ForUri(server.SnapshotFrames(), target);
            if (predicate(published))
            {
                return published;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for publishDiagnostics for {uri}. stderr: {server.GetStderrText()}");
    }

    private static string Escape(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

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

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tooling.version")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root (tooling.version missing).");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }
}
