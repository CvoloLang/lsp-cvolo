using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Backend;

public class CvoloLexerDiagnosticTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly CvoloLanguageBackend _backend;

    public CvoloLexerDiagnosticTests()
    {
        _workspace = TestWorkspace.CreateProject(
            ["main.cvl"],
            _ => "int Main() { return 0; }");
        _backend = new CvoloLanguageBackend([_workspace.DirectoryPath], null, new RecordingCoreLogger());
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private DocumentUri MainUri => DocumentUri.Create(_workspace.PathOf("main.cvl"));

    [Fact]
    public void UnrecognizedCharacter_IsPublishedAsAnEditorDiagnostic()
    {
        const string source = "int Main() { return $; }";
        var project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = Resolve(project, MainUri);
        BackendSnapshot snapshot = _backend.UpdateDocument(project, handle, source);

        var diagnostics = _backend.GetDiagnostics(snapshot, [handle]).Diagnostics;

        BackendDiagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == "CVL1911");

        Assert.Equal(BackendDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(new TextSpan(source.IndexOf('$'), 1), diagnostic.Location.Span);
        Assert.True(DocumentUriPathComparer.Instance.Equals(MainUri, diagnostic.Location.Document));
        Assert.DoesNotContain(diagnostics, d => d.Code == "CVL0000");
    }

    [Fact]
    public void UnrecognizedCharacter_IsReportedOncePerOccurrence()
    {
        const string source = "int Main() { return $; }\nint Lib() { return @; }";
        var project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = Resolve(project, MainUri);
        BackendSnapshot snapshot = _backend.UpdateDocument(project, handle, source);

        var run = _backend.GetDiagnostics(snapshot, [handle]);

        Assert.Equal(2, run.Diagnostics.Count(d => d.Code == "CVL1911"));
        Assert.DoesNotContain(run.Diagnostics, d => d.Code == "CVL0000");
    }

    [Fact]
    public void QuestionMark_IsNotALexerError()
    {
        const string source = "int Main() { return ?; }";
        var project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = Resolve(project, MainUri);
        BackendSnapshot snapshot = _backend.UpdateDocument(project, handle, source);

        var run = _backend.GetDiagnostics(snapshot, [handle]);

        Assert.DoesNotContain(run.Diagnostics, d => d.Code == "CVL1911");
        Assert.DoesNotContain(run.Diagnostics, d => d.Code == "CVL0000");
    }

    private BackendDocumentHandle Resolve(BackendProject project, DocumentUri uri)
    {
        Assert.True(_backend.TryResolveDocument(project, uri, out BackendDocumentHandle handle));
        return handle;
    }
}
