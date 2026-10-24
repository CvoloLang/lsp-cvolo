using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

// Process startup and forced termination are unusually sensitive to worker
// saturation under Windows CI. Run this collection without concurrent test
// collections while preserving the actual server-process E2E assertions.
[CollectionDefinition("IsolatedProcessLifecycle", DisableParallelization = true)]
public sealed class IsolatedProcessLifecycleCollection;

[Collection("IsolatedProcessLifecycle")]
public class ProcessLifecycleTests
{

    [Fact]
    public async Task RealProcess_RecordingTransitionsRetainPidIdentityAndEpoch()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "cvolo-lsp-mode-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string trace = Path.Combine(directory, "trace.jsonl");
        try
        {
            using var server = ServerProcess.StartWithEnvironment(
                new Dictionary<string, string?>
                {
                    ["CVOLO_OBS_MODE"] = "off",
                    ["CVOLO_TRACE_PATH"] = trace,
                }, "--stdio");
            int pid = server.Id;
            await InitializeProcessAsync(server);
            JObject initial = await RequestRecordingStatusAsync(server, 10);
            Assert.True((bool)initial["supported"]!);
            Assert.Equal("off", (string?)initial["effectiveMode"]);
            Assert.Equal(0, (long)initial["recordingEpoch"]!);
            string instance = (string)initial["serverInstanceId"]!;
            Assert.False(string.IsNullOrWhiteSpace(instance));
            Assert.False(File.Exists(trace));

            // Before activation, requests must not silently create a log or
            // acquire a retroactive trace when the observer starts later.
            server.SendJson(JsonRpcFrames.Request(11, "cvolo/unknownRequest", "null"));
            JObject before = await server.WaitForResponseAsync(11, TimeSpan.FromSeconds(20));
            Assert.NotNull(before["error"]);
            Assert.False(File.Exists(trace));

            var sequence = new[] { "basic", "detailed", "off", "basic", "off" };
            long epoch = 0;
            int requestId = 20;
            foreach (string mode in sequence)
            {
                JObject ack = await RequestRecordingModeAsync(server, requestId++, mode);
                Assert.Equal("applied", (string?)ack["status"]);
                Assert.Equal(mode, (string?)ack["effectiveMode"]);
                Assert.Equal(++epoch, (long)ack["recordingEpoch"]!);
                Assert.Equal(instance, (string?)ack["serverInstanceId"]);
                Assert.NotNull(ack["appliedAtUtc"]);
                Assert.False((bool)ack["captureSources"]!);
                Assert.False((bool)ack["capturePaths"]!);
                Assert.False((bool)ack["forensicScope"]!);
                Assert.False(server.HasExited);
                Assert.Equal(pid, server.Id);
                JObject status = await RequestRecordingStatusAsync(server, requestId++);
                Assert.Equal(mode, (string?)status["effectiveMode"]);
                Assert.Equal(epoch, (long)status["recordingEpoch"]!);
                Assert.Equal(instance, (string?)status["serverInstanceId"]);
                if (mode != "off")
                {
                    Assert.True((bool)ack["diskRecordingActive"]!);
                    Assert.True(File.Exists(trace));
                }
                else
                {
                    Assert.False((bool)ack["diskRecordingActive"]!);
                }
            }

            server.SendJson(JsonRpcFrames.Request(90, "shutdown", "null"));
            JObject shutdown = await server.WaitForResponseAsync(90, TimeSpan.FromSeconds(20));
            Assert.Equal(JTokenType.Null, shutdown["result"]!.Type);
            server.SendJson(JsonRpcFrames.Notification("exit", "null"));
            Assert.Equal(0, await server.WaitForExitAsync(TimeSpan.FromSeconds(20)));

            string[] lines = await File.ReadAllLinesAsync(trace);
            Assert.NotEmpty(lines);
            foreach (string line in lines)
                Assert.Equal(JTokenType.Object, JObject.Parse(line).Type);
            // Verify no private source content was needed to toggle modes.
            Assert.DoesNotContain("cvolo-lsp-mode-e2e-", string.Join("\n", lines));
            Assert.DoesNotContain("cvolo/unknownRequest", string.Join("\n", lines));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RealProcess_FailedWriterStartKeepsStdoutFramedAndModeOff()
    {
        // Deliberately pass a directory as the output file: creating a JSONL
        // FileStream fails without requiring root permissions or disk filling.
        string directory = Path.Combine(Path.GetTempPath(),
            "cvolo-lsp-writer-fault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var server = ServerProcess.StartWithEnvironment(
                new Dictionary<string, string?>
                {
                    ["CVOLO_OBS_MODE"] = "off",
                    ["CVOLO_TRACE_PATH"] = directory,
                }, "--stdio");
            await InitializeProcessAsync(server);
            JObject initial = await RequestRecordingStatusAsync(server, 50);
            Assert.Equal("off", (string?)initial["effectiveMode"]);

            JObject failed = await RequestRecordingModeAsync(server, 51, "detailed");
            Assert.Equal("rejected", (string?)failed["status"]);
            Assert.Equal("writer_open_failed", (string?)failed["reason"]);
            Assert.Equal("off", (string?)failed["effectiveMode"]);
            Assert.Equal(0, (long)failed["recordingEpoch"]!);
            Assert.False((bool)failed["diskRecordingActive"]!);

            JObject after = await RequestRecordingStatusAsync(server, 52);
            Assert.Equal("off", (string?)after["effectiveMode"]);
            Assert.Equal((string?)initial["serverInstanceId"],
                (string?)after["serverInstanceId"]);
            Assert.False(server.HasExited);

            server.SendJson(JsonRpcFrames.Request(53, "shutdown", "null"));
            Assert.Equal(JTokenType.Null,
                (await server.WaitForResponseAsync(53, TimeSpan.FromSeconds(20)))["result"]!.Type);
            server.SendJson(JsonRpcFrames.Notification("exit", "null"));
            Assert.Equal(0, await server.WaitForExitAsync(TimeSpan.FromSeconds(20)));

            byte[] stdout = await server.WaitForStdoutStableAsync(TimeSpan.FromSeconds(10));
            (List<string> messages, int consumed) = FrameParser.Parse(stdout);
            Assert.Equal(stdout.Length, consumed);
            Assert.NotEmpty(messages);
            foreach (string message in messages)
                Assert.Equal("2.0", JObject.Parse(message)["jsonrpc"]?.Value<string>());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RealProcess_RestartGivesFreshServerIdentityAndOffEpoch()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "cvolo-lsp-restart-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string? firstId;
            var env = new Dictionary<string, string?>
            {
                ["CVOLO_OBS_MODE"] = "off",
                ["CVOLO_TRACE_PATH"] = Path.Combine(directory, "trace1.jsonl"),
            };
            using (var first = ServerProcess.StartWithEnvironment(env, "--stdio"))
            {
                await InitializeProcessAsync(first);
                JObject initial = await RequestRecordingStatusAsync(first, 40);
                firstId = (string?)initial["serverInstanceId"];
                Assert.False(string.IsNullOrWhiteSpace(firstId));
                JObject started = await RequestRecordingModeAsync(first, 41, "basic");
                Assert.Equal("applied", (string?)started["status"]);
                Assert.Equal(1, (long)started["recordingEpoch"]!);
                first.SendJson(JsonRpcFrames.Request(42, "shutdown", "null"));
                await first.WaitForResponseAsync(42, TimeSpan.FromSeconds(20));
                first.SendJson(JsonRpcFrames.Notification("exit", "null"));
                Assert.Equal(0, await first.WaitForExitAsync(TimeSpan.FromSeconds(20)));
            }

            env["CVOLO_TRACE_PATH"] = Path.Combine(directory, "trace2.jsonl");
            using (var second = ServerProcess.StartWithEnvironment(env, "--stdio"))
            {
                await InitializeProcessAsync(second);
                JObject status = await RequestRecordingStatusAsync(second, 40);
                Assert.NotEqual(firstId, (string?)status["serverInstanceId"]);
                Assert.Equal("off", (string?)status["effectiveMode"]);
                Assert.Equal(0, (long)status["recordingEpoch"]!);
                Assert.False(File.Exists(env["CVOLO_TRACE_PATH"]!));
                second.SendJson(JsonRpcFrames.Request(42, "shutdown", "null"));
                await second.WaitForResponseAsync(42, TimeSpan.FromSeconds(20));
                second.SendJson(JsonRpcFrames.Notification("exit", "null"));
                Assert.Equal(0, await second.WaitForExitAsync(TimeSpan.FromSeconds(20)));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task InitializeProcessAsync(ServerProcess server)
    {
        server.SendJson(JsonRpcFrames.Request(1, "initialize",
            "{\"processId\":null,\"rootPath\":null}"));
        JObject init = await server.WaitForResponseAsync(1, TimeSpan.FromSeconds(20));
        Assert.NotNull(init["result"]?["capabilities"]);
        server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));
    }

    private static async Task<JObject> RequestRecordingStatusAsync(ServerProcess server, int id)
    {
        server.SendJson(JsonRpcFrames.Request(id,
            "cvolo/observability/status.v1", "null"));
        JObject reply = await server.WaitForResponseAsync(id, TimeSpan.FromSeconds(20));
        Assert.Null(reply["error"]);
        return (JObject)reply["result"]!;
    }

    private static async Task<JObject> RequestRecordingModeAsync(
        ServerProcess server, int id, string mode)
    {
        var parameters = new JObject
        {
            ["protocolVersion"] = 1, ["mode"] = mode,
            ["captureSources"] = false, ["capturePaths"] = false,
            ["forensicScope"] = false,
        };
        server.SendJson(JsonRpcFrames.Request(id,
            "cvolo/observability/setRecording.v1",
            parameters.ToString(Newtonsoft.Json.Formatting.None)));
        JObject reply = await server.WaitForResponseAsync(id, TimeSpan.FromSeconds(20));
        Assert.Null(reply["error"]);
        return (JObject)reply["result"]!;
    }

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
        // WaitForResponseAsync has its own five-second default timeout.
        // Supply the intended CI startup allowance to that inner wait too,
        // otherwise the outer 20-second guard never takes effect.
        JObject response = await server.WaitForResponseAsync(1, TimeSpan.FromSeconds(20))
            .WithTimeout("initialize response", TimeSpan.FromSeconds(25));
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