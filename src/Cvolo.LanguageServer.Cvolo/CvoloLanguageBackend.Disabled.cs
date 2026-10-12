using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Instrumentation;
using Cvolo.LanguageServer.Core.Logging;

namespace Cvolo.LanguageServer.Cvolo;

internal sealed class CvoloLanguageBackend(IReadOnlyList<string> workspaceFolders, string? fallbackRoot, ICoreLogger? logger = null, IReadOnlyList<string>? libraryPaths = null, ServerMetrics? metrics = null) : ILanguageBackend
{
    private readonly ICoreLogger _logger = logger ?? new NullCoreLogger();

    public IReadOnlyList<string> WorkspaceFolders { get; } = workspaceFolders;

    public string? FallbackRoot { get; } = fallbackRoot;

    public BackendProject? OpenProject(DocumentUri document)
    {
        _logger.Write(CoreLogLevel.Warning, "Cvolo compiler tooling is unavailable; document synchronization is running without a compiler backend.");
        return null;
    }

    public bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle)
    {
        handle = null!;
        return false;
    }

    public BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public (BackendSnapshot snapshot, long proposedGeneration) StageDocumentUpdate(BackendProject project, BackendDocumentHandle handle, string text)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public void CommitDocumentUpdate(BackendProject project, BackendDocumentHandle handle, BackendSnapshot snapshot, long generationCommitted)
    {
    }

    public BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public BackendSnapshot CaptureCurrentSnapshot(BackendProject project)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
    {
        return false;
    }

    public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public BackendCompletionResult GetCompletions(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    /// <summary>
    /// Validates a completion replacement span independently of compiler tooling so
    /// adapter boundary tests remain available when the disabled backend is compiled.
    /// </summary>
    internal static bool IsValidReplacementSpan(int start, int length, int position, int textLength)
    {
        return start >= 0
            && length >= 0
            && start + length <= textLength
            && start <= position
            && position <= start + length;
    }

    public BackendSignatureHelpResult? GetSignatureHelp(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public BackendSymbolInfo? GetSymbolAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public BackendReferenceResult GetReferences(BackendSnapshot snapshot, BackendSymbolHandle symbol, bool includeDeclaration)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public BackendRenamePreparation? PrepareRename(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public BackendRenameResult RenameSymbol(BackendSnapshot snapshot, BackendSymbolHandle symbol, string newName)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }

    public BackendSemanticTokenResult GetSemanticTokens(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        throw new InvalidOperationException("Cvolo compiler tooling is unavailable.");
    }
}