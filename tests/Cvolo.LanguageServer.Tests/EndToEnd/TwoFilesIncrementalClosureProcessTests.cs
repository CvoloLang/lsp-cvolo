using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.EndToEnd;

/// <summary>
/// S5 end-to-end coverage for the INC "Incremental Semantic Snapshots" pipeline:
/// a real server process rebuilt an import closure from captured inputs after a
/// <c>using</c> edit, and navigation resolves against the rebuilt generation.
/// </summary>
public class TwoFilesIncrementalClosureProcessTests
{
    [Fact]
    public async Task AddingUsing_OverStdio_RebuildsClosureAndResolvesNavigation()
    {
        string fixtureSource = Path.Combine(FindRepoRoot(), "tests", "fixtures", "cvolo-lsp-v8", "projects", "TwoFiles");
        string workspace = Path.Combine(Path.GetTempPath(), "cvolo-ls-twofiles-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(fixtureSource, workspace);

        try
        {
            using var server = ServerProcess.Start("--stdio");
            string folderUri = new Uri(workspace).AbsoluteUri;
            server.SendJson(JsonRpcFrames.Request(1, "initialize",
                $"{{\"processId\":{Environment.ProcessId},\"workspaceFolders\":[{{\"uri\":\"{folderUri}\",\"name\":\"w\"}}]}}"));
            await server.WaitForResponseAsync(1).WithTimeout("initialize response");
            server.SendJson(JsonRpcFrames.Notification("initialized", "{}"));

            string aPath = Path.Combine(workspace, "A.cvl");
            string bPath = Path.Combine(workspace, "B.cvl");
            string aUri = new Uri(aPath).AbsoluteUri;
            string bUri = new Uri(bPath).AbsoluteUri;

            string initialA = File.ReadAllText(aPath);
            string initialB = File.ReadAllText(bPath);

            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen",
                $"{{\"textDocument\":{{\"uri\":\"{aUri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{Escape(initialA)}\"}}}}"));
            server.SendJson(JsonRpcFrames.Notification("textDocument/didOpen",
                $"{{\"textDocument\":{{\"uri\":\"{bUri}\",\"languageId\":\"cvolo\",\"version\":1,\"text\":\"{Escape(initialB)}\"}}}}"));

            // Add the `using` that pulls in the Widgets namespace declared in B.cvl.
            // This changes the import set, forcing the backend to rebuild the
            // closure from captured inputs (S2/S3).
            string editedA = "using Widgets;\r\n\r\n" + initialA;
            server.SendJson(JsonRpcFrames.Notification("textDocument/didChange",
                $"{{\"textDocument\":{{\"uri\":\"{aUri}\",\"version\":2}},\"contentChanges\":[{{\"text\":\"{Escape(editedA)}\"}}]}}"));

            (int line, int character) = PositionOfIdentifier(editedA, "Foo");

            int id = 2;
            JObject? definition = null;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                server.SendJson(JsonRpcFrames.Request(id, "textDocument/definition",
                    $"{{\"textDocument\":{{\"uri\":\"{aUri}\"}},\"position\":{{\"line\":{line},\"character\":{character}}}}}"));
                definition = await server.WaitForResponseAsync(id).WithTimeout("definition response");
                id++;
                if (definition["result"] is JArray { Count: > 0 })
                {
                    break;
                }

                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            Assert.NotNull(definition);
            Assert.Null(definition!["error"]);
            var locations = definition["result"] as JArray;
            Assert.NotNull(locations);
            Assert.NotEmpty(locations!);
            string first = locations![0]?.ToString() ?? string.Empty;
            Assert.Contains("B.cvl", first, StringComparison.OrdinalIgnoreCase);

            server.SendJson(JsonRpcFrames.Request(id, "shutdown", "null"));
            await server.WaitForResponseAsync(id).WithTimeout("shutdown response");
            server.SendJson(JsonRpcFrames.Notification("exit", "null"));

            int exitCode = await server.WaitForExitAsync().WithTimeout("server exit");
            Assert.Equal(0, exitCode);
        }
        finally
        {
            TryDeleteDirectory(workspace);
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

    private static (int Line, int Character) PositionOfIdentifier(string text, string identifier)
    {
        int offset = text.IndexOf(identifier, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"identifier '{identifier}' not found");
        return OffsetToPosition(text, offset);
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

        foreach (string directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
