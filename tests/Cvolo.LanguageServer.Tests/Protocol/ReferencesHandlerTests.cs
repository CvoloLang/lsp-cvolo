using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Protocol coverage for <c>textDocument/references</c> against the real Cvolo-backed store:
/// a symbol's occurrences are reported and honour the <c>includeDeclaration</c> context flag.
/// </summary>
public class ReferencesHandlerTests : IDisposable
{
    private const string MainSource =
        "int main() {\n" +
        "    val int count = 1;\n" +
        "    return count + count;\n" +
        "}\n";

    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public ReferencesHandlerTests()
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

    [Fact]
    public async Task References_IncludingTheDeclaration_ReportsEveryOccurrence()
    {
        await InitializeAndOpenAsync();
        var (line, character) = Position(MainSource, "count", delta: 1);

        Location[]? references = await _session.Client
            .ReferencesAsync(_workspace.DocumentUri("main.cvl"), line, character, includeDeclaration: true)
            .WithTimeout("references");

        Assert.NotNull(references);
        Assert.Equal(3, references!.Length);
        Assert.All(references, location => Assert.EndsWith("main.cvl", location.Uri.LocalPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task References_OmittingTheDeclaration_ReturnsOnlyUses()
    {
        await InitializeAndOpenAsync();
        var (line, character) = Position(MainSource, "count", delta: 1);

        Location[]? withoutDeclaration = await _session.Client
            .ReferencesAsync(_workspace.DocumentUri("main.cvl"), line, character, includeDeclaration: false)
            .WithTimeout("references without declaration");

        Assert.NotNull(withoutDeclaration);
        Assert.Equal(2, withoutDeclaration!.Length);
    }
}
