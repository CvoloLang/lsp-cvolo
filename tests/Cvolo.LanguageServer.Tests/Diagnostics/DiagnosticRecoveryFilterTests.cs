using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

/// <summary>
/// INC 3.1 G1(b): the language server no longer suppresses a semantic diagnostic merely because
/// a parser error shares its (document, span start). Same-start/code pairing is not causal proof,
/// so the conservative fallback is to retain every diagnostic. Any future suppression must be
/// driven by compiler-attested recovery-group provenance, not this heuristic.
/// </summary>
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
    public void SameLocation_ParserAndSemanticDiagnostics_AreBothRetained()
    {
        var run = new BackendDiagnosticRun(
            new Dictionary<DocumentUri, string> { [Uri] = "W\nGL.DeleteBuffers(...);" },
            [Diagnostic("CVL4004", 2), Diagnostic("CVL1045", 2), Diagnostic("CVL9999", 2)]);

        // No heuristic is applied inside the scheduler any more; every diagnostic survives.
        Assert.Equal(
            new[] { "CVL4004", "CVL1045", "CVL9999" },
            run.Diagnostics.Select(d => d.Code));
    }

    [Fact]
    public void ParserSyntaxError_IsNeverHiddenBySemanticLocationOverlap()
    {
        var run = new BackendDiagnosticRun(
            new Dictionary<DocumentUri, string> { [Uri] = "bad declaration" },
            [Diagnostic("CVL4004", 1), Diagnostic("CVL1045", 4)]);

        Assert.Contains(run.Diagnostics, d => d.Code == "CVL4004");
        Assert.Equal(2, run.Diagnostics.Count);
    }
}
