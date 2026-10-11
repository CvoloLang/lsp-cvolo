using Cvolo.Compiler.Tooling;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Logging;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Backend;

/// <summary>
/// Snapshot advancement keeps diagnostics and editor-intelligence queries tied to the same current
/// text. Transient syntax errors must still be surfaced as diagnostics without crashing semantic
/// queries that can answer from the captured snapshot.
/// </summary>
public class CvoloSnapshotStabilityTests : IDisposable
{
    private const string Valid = """
        public int Add(int left, int right) { return left + right; }

        public struct Placed { public int Offset; }

        public extension Placed
        {
            public Placed(int offset) { Offset = offset; }
            public Placed Shifted() { return Placed(Offset + 1); }
        }

        int Main() { val placed = Placed(4); return Add(placed.Offset, placed.Shifted().Offset); }
        """;

    /// <summary>One keystroke that cannot parse: the final closing brace becomes '@'.</summary>
    private static string WithTransientBreak(string source) => source[..^1] + "@";

    private readonly TestWorkspace _workspace;
    private readonly RecordingCoreLogger _logger;
    private readonly CvoloLanguageBackend _backend;

    public CvoloSnapshotStabilityTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"], _ => Valid);
        _logger = new RecordingCoreLogger();
        _backend = new CvoloLanguageBackend([_workspace.DirectoryPath], null, _logger);
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private DocumentUri MainUri => DocumentUri.Create(_workspace.PathOf("main.cvl"));

    private static IReadOnlyList<BackendCodeLensInfo> ReferenceLenses(
        CvoloLanguageBackend backend,
        BackendSnapshot snapshot,
        BackendDocumentHandle handle) =>
        backend.GetCodeLenses(snapshot, handle)
            .Where(lens => lens.Kind == BackendCodeLensKind.References)
            .ToList();

    private static IReadOnlyList<BackendDiagnostic> DiagnosticsFor(
        CvoloLanguageBackend backend,
        BackendSnapshot snapshot,
        BackendDocumentHandle handle) =>
        backend.GetDiagnostics(snapshot, [handle]).Diagnostics.ToList();

    private static int PositionOf(string needle, int offsetIntoNeedle = 0) =>
        Valid.IndexOf(needle, StringComparison.Ordinal) + offsetIntoNeedle;

    [Fact]
    public void TransientSyntaxError_ChangesDiagnosticsButKeepsNavigationStable()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);

        var settled = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, Valid);
        IReadOnlyList<BackendCodeLensInfo> beforeLenses = ReferenceLenses(_backend, settled, handle);
        IReadOnlyList<BackendDiagnostic> beforeDiagnostics = DiagnosticsFor(_backend, settled, handle);
        IReadOnlyList<BackendDocumentSymbol> beforeOutline = _backend.GetDocumentSymbols(settled, handle);

        Assert.NotEmpty(beforeLenses);
        Assert.Empty(beforeDiagnostics);
        Assert.NotEmpty(beforeOutline);

        var broken = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, WithTransientBreak(Valid));

        // Diagnostics always describe the current text: the syntax error is reported, never hidden
        // to keep the editor quiet.
        IReadOnlyList<BackendDiagnostic> brokenDiagnostics = DiagnosticsFor(_backend, broken, handle);
        Assert.Contains(brokenDiagnostics, d => ParserSyntaxDiagnostic.IsSyntaxError(d.Code));

        // Navigation keeps the last coherent state: same lenses, same anchors, same outline.
        IReadOnlyList<BackendCodeLensInfo> afterLenses = ReferenceLenses(_backend, broken, handle);
        Assert.Equal(beforeLenses.Count, afterLenses.Count);
        Assert.Equal(
            beforeLenses.Select(lens => (lens.Range.Start, lens.Range.Length, lens.Title)).ToList(),
            afterLenses.Select(lens => (lens.Range.Start, lens.Range.Length, lens.Title)).ToList());

        IReadOnlyList<BackendDocumentSymbol> afterOutline = _backend.GetDocumentSymbols(broken, handle);
        Assert.Equal(beforeOutline.Select(s => s.Name).ToList(), afterOutline.Select(s => s.Name).ToList());
    }

    [Fact]
    public void TrailingDotCompletion_KeepsWorkingDuringATransientSyntaxError()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);

        _backend.UpdateDocument(project, handle, Valid);
        var broken = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, WithTransientBreak(Valid));

        // Completion is answered from the current edit context plus the best available semantic
        // index, so `layout.` still offers FromSize while the rest of the file does not parse.
        int afterDot = PositionOf("placed.", "placed.".Length);
        var completion = _backend.GetCompletions(broken, handle, afterDot);
        Assert.Contains(completion.Items, item => item.Label == "Shifted");
    }

    [Fact]
    public void GoToDefinition_SurvivesATransientSyntaxError()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);

        var settled = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, Valid);
        int position = PositionOf("placed.Shifted", "placed.".Length);
        BackendSymbolInfo? before = _backend.GetSymbolAtPosition(settled, handle, position);
        Assert.NotNull(before);
        Assert.NotEmpty(_backend.GetDefinitions(settled, before.Symbol!).Targets);

        var broken = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, WithTransientBreak(Valid));
        BackendSymbolInfo? after = _backend.GetSymbolAtPosition(broken, handle, position);
        Assert.NotNull(after);
        Assert.NotEmpty(_backend.GetDefinitions(broken, after.Symbol!).Targets);
        Assert.NotEmpty(_backend.GetReferences(broken, after.Symbol!, includeDeclaration: false).Locations);
    }

    [Fact]
    public void RepairingTheEdit_ReplacesTheRetainedSnapshot()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);

        _backend.UpdateDocument(project, handle, Valid);
        var broken = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, WithTransientBreak(Valid));
        Assert.Contains(DiagnosticsFor(_backend, broken, handle), d => ParserSyntaxDiagnostic.IsSyntaxError(d.Code));

        var repaired = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, Valid + "\n");

        Assert.Empty(DiagnosticsFor(_backend, repaired, handle));
        Assert.True(repaired.Generation > broken.Generation);
    }

    [Fact]
    public void SustainedIncoherenceKeepsAdvancingTheCurrentSnapshot()
    {
        var project = _backend.OpenProject(MainUri)!;
        var handle = _backend.Resolve(project, MainUri);

        _backend.UpdateDocument(project, handle, Valid);
        ToolingBackendSnapshot? snapshot = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, WithTransientBreak(Valid));
        long firstBrokenGeneration = snapshot.Generation;
        for (int i = 0; i < 4; i++)
        {
            snapshot = (ToolingBackendSnapshot)_backend.UpdateDocument(project, handle, WithTransientBreak(Valid) + "\n// " + new string('x', i));
        }

        Assert.NotNull(snapshot);
        Assert.True(snapshot.Generation > firstBrokenGeneration);
        Assert.Contains(DiagnosticsFor(_backend, snapshot, handle), d => ParserSyntaxDiagnostic.IsSyntaxError(d.Code));
    }
}
