using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

public class ProcessLifecycleTests
{
    [Fact]
    public async Task ExitBeforeInitialize_ExitsNonZero_WithNoStdoutFrames()
    {
        using var server = ServerProcess.Start("--stdio");
        server.SendJson(JsonRpcFrames.Notification("exit", "null"));

        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(1, exitCode);
        Assert.Empty(server.SnapshotFrames());
    }

    [Fact]
    public async Task DeadClientProcessId_ReturnsInvalidParams_AndExitsNonZero()
    {
        var deadPid = UnusedProcessIdFinder.Find();
        using var server = ServerProcess.Start("--stdio");
        server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{deadPid},\"rootPath\":null}}"));

        // Windows CI can require more than the generic five-second budget
        // to start a freshly built self-contained server under parallel test
        // load. Keep the assertion that a dead client PID is rejected, while
        // allowing bounded startup time before receiving the error response.
        JObject response = await server.WaitForResponseAsync(1)
            .WithTimeout("initialize response", TimeSpan.FromSeconds(20));
        Assert.Equal(-32602, response["error"]!["code"]!.Value<int>());

        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task AllStdoutBytes_AreValidJsonRpcFrames()
    {
        using var server = ServerProcess.Start("--stdio");
        await RunFullLifecycleAsync(server);
        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(0, exitCode);

        var stdout = await server.WaitForStdoutStableAsync().WithTimeout("stdout drain");
        (List<string> frames, var consumed) = FrameParser.Parse(stdout);
        Assert.Equal(stdout.Length, consumed);

        foreach (var frame in frames)
        {
            var json = JObject.Parse(frame);
            Assert.Equal("2.0", json["jsonrpc"]?.Value<string>());
        }
    }

    [Fact]
    public async Task EndToEnd_Lifecycle_ExitsCleanly_WithExpectedFrames()
    {
        using var server = ServerProcess.Start("--stdio");

        server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{Environment.ProcessId},\"rootPath\":null}}"));
        JObject initialize = await server.WaitForResponseAsync(1).WithTimeout("initialize response");
        Assert.Equal("cvolo-language-server", initialize["result"]!["serverInfo"]!["name"]!.Value<string>());
        Assert.NotNull(initialize["result"]!["capabilities"]);
        Assert.True(initialize["result"]!["capabilities"]!["textDocumentSync"]!["openClose"]!.Value<bool>());
        Assert.Equal(2, initialize["result"]!["capabilities"]!["textDocumentSync"]!["change"]!.Value<int>());

        server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));
        server.SendJson(JsonRpcFrames.Request(2, "shutdown", "null"));
        JObject shutdown = await server.WaitForResponseAsync(2).WithTimeout("shutdown response");
        Assert.Equal(JTokenType.Null, shutdown["result"]!.Type);

        server.SendJson(JsonRpcFrames.Notification("exit", "null"));
        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task EndToEnd_DocumentSync_OpenChangeClose_ExitsCleanly()
    {
        using var workspace = TestWorkspace.CreateProject(["main.cvl"]);
        using var server = ServerProcess.Start("--stdio");

        server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{UriEscape(workspace.DirectoryPath)}\",\"name\":\"w\"}}]}}"));
        await server.WaitForResponseAsync(1).WithTimeout("initialize response");

        string uri = new Uri(Path.Combine(workspace.DirectoryPath, "main.cvl")).AbsoluteUri;
        string document = $"{{\"uri\":\"{uri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"int Main() {{ return 0; }}\"}}";

        server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen", $"{{\"textDocument\":{document}}}"));
        server.SendJson(JsonRpcFrames.Notification("textDocument/didChange", $"{{\"textDocument\":{{\"uri\":\"{uri}\",\"version\":2}},\"contentChanges\":[{{\"range\":{{\"start\":{{\"line\":0,\"character\":12}},\"end\":{{\"line\":0,\"character\":12}}}},\"text\":\" // edited\"}}]}}"));
        server.SendJson(JsonRpcFrames.Notification("textDocument/didClose", $"{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}"));
        server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));
        server.SendJson(JsonRpcFrames.Request(2, "shutdown", "null"));
        await server.WaitForResponseAsync(2).WithTimeout("shutdown response");
        server.SendJson(JsonRpcFrames.Notification("exit", "null"));

        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(0, exitCode);
    }

    private static string UriEscape(string path)
    {
        return new Uri(path).AbsoluteUri;
    }

    [Fact]
    public async Task VersionFlag_PrintsExactlyFourLabelledLines_ExitsZero()
    {
        using var server = ServerProcess.Start("--version");
        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(0, exitCode);

        var lines = await ReadVersionLinesAsync(server);

        Assert.Equal(4, lines.Length);
        Assert.Matches(@"^Cvolo Language Server \d+\.\d+\.\d+", lines[0]);
        Assert.Matches(@"^Commit: [0-9a-f]{40}$", lines[1]);
        Assert.Matches(@"^Tooling: \d+\.\d+\.\d+", lines[2]);
        Assert.Matches(@"^Compiler compatibility: \d+\.\d+", lines[3]);

        // The product version must be a clean SemVer with no build metadata, and must
        // not be derived from the compiler line.
        var productVersion = lines[0]["Cvolo Language Server ".Length..];
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?$", productVersion);
        var compilerLine = lines[3]["Compiler compatibility: ".Length..];
        Assert.NotEqual(compilerLine, productVersion);
    }

    [Fact]
    public async Task VersionFlagWithJson_PrintsOneMachineReadableObject_ExitsZero()
    {
        using var server = ServerProcess.Start("--version", "--json");
        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.Equal(0, exitCode);

        var lines = await ReadVersionLinesAsync(server);
        Assert.Single(lines);

        using var document = System.Text.Json.JsonDocument.Parse(lines[0]);
        var root = document.RootElement;

        // The field names are deliberately the bundle-manifest.json field names, so a
        // consumer can compare the two without translation.
        foreach (var field in new[]
                 {
                     "languageServerVersion", "languageServerCommit", "toolingVersion", "toolingCommit",
                     "compilerCompatibilityLine", "rid", "targetFramework", "runtimeVersion",
                 })
        {
            Assert.True(root.TryGetProperty(field, out _), $"--version --json must report '{field}'");
        }

        Assert.Matches(@"^[0-9a-f]{40}$", root.GetProperty("languageServerCommit").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+(\.\d+)?$", root.GetProperty("toolingVersion").GetString());
        Assert.Matches(@"^\d+\.\d+", root.GetProperty("compilerCompatibilityLine").GetString());
    }

    [Fact]
    public async Task JsonWithoutVersion_IsRejected_WithNonZeroExit()
    {
        using var server = ServerProcess.Start("--json");
        var exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
        Assert.NotEqual(0, exitCode);
    }

    private static async Task<string[]> ReadVersionLinesAsync(ServerProcess server)
    {
        var stdout = await server.WaitForStdoutStableAsync().WithTimeout("stdout drain");
        return System.Text.Encoding.UTF8
            .GetString(stdout)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task RunFullLifecycleAsync(ServerProcess server)
    {
        server.SendJson(JsonRpcFrames.Request(1, "initialize", $"{{\"processId\":{Environment.ProcessId},\"rootPath\":null}}"));
        await server.WaitForResponseAsync(1).WithTimeout("initialize response");

        server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));
        server.SendJson(JsonRpcFrames.Request(2, "shutdown", "null"));
        await server.WaitForResponseAsync(2).WithTimeout("shutdown response");

        server.SendJson(JsonRpcFrames.Notification("exit", "null"));
    }
}