using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>
/// Deterministic fake backend for completion concurrency tests. Completion can be
/// armed to block until released, so stale/cancel races are reproducible without
/// sleeps. Projects are keyed by directory so documents in one directory share a
/// project generation, matching the real adapter's project semantics.
/// </summary>
internal sealed class BlockingBackend : ILanguageBackend
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FakeProject> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim _block = new(initialState: true);
    private readonly ManualResetEventSlim _entered = new(initialState: false);

    /// <summary>When true, a completion call throws an ordinary exception.</summary>
    public bool ThrowOnCompletion { get; set; }

    public int CompletionCalls;

    public IReadOnlyList<BackendCompletionItem> CannedItems { get; set; } =
        [new BackendCompletionItem("canned", "canned", BackendCompletionKind.Local, Detail: null, InsertionPlan: null, ResolveHandle: null, ResolvableFields: BackendCompletionResolvableFields.None)];

    /// <summary>When set, replaces the canned result; used to feed malformed spans.</summary>
    public Func<int, BackendCompletionResult>? CompletionResultFactory { get; set; }

    /// <summary>Arms the next completion call to block until <see cref="Release"/>.</summary>
    public void Arm()
    {
        _entered.Reset();
        _block.Reset();
    }

    /// <summary>Lets a blocked completion call finish.</summary>
    public void Release()
    {
        _block.Set();
    }

    /// <summary>Waits until a completion call has entered the backend.</summary>
    public bool WaitUntilEntered(TimeSpan timeout)
    {
        return _entered.Wait(timeout);
    }

    public BackendProject? OpenProject(DocumentUri document)
    {
        lock (_gate)
        {
            string directory = Path.GetDirectoryName(document.LocalPath) ?? document.LocalPath;
            if (!_projects.TryGetValue(directory, out FakeProject? project))
            {
                project = new FakeProject();
                _projects[directory] = project;
            }

            return project;
        }
    }

    public bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle)
    {
        handle = new FakeHandle(document);
        return true;
    }

    public BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text)
    {
        lock (_gate)
        {
            var fake = (FakeProject)project;
            fake.Generation++;
            fake.Current = new FakeSnapshot(fake.Generation);
            return fake.Current;
        }
    }

    public BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle)
    {
        return UpdateDocument(project, handle, string.Empty);
    }

    public BackendSnapshot CaptureCurrentSnapshot(BackendProject project)
    {
        lock (_gate)
        {
            return ((FakeProject)project).Current;
        }
    }

    public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
    {
        lock (_gate)
        {
            return ((FakeSnapshot)snapshot).Generation == ((FakeProject)project).Generation;
        }
    }

    public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
    {
        return new BackendDiagnosticRun(new Dictionary<DocumentUri, string>(), []);
    }

    public BackendCompletionResult GetCompletions(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        Interlocked.Increment(ref CompletionCalls);
        _entered.Set();
        _block.Wait();

        if (ThrowOnCompletion)
        {
            throw new InvalidOperationException("simulated completion failure");
        }

        return CompletionResultFactory is { } factory
            ? factory(position)
            : new BackendCompletionResult(new TextSpan(position, 0), CannedItems);
    }

    public BackendSignatureHelpResult? GetSignatureHelp(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        Interlocked.Increment(ref SignatureHelpCalls);
        _signatureHelpEntered.Set();
        _signatureHelpBlock.Wait();

        if (ThrowOnSignatureHelp)
        {
            throw new InvalidOperationException("simulated signature help failure");
        }

        return SignatureHelpResultFactory is { } factory ? factory() : CannedSignatureHelp;
    }

    public BackendCompletionResolvedInfo? ResolveCompletion(BackendSnapshot snapshot, BackendCompletionResolveHandle handle)
    {
        Interlocked.Increment(ref ResolveCalls);
        _resolveEntered.Set();
        _resolveBlock.Wait();

        if (ThrowOnResolve)
        {
            throw new InvalidOperationException("simulated resolve failure");
        }

        return CannedResolvedInfo;
    }

    /// <summary>Mints a distinct resolve handle for canned completion items.</summary>
    public BackendCompletionResolveHandle CreateResolveHandle()
    {
        return new FakeResolveHandle(Interlocked.Increment(ref _resolveHandleSeed));
    }

    private readonly ManualResetEventSlim _signatureHelpBlock = new(initialState: true);
    private readonly ManualResetEventSlim _signatureHelpEntered = new(initialState: false);

    public int SignatureHelpCalls;

    /// <summary>The signature help result returned by <see cref="GetSignatureHelp"/>.</summary>
    public BackendSignatureHelpResult? CannedSignatureHelp { get; set; }

    /// <summary>When set, replaces the canned signature help result.</summary>
    public Func<BackendSignatureHelpResult?>? SignatureHelpResultFactory { get; set; }

    /// <summary>When true, a signature help call throws an ordinary exception.</summary>
    public bool ThrowOnSignatureHelp { get; set; }

    /// <summary>Arms the next signature help call to block until <see cref="ReleaseSignatureHelp"/>.</summary>
    public void ArmSignatureHelp()
    {
        _signatureHelpEntered.Reset();
        _signatureHelpBlock.Reset();
    }

    /// <summary>Lets a blocked signature help call finish.</summary>
    public void ReleaseSignatureHelp()
    {
        _signatureHelpBlock.Set();
    }

    /// <summary>Waits until a signature help call has entered the backend.</summary>
    public bool WaitUntilSignatureHelpEntered(TimeSpan timeout)
    {
        return _signatureHelpEntered.Wait(timeout);
    }

    private int _resolveHandleSeed;
    private readonly ManualResetEventSlim _resolveBlock = new(initialState: true);
    private readonly ManualResetEventSlim _resolveEntered = new(initialState: false);

    public int ResolveCalls;

    /// <summary>The resolved fields returned by <see cref="ResolveCompletion"/>.</summary>
    public BackendCompletionResolvedInfo? CannedResolvedInfo { get; set; }

    /// <summary>When true, a resolve call throws an ordinary exception.</summary>
    public bool ThrowOnResolve { get; set; }

    /// <summary>Arms the next resolve call to block until <see cref="ReleaseResolve"/>.</summary>
    public void ArmResolve()
    {
        _resolveEntered.Reset();
        _resolveBlock.Reset();
    }

    /// <summary>Lets a blocked resolve call finish.</summary>
    public void ReleaseResolve()
    {
        _resolveBlock.Set();
    }

    /// <summary>Waits until a resolve call has entered the backend.</summary>
    public bool WaitUntilResolveEntered(TimeSpan timeout)
    {
        return _resolveEntered.Wait(timeout);
    }

    private readonly ManualResetEventSlim _navigationBlock = new(initialState: true);
    private readonly ManualResetEventSlim _navigationEntered = new(initialState: false);

    /// <summary>When true, a navigation call throws an ordinary exception.</summary>
    public bool ThrowOnNavigation { get; set; }

    /// <summary>The symbol returned by <see cref="GetSymbolAtPosition"/> (null means "no symbol").</summary>
    public BackendSymbolInfo? CannedSymbol { get; set; }

    /// <summary>The definitions returned by <see cref="GetDefinitions"/>.</summary>
    public BackendDefinitionResult CannedDefinitions { get; set; } =
        new(new Dictionary<DocumentUri, string>(), []);

    /// <summary>The type definitions returned by <see cref="GetTypeDefinitions"/> (null means "none").</summary>
    public BackendDefinitionResult? CannedTypeDefinitions { get; set; }

    /// <summary>Number of times <see cref="GetTypeDefinitions"/> was called.</summary>
    public int TypeDefinitionCalls => _typeDefinitionCalls;

    private int _typeDefinitionCalls;

    /// <summary>The outline returned by <see cref="GetDocumentSymbols"/>.</summary>
    public IReadOnlyList<BackendDocumentSymbol> CannedSymbols { get; set; } = [];

    /// <summary>Arms the next navigation call to block until <see cref="ReleaseNavigation"/>.</summary>
    public void ArmNavigation()
    {
        _navigationEntered.Reset();
        _navigationBlock.Reset();
    }

    /// <summary>Lets a blocked navigation call finish.</summary>
    public void ReleaseNavigation()
    {
        _navigationBlock.Set();
    }

    /// <summary>Waits until a navigation call has entered the backend.</summary>
    public bool WaitUntilNavigationEntered(TimeSpan timeout)
    {
        return _navigationEntered.Wait(timeout);
    }

    public BackendSymbolInfo? GetSymbolAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        EnterNavigation();
        return CannedSymbol;
    }

    public BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol)
    {
        EnterNavigation();
        return CannedDefinitions;
    }

    public BackendDefinitionResult? GetTypeDefinitions(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        Interlocked.Increment(ref _typeDefinitionCalls);
        EnterNavigation();
        return CannedTypeDefinitions;
    }

    /// <summary>The implementations returned by <see cref="GetImplementations"/> (null means "none").</summary>
    public BackendDefinitionResult? CannedImplementations { get; set; }

    /// <summary>Number of times <see cref="GetImplementations"/> was called.</summary>
    public int ImplementationCalls => _implementationCalls;

    private int _implementationCalls;

    public BackendDefinitionResult? GetImplementations(BackendSnapshot snapshot, BackendSymbolHandle symbol)
    {
        Interlocked.Increment(ref _implementationCalls);
        EnterNavigation();
        return CannedImplementations;
    }

    /// <summary>The hierarchy result returned by <see cref="PrepareTypeHierarchy"/> (null means "none").</summary>
    public BackendTypeHierarchyResult? CannedPreparedHierarchy { get; set; }

    /// <summary>Number of times <see cref="PrepareTypeHierarchy"/> was called.</summary>
    public int PrepareTypeHierarchyCalls => _prepareTypeHierarchyCalls;

    private int _prepareTypeHierarchyCalls;

    public BackendTypeHierarchyResult? PrepareTypeHierarchy(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        Interlocked.Increment(ref _prepareTypeHierarchyCalls);
        EnterNavigation();
        return CannedPreparedHierarchy;
    }

    /// <summary>The supertypes returned by <see cref="GetSupertypes"/>.</summary>
    public BackendTypeHierarchyResult CannedSupertypes { get; set; } = BackendTypeHierarchyResult.Empty;

    /// <summary>The subtypes returned by <see cref="GetSubtypes"/>.</summary>
    public BackendTypeHierarchyResult CannedSubtypes { get; set; } = BackendTypeHierarchyResult.Empty;

    /// <summary>The re-resolution key of the last hierarchy follow-up the server asked for.</summary>
    public string? LastHierarchyKey { get; private set; }

    public BackendTypeHierarchyResult GetSupertypes(BackendSnapshot snapshot, DocumentUri document, string key)
    {
        LastHierarchyKey = key;
        EnterNavigation();
        return CannedSupertypes;
    }

    public BackendTypeHierarchyResult GetSubtypes(BackendSnapshot snapshot, DocumentUri document, string key)
    {
        LastHierarchyKey = key;
        EnterNavigation();
        return CannedSubtypes;
    }

    public IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        EnterNavigation();
        return CannedSymbols;
    }

    public BackendSemanticTokenResult GetSemanticTokens(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        Interlocked.Increment(ref _semanticTokensCalls);
        EnterNavigation();
        return CannedSemanticTokens;
    }

    /// <summary>The semantic-token result returned by <see cref="GetSemanticTokens"/>.</summary>
    public BackendSemanticTokenResult CannedSemanticTokens { get; set; } = new([]);

    /// <summary>Number of times <see cref="GetSemanticTokens"/> was called.</summary>
    public int SemanticTokensCalls => _semanticTokensCalls;

    private int _semanticTokensCalls;

    private void EnterNavigation()
    {
        _navigationEntered.Set();
        _navigationBlock.Wait();

        if (ThrowOnNavigation)
        {
            throw new InvalidOperationException("simulated navigation failure");
        }
    }

    private int _codeFixHandleSeed;

    public int CodeFixCalls;

    public int ResolveCodeFixCalls;

    public BackendCodeFixResult CannedCodeFixes { get; set; } = new([]);

    public BackendCodeFixResolution CannedCodeFixResolution { get; set; } =
        new BackendCodeFixFailure("no code fix configured");

    public bool ThrowOnCodeFixes { get; set; }

    public bool ThrowOnResolveCodeFix { get; set; }

    public Func<BackendCodeFixHandle, BackendCodeFixResolution>? CodeFixResolveFactory { get; set; }

    public BackendCodeFixHandle CreateCodeFixHandle()
    {
        return new FakeCodeFixHandle(Interlocked.Increment(ref _codeFixHandleSeed));
    }

    public BackendCodeFixResult GetCodeFixes(BackendSnapshot snapshot, BackendDocumentHandle document, TextSpan range)
    {
        Interlocked.Increment(ref CodeFixCalls);

        if (ThrowOnCodeFixes)
        {
            throw new InvalidOperationException("simulated code action failure");
        }

        return CannedCodeFixes;
    }

    public BackendCodeFixResolution ResolveCodeFix(BackendSnapshot snapshot, BackendCodeFixHandle fix)
    {
        Interlocked.Increment(ref ResolveCodeFixCalls);

        if (ThrowOnResolveCodeFix)
        {
            throw new InvalidOperationException("simulated code fix resolve failure");
        }

        return CodeFixResolveFactory is { } factory ? factory(fix) : CannedCodeFixResolution;
    }

    private readonly ManualResetEventSlim _editorBlock = new(initialState: true);
    private readonly ManualResetEventSlim _editorEntered = new(initialState: false);

    /// <summary>When true, every editor-intelligence call throws an ordinary exception.</summary>
    public bool ThrowOnEditorIntelligence { get; set; }

    public int CodeLensCalls;

    public int InlayHintCalls;

    public int DocumentHighlightCalls;

    public int FoldingRangeCalls;

    public int SelectionRangeCalls;

    public int TypeLayoutCalls;

    /// <summary>The lenses returned by <see cref="GetCodeLenses"/>.</summary>
    public IReadOnlyList<BackendCodeLensInfo> CannedCodeLenses { get; set; } = [];

    /// <summary>When set, replaces the canned lenses; used to feed malformed spans.</summary>
    public Func<IReadOnlyList<BackendCodeLensInfo>>? CodeLensFactory { get; set; }

    /// <summary>The options the last <see cref="GetCodeLenses"/> call received.</summary>
    public BackendCodeLensOptions? LastCodeLensOptions { get; private set; }

    /// <summary>The hints returned by <see cref="GetInlayHints"/>.</summary>
    public IReadOnlyList<BackendInlayHint> CannedInlayHints { get; set; } = [];

    /// <summary>When set, replaces the canned hints; used to feed invalid positions.</summary>
    public Func<IReadOnlyList<BackendInlayHint>>? InlayHintFactory { get; set; }

    /// <summary>The range and options the last <see cref="GetInlayHints"/> call received.</summary>
    public (TextSpan Range, BackendInlayHintOptions? Options) LastInlayHintRequest { get; private set; }

    /// <summary>The highlights returned by <see cref="GetDocumentHighlights"/>.</summary>
    public IReadOnlyList<BackendDocumentHighlight> CannedHighlights { get; set; } = [];

    /// <summary>When set, replaces the canned highlights; used to feed malformed spans.</summary>
    public Func<IReadOnlyList<BackendDocumentHighlight>>? HighlightFactory { get; set; }

    /// <summary>The folding ranges returned by <see cref="GetFoldingRanges"/>.</summary>
    public IReadOnlyList<BackendFoldingRange> CannedFoldingRanges { get; set; } = [];

    /// <summary>When set, replaces the canned folding ranges; used to feed malformed spans.</summary>
    public Func<IReadOnlyList<BackendFoldingRange>>? FoldingRangeFactory { get; set; }

    /// <summary>The selection chains returned by <see cref="GetSelectionRanges"/>.</summary>
    public IReadOnlyList<BackendSelectionRange?> CannedSelectionRanges { get; set; } = [];

    /// <summary>When set, replaces the canned selection chains; used to feed malformed chains.</summary>
    public Func<IReadOnlyList<BackendSelectionRange?>>? SelectionRangeFactory { get; set; }

    /// <summary>The positions the last <see cref="GetSelectionRanges"/> call received.</summary>
    public IReadOnlyList<int> LastSelectionPositions { get; private set; } = [];

    /// <summary>The layout returned by <see cref="GetTypeLayoutAtPosition"/>.</summary>
    public BackendTypeLayoutInspection? CannedTypeLayout { get; set; }

    /// <summary>When set, replaces the canned layout; used to feed malformed layouts.</summary>
    public Func<BackendTypeLayoutInspection?>? TypeLayoutFactory { get; set; }

    /// <summary>The layout returned by <see cref="GetTypeLayoutBySubject"/>; null means unavailable.</summary>
    public BackendTypeLayoutInspection? CannedSubjectLayout { get; set; }

    /// <summary>When set, replaces the canned subject layout.</summary>
    public Func<BackendTypeLayoutInspection?>? SubjectLayoutFactory { get; set; }

    /// <summary>The subject the last <see cref="GetTypeLayoutBySubject"/> call received.</summary>
    public string? LastTypeLayoutSubject { get; private set; }

    /// <summary>Arms the next editor-intelligence call to block until <see cref="ReleaseEditorIntelligence"/>.</summary>
    public void ArmEditorIntelligence()
    {
        _editorEntered.Reset();
        _editorBlock.Reset();
    }

    /// <summary>Lets a blocked editor-intelligence call finish.</summary>
    public void ReleaseEditorIntelligence()
    {
        _editorBlock.Set();
    }

    /// <summary>Waits until an editor-intelligence call has entered the backend.</summary>
    public bool WaitUntilEditorIntelligenceEntered(TimeSpan timeout)
    {
        return _editorEntered.Wait(timeout);
    }

    private void EnterEditorIntelligence()
    {
        _editorEntered.Set();
        _editorBlock.Wait();

        if (ThrowOnEditorIntelligence)
        {
            throw new InvalidOperationException("simulated editor intelligence failure");
        }
    }

    public IReadOnlyList<BackendCodeLensInfo> GetCodeLenses(BackendSnapshot snapshot, BackendDocumentHandle document, BackendCodeLensOptions? options = null)
    {
        Interlocked.Increment(ref CodeLensCalls);
        LastCodeLensOptions = options;
        EnterEditorIntelligence();
        return CodeLensFactory is { } factory ? factory() : CannedCodeLenses;
    }

    public IReadOnlyList<BackendInlayHint> GetInlayHints(BackendSnapshot snapshot, BackendDocumentHandle document, TextSpan requestedRange, BackendInlayHintOptions? options = null)
    {
        Interlocked.Increment(ref InlayHintCalls);
        LastInlayHintRequest = (requestedRange, options);
        EnterEditorIntelligence();
        return InlayHintFactory is { } factory ? factory() : CannedInlayHints;
    }

    public IReadOnlyList<BackendDocumentHighlight> GetDocumentHighlights(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        Interlocked.Increment(ref DocumentHighlightCalls);
        EnterEditorIntelligence();
        return HighlightFactory is { } factory ? factory() : CannedHighlights;
    }

    public IReadOnlyList<BackendFoldingRange> GetFoldingRanges(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        Interlocked.Increment(ref FoldingRangeCalls);
        EnterEditorIntelligence();
        return FoldingRangeFactory is { } factory ? factory() : CannedFoldingRanges;
    }

    public IReadOnlyList<BackendSelectionRange?> GetSelectionRanges(BackendSnapshot snapshot, BackendDocumentHandle document, IReadOnlyList<int> positions)
    {
        Interlocked.Increment(ref SelectionRangeCalls);
        LastSelectionPositions = positions;
        EnterEditorIntelligence();
        return SelectionRangeFactory is { } factory ? factory() : CannedSelectionRanges;
    }

    public BackendTypeLayoutInspection? GetTypeLayoutAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        Interlocked.Increment(ref TypeLayoutCalls);
        EnterEditorIntelligence();
        return TypeLayoutFactory is { } factory ? factory() : CannedTypeLayout;
    }

    public BackendTypeLayoutInspection? GetTypeLayoutBySubject(BackendSnapshot snapshot, BackendDocumentHandle document, string subject)
    {
        Interlocked.Increment(ref TypeLayoutCalls);
        LastTypeLayoutSubject = subject;
        EnterEditorIntelligence();
        return SubjectLayoutFactory is { } factory ? factory() : CannedSubjectLayout;
    }

    private sealed class FakeProject : BackendProject
    {
        public long Generation;

        public FakeSnapshot Current = new(0);
    }

    private sealed class FakeSnapshot(long generation) : BackendSnapshot
    {
        public long Generation { get; } = generation;
    }

    private sealed class FakeHandle(DocumentUri uri) : BackendDocumentHandle
    {
        public DocumentUri Uri { get; } = uri;
    }

    private sealed class FakeResolveHandle(int seed) : BackendCompletionResolveHandle
    {
        public int Seed { get; } = seed;
    }

    private sealed class FakeCodeFixHandle(int seed) : BackendCodeFixHandle
    {
        public int Seed { get; } = seed;
    }
}
