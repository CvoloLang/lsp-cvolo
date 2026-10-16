using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Protocol coverage for <c>textDocument/prepareRename</c> and <c>textDocument/rename</c>:
/// a rename target is reported with its exact range, and a rename returns a workspace edit
/// touching every occurrence and nothing else.
/// </summary>
public class RenameHandlerTests : IDisposable
{
    private const string MainSource =
        "int main() {\n" +
        "    val int count = 1;\n" +
        "    return count + count;\n" +
        "}\n";

    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public RenameHandlerTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"], _ => MainSource);
        _session = ProtocolSession.Start();
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private async Task InitializeAndOpenAsync()
    {
        await _session.Client
            .InitializeAsync(processId: null, rootPath: _workspace.DirectoryPath)
            .WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
        await _session.Client
            .NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, MainSource)
            .WithTimeout("didOpen");
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
            else
            {
                character++;
            }
        }

        return (line, character);
    }

    private static List<TextEditPayload> Edits(WorkspaceEditPayload edit)
    {
        if (edit.Changes is { } changes)
        {
            return [.. changes.SelectMany(pair => pair.Value)];
        }

        if (edit.DocumentChanges is { } documentChanges)
        {
            return [.. documentChanges.SelectMany(change => change.Edits)];
        }

        return [];
    }

    [Fact]
    public async Task PrepareRename_OnAnOccurrence_ReturnsRangeAndPlaceholder()
    {
        await InitializeAndOpenAsync();
        var (line, character) = Position(MainSource, "count", delta: 1);

        PrepareRenameResultPayload? prepared = await _session.Client
            .PrepareRenameAsync(_workspace.DocumentUri("main.cvl"), line, character)
            .WithTimeout("prepareRename");

        Assert.NotNull(prepared);
        Assert.Equal("count", prepared!.Placeholder);
        Assert.Equal(line, prepared.Range.Start.Line);
        Assert.Equal(character - 1, prepared.Range.Start.Character);
        Assert.Equal(5, prepared.Range.End.Character - prepared.Range.Start.Character);
    }

    [Fact]
    public async Task Rename_RenamesEveryOccurrence()
    {
        await InitializeAndOpenAsync();
        var (line, character) = Position(MainSource, "count", delta: 1);

        WorkspaceEditPayload edit = await _session.Client
            .RenameAsync(_workspace.DocumentUri("main.cvl"), line, character, "total")
            .WithTimeout("rename");

        List<TextEditPayload> edits = Edits(edit);
        Assert.Equal(3, edits.Count);
        Assert.All(edits, e => Assert.Equal("total", e.NewText));
        Assert.All(edits, e => Assert.Equal(5, e.Range.End.Character - e.Range.Start.Character));
    }
}
