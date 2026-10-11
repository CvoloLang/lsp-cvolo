using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Diagnostics;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

public class DiagnosticRecoveryFilterTests
{
    private static readonly DocumentUri Uri = DocumentUri.Create(Path.Combine(Path.GetTempPath(), "recovery.cvl"));

    private static BackendDiagnostic Diagnostic(string code, int offset) => new(
        BackendDiagnosticSeverity.Error,
        code,
        "parser issue",
        new BackendDiagnosticLocation(Uri, new TextSpan(offset, 1), null),
        []);

    [Fact]
    public void SameLocation_NoViableAlternative_DropsOnlyRecoveryCascade()
    {
        var run = new BackendDiagnosticRun(
            new Dictionary<DocumentUri, string> { [Uri] = "W\nGL.DeleteBuffers(...);" },
            [Diagnostic("CVL4004", 2), Diagnostic("CVL1045", 2), Diagnostic("CVL9999", 2)]);

        var result = DiagnosticScheduler.CollapseParserRecoveryDiagnostics(run);

        Assert.Equal(new[] { "CVL4004", "CVL9999" }, result.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void GenuineAssociatedFunctionDiagnostic_IsNotHidden()
    {
        var run = new BackendDiagnosticRun(
            new Dictionary<DocumentUri, string> { [Uri] = "bad declaration" },
            [Diagnostic("CVL4004", 1), Diagnostic("CVL1045", 4)]);

        var result = DiagnosticScheduler.CollapseParserRecoveryDiagnostics(run);

        Assert.Equal(2, result.Diagnostics.Count);
    }
}
