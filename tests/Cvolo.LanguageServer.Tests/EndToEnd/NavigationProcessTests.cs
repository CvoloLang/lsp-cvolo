using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

/// <summary>
/// Real-process hover/definition/documentSymbol coverage over stdio with a
/// known-valid fixture, asserting valid responses and a clean exit (§28.20).
/// </summary>
public class NavigationProcessTests
{
    [Fact]
    public async Task Navigation_OverStdio_ReturnsHoverDefinitionAndOutline_AndExitsCleanly()
    {
        string fixtureSource = Path.Combine(FindRepoRoot(), "tests", "fixtures", "navigation");
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-navigation-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(fixtureSource, workspace);

        try
        {
            using var server = ServerProcess.Start("--stdio");
            var folderUri = new Uri(workspace).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}]}}"));
            await server.WaitForResponseAsync(1).WithTimeout("initialize response");
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            string mainPath = Path.Combine(workspace, "main.cvl");
            string text = File.ReadAllText(mainPath);
            int offset = text.IndexOf("Helper(p.x)", StringComparison.Ordinal);
            (int line, int character) = OffsetToPosition(text, offset);

            var uri = new Uri(mainPath).AbsoluteUri;
            string escaped = Escape(text);
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen", $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{escaped}\"}}}}"));

            server.SendJson(JsonRpcFrames.Request(2, "textDocument/hover", $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}"));
            JObject hover = await server.WaitForResponseAsync(2).WithTimeout("hover response");
            Assert.Null(hover["error"]);
            string hoverText = hover["result"]?["contents"]?.ToString() ?? string.Empty;
            Assert.Contains("Helper", hoverText);
            Assert.Equal(line, (int?)hover["result"]?["range"]?["start"]?["line"]);

            server.SendJson(JsonRpcFrames.Request(3, "textDocument/definition", $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}"));
            JObject definition = await server.WaitForResponseAsync(3).WithTimeout("definition response");
            Assert.Null(definition["error"]);
            var locations = definition["result"] as JArray;
            Assert.NotNull(locations);
            Assert.NotEmpty(locations!);

            server.SendJson(JsonRpcFrames.Request(4, "textDocument/documentSymbol", $"{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}"));
            JObject symbols = await server.WaitForResponseAsync(4).WithTimeout("documentSymbol response");
            Assert.Null(symbols["error"]);
            var symbolArray = symbols["result"] as JArray;
            Assert.NotNull(symbolArray);
            Assert.NotEmpty(symbolArray!);

            server.SendJson(JsonRpcFrames.Request(5, "shutdown", "null"));
            await server.WaitForResponseAsync(5).WithTimeout("shutdown response");
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
    public async Task Definition_RealProcess_DetailedTraceLinksFoundLocationToNavigationStages()
    {
        string sourceDirectory = Path.Combine(FindRepoRoot(), "tests", "fixtures", "navigation");
        string workspace = Path.Combine(Path.GetTempPath(),
            "cvolo-ls-f12-trace-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(sourceDirectory, workspace);
        string tracePath = Path.Combine(workspace, "navigation-trace.jsonl");
        try
        {
            using var server = ServerProcess.StartWithEnvironment(
                new Dictionary<string, string?>
                {
                    ["CVOLO_OBS_MODE"] = "detailed",
                    ["CVOLO_TRACE_PATH"] = tracePath,
                }, "--stdio");
            string folderUri = new Uri(workspace).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Request(1, "initialize",
                $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}]}}"));
            JObject initialized = await server.WaitForResponseAsync(1, TimeSpan.FromSeconds(20));
            Assert.Null(initialized["error"]);
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            server.SendJson(JsonRpcFrames.Request(2,
                "cvolo/observability/status.v1", "null"));
            JObject status = await server.WaitForResponseAsync(2, TimeSpan.FromSeconds(20));
            Assert.Null(status["error"]);
            Assert.Equal("detailed", (string?)status["result"]?["effectiveMode"]);
            Assert.True((bool?)status["result"]?["diskRecordingActive"]);

            string path = Path.Combine(workspace, "main.cvl");
            string source = File.ReadAllText(path);
            string uri = new Uri(path).AbsoluteUri;
            int useOffset = source.IndexOf("Helper(p.x)", StringComparison.Ordinal);
            int declarationOffset = source.IndexOf("Helper(int value)", StringComparison.Ordinal);
            Assert.True(useOffset >= 0 && declarationOffset >= 0);
            (int line, int column) = OffsetToPosition(source, useOffset);
            (int expectedLine, _) = OffsetToPosition(source, declarationOffset);
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{Escape(source)}\"}}}}"));
            server.SendJson(JsonRpcFrames.Request(3, "textDocument/definition",
                $"{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":{line},\"character\":{column}}}}}"));
            JObject answer = await server.WaitForResponseAsync(3, TimeSpan.FromSeconds(20));
            Assert.Null(answer["error"]);
            JArray locations = Assert.IsType<JArray>(answer["result"]);
            Assert.NotEmpty(locations);
            Assert.Equal(uri, (string?)locations[0]?["uri"]);
            Assert.Equal(expectedLine, (int?)locations[0]?["range"]?["start"]?["line"]);

            // An explicit Stop is an observable drain boundary; never rely on
            // process exit timing to imply that the writer flushed every event.
            server.SendJson(JsonRpcFrames.Request(4,
                "cvolo/observability/setRecording.v1",
                "{\"protocolVersion\":1,\"mode\":\"off\",\"captureSources\":false,\"capturePaths\":false,\"forensicScope\":false}"));
            JObject stopped = await server.WaitForResponseAsync(4, TimeSpan.FromSeconds(20));
            Assert.Equal("applied", (string?)stopped["result"]?["status"]);
            Assert.Equal("off", (string?)stopped["result"]?["effectiveMode"]);
            Assert.False((bool?)stopped["result"]?["diskRecordingActive"]);

            server.SendJson(JsonRpcFrames.Request(5, "shutdown", "null"));
            await server.WaitForResponseAsync(5, TimeSpan.FromSeconds(20));
            server.SendJson(JsonRpcFrames.Notification("exit", "null"));
            Assert.Equal(0, await server.WaitForExitAsync(TimeSpan.FromSeconds(20)));

            Assert.True(File.Exists(tracePath));
            string[] lines = await File.ReadAllLinesAsync(tracePath);
            Assert.NotEmpty(lines);
            foreach (string record in lines)
                Assert.Equal(JTokenType.Object, JObject.Parse(record).Type);
            string trace = string.Join("\n", lines);
            Assert.Contains("definition.position_mapped", trace);
            Assert.Contains("symbol.lookup.completed", trace);
            Assert.Contains("definition.mapping.completed", trace);
            Assert.Contains("request.completed", trace);
            Assert.Contains("rpc.response.sent", trace);
            Assert.DoesNotContain("int Helper(int value)", trace); // no source capture
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
    }


    [Fact]
    public async Task Definition_RealProcess_NavigatesIntoClosedSdkSource_WithCorrelatedTrace()
    {
        // An external bundled SDK source is a distinct navigation target from
        // the application file. This proves the LSP maps a captured target text
        // without requiring the target to have been opened in the editor. It is
        // not a .cvlib package-source acceptance oracle (OBS-LSP-002 remains
        // incomplete until a real package fixture is exercised).
        string sourceDirectory = Path.Combine(FindRepoRoot(), "tests", "fixtures", "navigation");
        string workspace = Path.Combine(Path.GetTempPath(),
            "cvolo-ls-f12-sdk-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(sourceDirectory, workspace);
        string tracePath = Path.Combine(workspace, "sdk-navigation-trace.jsonl");
        const string source = "using System.Math.Float;\nfloat CosF(float value) { return System.Math.Float.Cos(value); }\n";
        string mainPath = Path.Combine(workspace, "main.cvl");
        File.WriteAllText(mainPath, source);

        try
        {
            using var server = ServerProcess.StartWithEnvironment(
                new Dictionary<string, string?>
                {
                    ["CVOLO_OBS_MODE"] = "detailed",
                    ["CVOLO_TRACE_PATH"] = tracePath,
                }, "--stdio");

            string workspaceUri = new Uri(workspace).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Request(1, "initialize",
                JsonConvert.SerializeObject(new
                {
                    processId = Environment.ProcessId,
                    workspaceFolders = new[] { new { uri = workspaceUri, name = "w" } },
                })));
            JObject initialized = await server.WaitForResponseAsync(1, TimeSpan.FromSeconds(20));
            Assert.Null(initialized["error"]);
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            string mainUri = new Uri(mainPath).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen",
                JsonConvert.SerializeObject(new
                {
                    textDocument = new { uri = mainUri, languageId = "cvolo", version = 1, text = source },
                })));

            int callOffset = source.IndexOf("Cos(value)", StringComparison.Ordinal);
            Assert.True(callOffset >= 0);
            (int callLine, int callCharacter) = OffsetToPosition(source, callOffset);
            server.SendJson(JsonRpcFrames.Request(3, "textDocument/definition",
                JsonConvert.SerializeObject(new
                {
                    textDocument = new { uri = mainUri },
                    position = new { line = callLine, character = callCharacter },
                })));
            JObject definition = await server.WaitForResponseAsync(3, TimeSpan.FromSeconds(30));
            Assert.Null(definition["error"]);
            JArray locations = Assert.IsType<JArray>(definition["result"]);

            // Independent oracle: the compiler-pinned System.Math.Float SDK
            // declares this exact function in Float.cvl.
            const string declaration = "public float Cos(float x);";
            const string suffix = "libraries/System/Math/Float.cvl";
            JToken location = Assert.Single(locations);
            string targetUriText = Assert.IsType<JValue>(location["uri"]).Value<string>()!;
            var targetUri = new Uri(targetUriText);
            Assert.True(targetUri.IsFile);
            string targetPath = targetUri.LocalPath;
            Assert.EndsWith(suffix, targetPath.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.True(File.Exists(targetPath), "Returned SDK target must exist on disk");
            string targetSource = File.ReadAllText(targetPath);
            int declarationOffset = targetSource.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(declarationOffset >= 0, "Pinned SDK oracle declaration was not found");
            (int expectedLine, int expectedColumn) =
                OffsetToPosition(targetSource, declarationOffset + "public float ".Length);
            Assert.Equal(expectedLine, (int?)location["range"]?["start"]?["line"]);
            Assert.Equal(expectedColumn, (int?)location["range"]?["start"]?["character"]);
            Assert.Equal(expectedColumn + 3, (int?)location["range"]?["end"]?["character"]);

            // Drain the actual persistent writer before analyzing JSONL. No
            // producerSequence or UTC-based guess about writer order is needed.
            server.SendJson(JsonRpcFrames.Request(4, "cvolo/observability/setRecording.v1",
                "{\"protocolVersion\":1,\"mode\":\"off\",\"captureSources\":false,\"capturePaths\":false,\"forensicScope\":false}"));
            JObject stopped = await server.WaitForResponseAsync(4, TimeSpan.FromSeconds(20));
            Assert.Equal("applied", (string?)stopped["result"]?["status"]);
            Assert.False((bool?)stopped["result"]?["diskRecordingActive"]);

            server.SendJson(JsonRpcFrames.Request(5, "shutdown", "null"));
            await server.WaitForResponseAsync(5, TimeSpan.FromSeconds(20));
            server.SendJson(JsonRpcFrames.Notification("exit", "null"));
            Assert.Equal(0, await server.WaitForExitAsync(TimeSpan.FromSeconds(20)));

            Assert.True(File.Exists(tracePath));
            JObject[] events = (await File.ReadAllLinesAsync(tracePath))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(JObject.Parse)
                .ToArray();
            Assert.NotEmpty(events);

            JObject received = Assert.Single(events.Where(e =>
                (string?)e["event"] == "rpc.request.received"
                && e["rpcRequestId"]?["type"]?.ToString() == "number"
                && e["rpcRequestId"]?["value"]?.ToString() == "3"));
            string requestUid = Assert.IsType<string>((string?)received["requestUid"]);
            JObject requestStarted = Assert.Single(events.Where(e =>
                (string?)e["event"] == "request.started"
                && (string?)e["stage"] == "definition"
                && (string?)e["requestUid"] == requestUid));
            string operationId = Assert.IsType<string>((string?)requestStarted["operationId"]);

            foreach (string stageEvent in new[]
            {
                "definition.position_mapped",
                "symbol.lookup.completed",
                "definition.targets.completed",
                "definition.mapping.completed",
                "definition.response.completed",
            })
            {
                Assert.Contains(events, e => (string?)e["event"] == stageEvent
                    && (string?)e["requestUid"] == requestUid);
            }

            JObject terminal = Assert.Single(events.Where(e =>
                (string?)e["event"] == "request.completed"
                && (string?)e["requestUid"] == requestUid
                && (string?)e["stage"] == "definition"));
            Assert.Equal(operationId, (string?)terminal["operationId"]);
            Assert.Equal("success", (string?)terminal["outcome"]);
            Assert.Contains(events, e => (string?)e["event"] == "rpc.response.sent"
                && (string?)e["requestUid"] == requestUid);
            Assert.DoesNotContain(source, string.Join("\n", events.Select(e => e.ToString())), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(workspace))
                Directory.Delete(workspace, recursive: true);
        }
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
