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

        // The lexer diagnostic is the point of this test. A stray '$' is also followed by a
        // parser-level error on the enclosing '}' (the lexer drops the character); that is an
        // independent, pre-existing parser behaviour, so assert the lexer origin precisely
        // rather than assuming it is the only error in the run.
        BackendDiagnostic diagnostic = Assert.Single(diagnostics, d => d.Code == "CVL1911");

        Assert.Equal(BackendDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(new TextSpan(20, 1), diagnostic.Location.Span);
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

        Assert.True(run.Diagnostics.Count(d => d.Severity == BackendDiagnosticSeverity.Error) >= 2);
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
