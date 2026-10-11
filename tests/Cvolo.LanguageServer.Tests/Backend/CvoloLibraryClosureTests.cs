using Cvolo.Compiler.Tooling;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Logging;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Backend;

public class CvoloLibraryClosureTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly RecordingCoreLogger _logger;
    private readonly CvoloLanguageBackend _backend;

    public CvoloLibraryClosureTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"], _ => "int Main() { return 0; }\n");
        _logger = new RecordingCoreLogger();
        _backend = new CvoloLanguageBackend([_workspace.DirectoryPath], null, _logger);
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private DocumentUri MainUri => DocumentUri.Create(_workspace.PathOf("main.cvl"));

    private static bool IsConsole(DocumentSnapshot document) =>
        document.FilePath.EndsWith(Path.Combine("System", "Console.cvl"), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void BodyOnlyEdit_DoesNotRediscoverProject()
    {
        var project = (CvoloProjectSession)_backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);
        CvoloProject original = project.Project;

        var first = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, "int Main() { return 1; }\n");
        var second = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, "int Main() { return 2; }\n");

        Assert.Same(original, project.Project);
        Assert.True(second.Generation > first.Generation);
    }

    [Fact]
    public void UsingFollowedByBodyEdit_DoesNotRediscoverProject()
    {
        var project = (CvoloProjectSession)_backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);
        _backend.UpdateDocument(project, handle, "using System;\nint Main() { return 0; }\n");
        CvoloProject original = project.Project;

        _backend.UpdateDocument(project, handle, "using System;\nint Main() { return 1; }\n");

        Assert.Same(original, project.Project);
    }

    [Fact]
    public void DidChangeAddingUsing_AdvancesClosureWithoutRestart()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);
        const string edited = "using System;\nint Main() { Console.WriteLine(\"hi\"); return 0; }\n";

        var snapshot = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, edited);

        Assert.Contains(snapshot.Snapshot.Documents.Values, IsConsole);
        Assert.Equal(edited, snapshot.Snapshot.GetDocument(((CvoloDocumentHandle)handle).DocumentId).Text.ToString());
        Assert.Equal("int Main() { return 0; }\n", File.ReadAllText(MainUri.LocalPath));

        var consolePath = snapshot.Snapshot.Documents.Values.Single(IsConsole).FilePath;
        Assert.True(_backend.TryResolveDocument(project, DocumentUri.Create(consolePath), out _));
    }

    [Fact]
    public void DidChangeRemovingUsing_AdvancesClosureCoherently()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);
        _backend.UpdateDocument(project, handle, "using System;\nint Main() { Console.WriteLine(\"hi\"); return 0; }\n");

        var snapshot = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, "int Main() { return 0; }\n");

        Assert.DoesNotContain(snapshot.Snapshot.Documents.Values, IsConsole);
    }

    [Fact]
    public void DefinitionIntoSelectedStd_UsesRealSourceFileUri()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);
        const string edited = "using System;\nint Main() { Console.WriteLine(\"hi\"); return 0; }\n";
        _backend.UpdateDocument(project, handle, edited);
        var snapshot = (ToolingBackendSnapshot)_backend.CaptureCurrentSnapshot(project);

        var symbol = snapshot.Snapshot
            .GetDocument(((CvoloDocumentHandle)handle).DocumentId)
            .GetSymbolAtPosition(edited.IndexOf("WriteLine", StringComparison.Ordinal));

        Assert.NotNull(symbol);
        var definitions = snapshot.Snapshot.GetDefinitions(symbol!.SymbolId);
        Assert.NotEmpty(definitions);
        var document = snapshot.Snapshot.GetDocument(definitions[0].DocumentId);
        Assert.True(IsConsole(document));
        Assert.True(File.Exists(document.FilePath));
    }

    [Fact]
    public void CoreOnlyProject_HasNoFalseDiagnostics()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);
        var snapshot = (ToolingBackendSnapshot)_backend.CaptureCurrentSnapshot(project);

        var diagnostics = snapshot.Snapshot
            .GetDocument(((CvoloDocumentHandle)handle).DocumentId)
            .GetDiagnostics();

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ClosureChangingEdit_AdvancesGeneration_AndInvalidatesOldSnapshot()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);
        var before = (ToolingBackendSnapshot)_backend.CaptureCurrentSnapshot(project);

        var after = (ToolingBackendSnapshot)_backend.UpdateDocument(
            project,
            handle,
            "using System;\nint Main() { Console.WriteLine(\"hi\"); return 0; }\n");

        Assert.False(_backend.IsCurrentSnapshot(project, before));
        Assert.True(_backend.IsCurrentSnapshot(project, after));
    }

    [Fact]
    public void StdSourceWorkspace_DoesNotDuplicateBundledStd()
    {
        var stdPath = Path.Combine(AppContext.BaseDirectory, "libraries", "System", "Math", "Constants.cvl");
        Assert.True(File.Exists(stdPath), "the test host must ship the standard library");
        var stdUri = DocumentUri.Create(stdPath);
        var backend = new CvoloLanguageBackend([Path.GetDirectoryName(stdPath)!], null, _logger);

        var project = backend.OpenProject(stdUri);

        Assert.NotNull(project);
        Assert.True(backend.TryResolveDocument(project!, stdUri, out var handle));
        var snapshot = (ToolingBackendSnapshot)backend.CaptureCurrentSnapshot(project!);
        var document = snapshot.Snapshot.GetDocument(((CvoloDocumentHandle)handle).DocumentId);
        Assert.DoesNotContain(
            document.GetDiagnostics(),
            diagnostic => diagnostic.Message.Contains("Duplicate definition", StringComparison.Ordinal));
    }

    [Fact]
    public void NewLooseFileAddedAfterOpen_IsResolvedByRefreshingProject()
    {
        using var workspace = TestWorkspace.CreateDirectory();
        var memoryDirectory = Path.Combine(workspace.DirectoryPath, "Memory");
        Directory.CreateDirectory(memoryDirectory);
        var allocatorPath = Path.Combine(memoryDirectory, "Allocator.cvl");
        File.WriteAllText(allocatorPath, "int Allocate() { return 0; }\n");
        var backend = new CvoloLanguageBackend([workspace.DirectoryPath], null, _logger);
        var allocatorUri = DocumentUri.Create(allocatorPath);
        var project = backend.OpenProject(allocatorUri)!;
        Assert.True(backend.TryResolveDocument(project, allocatorUri, out _));

        var layoutPath = Path.Combine(memoryDirectory, "Layout.cvl");
        File.WriteAllText(layoutPath, "int Layout() { return 1; }\n");
        var layoutUri = DocumentUri.Create(layoutPath);

        Assert.True(backend.TryResolveDocument(project, layoutUri, out var handle));
        var snapshot = (ToolingBackendSnapshot)backend.CaptureCurrentSnapshot(project);
        Assert.Equal("int Layout() { return 1; }\n", snapshot.Snapshot.GetDocument(((CvoloDocumentHandle)handle).DocumentId).Text.ToString());
    }
}
