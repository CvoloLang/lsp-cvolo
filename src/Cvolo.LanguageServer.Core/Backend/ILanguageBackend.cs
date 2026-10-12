using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Backend;

/// <summary>
/// Opaque handle to a language backend's open project session. Never
/// inspected by the core; only passed back to the backend that minted it.
/// </summary>
internal abstract class BackendProject
{
}

/// <summary>
/// Opaque immutable view of the backend's project state. Snapshots are
/// advanced with <see cref="ILanguageBackend.UpdateDocument"/> and never
/// mutated in place.
/// </summary>
internal abstract class BackendSnapshot
{
}

/// <summary>
/// Opaque handle to one document within a <see cref="BackendProject"/>.
/// </summary>
internal abstract class BackendDocumentHandle
{
}

/// <summary>
/// Boundary the core calls to keep compiler-side state coherent with the
/// editor text it manages. Implemented by a language-specific adapter
/// (CvoloLanguageServer.Cvolo) that owns project discovery and the
/// per-project serialization of snapshot advancement.
/// </summary>
internal interface ILanguageBackend
{
    /// <summary>
    /// Opens, or reuses, the project hosting <paramref name="document"/>, or
    /// returns null when the document cannot be hosted (no project found,
    /// ambiguous project, or resolution failure). Discovery failures must be
    /// non-fatal: they only suppress state publication.
    /// </summary>
    BackendProject? OpenProject(DocumentUri document);
    /// <summary>
    /// Resolves <paramref name="document"/> inside the project.
    /// </summary>
    bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle);
    /// <summary>
    /// Advances <paramref name="project"/> so <paramref name="document"/>'s
    /// text is <paramref name="text"/> and returns the new snapshot. The
    /// returned snapshot must be coherent with the new text.
    /// </summary>
    BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text);
    /// <summary>
    /// Removes <paramref name="document"/>'s overlay, restoring the project
    /// baseline text, and returns the resulting snapshot.
    /// </summary>
    BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle);
    /// <summary>
    /// Captures the project's current immutable snapshot together with its
    /// adapter-owned generation. Used for semantic work that must be pinned to
    /// one project state.
    /// </summary>
    BackendSnapshot CaptureCurrentSnapshot(BackendProject project);
    /// <summary>
    /// Refreshes closed project documents from disk while preserving editor-owned overlays for
    /// <paramref name="openDocuments"/>, then returns the coherent current snapshot. This keeps
    /// semantic state synchronized after a client applies a workspace edit to closed files.
    /// Backends without disk-backed project documents may use the default snapshot capture.
    /// </summary>
    BackendSnapshot SynchronizeClosedDocuments(BackendProject project, IReadOnlyList<BackendDocumentHandle> openDocuments)
        => CaptureCurrentSnapshot(project);
    /// <summary>
    /// Reports whether <paramref name="snapshot"/> is still the project's
    /// current snapshot. Implementations compare an adapter-owned generation,
    /// not object identity.
    /// </summary>
    bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot);
    /// <summary>
    /// Reports whether the project's current generation has completed a full
    /// analysis (migration stage S4). A backend that does not run background
    /// analysis reports every generation ready. Interactive requests may still
    /// compute a result when this returns false — the flag only records that the
    /// generation's one shared analysis has not finished warming up yet.
    /// </summary>
    bool IsAnalysisReady(BackendProject project) => true;
    /// <summary>
    /// Computes diagnostics for <paramref name="targets"/> from
    /// <paramref name="snapshot"/>. Every call must use that same snapshot so
    /// one project analysis backs the whole result.
    /// </summary>
    BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets);

    /// <summary>
    /// Computes backend-neutral completion candidates for
    /// <paramref name="document"/> at <paramref name="position"/> (an absolute
    /// UTF-16 code-unit offset over the captured snapshot text) from the one
    /// coherent <paramref name="snapshot"/>. The returned replacement span must
    /// be a valid span over that same snapshot text that contains
    /// <paramref name="position"/> (§4.5, §7, §17). Implementations must never
    /// degrade to an unfiltered global list when triggered by a bare '.', never
    /// fabricate a replacement span, and never produce stale or cross-generation
    /// results (§4.4, §16.6, §30).
    /// </summary>
    BackendCompletionResult GetCompletions(BackendSnapshot snapshot, BackendDocumentHandle document, int position);

    /// <summary>
    /// Resolves the lazily-requested fields of one completion item against the exact snapshot
    /// its handle was minted from. A handle from a foreign snapshot yields null, never an
    /// unrelated item. Resolve never recomputes or changes the insertion template.
    /// </summary>
    BackendCompletionResolvedInfo? ResolveCompletion(BackendSnapshot snapshot, BackendCompletionResolveHandle handle)
        => null;

    /// <summary>
    /// Computes signature help for the call containing <paramref name="position"/> from the same
    /// coherent snapshot used by the editor request. Returns null when no callable is bound.
    /// </summary>
    BackendSignatureHelpResult? GetSignatureHelp(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        => null;

    /// <summary>
    /// Resolves the backend-neutral symbol at <paramref name="position"/> (an
    /// absolute UTF-16 code-unit offset over the captured snapshot text) from the
    /// one coherent <paramref name="snapshot"/>. Returns null when no trustworthy
    /// symbol is bound there. The returned handle is scoped to
    /// <paramref name="snapshot"/> and must not be used with another snapshot
    /// (§12, §15).
    /// </summary>
    BackendSymbolInfo? GetSymbolAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position);

    /// <summary>
    /// Returns the source declarations of <paramref name="symbol"/> from the same
    /// immutable <paramref name="snapshot"/> the handle was resolved from. A
    /// handle from a foreign snapshot yields an empty result, never an unrelated
    /// symbol. Target document texts are included so closed documents can be
    /// mapped (§17, §18).
    /// </summary>
    BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol);

    /// <summary>Returns project-source occurrences of a snapshot-scoped semantic symbol.</summary>
    BackendReferenceResult GetReferences(BackendSnapshot snapshot, BackendSymbolHandle symbol, bool includeDeclaration)
        => new(new Dictionary<DocumentUri, string>(), Array.Empty<BackendReferenceLocation>());

    /// <summary>Resolves and validates the rename target at an absolute UTF-16 position.</summary>
    BackendRenamePreparation? PrepareRename(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        => null;

    /// <summary>Computes a complete, snapshot-pure semantic rename plan.</summary>
    BackendRenameResult RenameSymbol(BackendSnapshot snapshot, BackendSymbolHandle symbol, string newName)
        => new BackendRenameFailure("Rename is not supported by this language backend.");

    /// <summary>
    /// Returns the declaration outline of <paramref name="document"/> from the
    /// captured <paramref name="snapshot"/> in deterministic source order (§19).
    /// </summary>
    IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(BackendSnapshot snapshot, BackendDocumentHandle document);

    /// <summary>
    /// Returns the semantic tokens of <paramref name="document"/> from the captured
    /// <paramref name="snapshot"/> (§13, §14). Classification is compiler-owned and semantic;
    /// unresolved occurrences are omitted.
    /// </summary>
    BackendSemanticTokenResult GetSemanticTokens(BackendSnapshot snapshot, BackendDocumentHandle document)
        => new(Array.Empty<BackendSemanticToken>());

    /// <summary>
    /// Returns compiler-provided quick fix candidates for <paramref name="document"/> whose
    /// diagnostics intersect <paramref name="range"/> (an absolute UTF-16 code-unit span over the
    /// captured snapshot text), from the same immutable <paramref name="snapshot"/>.
    /// </summary>
    BackendCodeFixResult GetCodeFixes(BackendSnapshot snapshot, BackendDocumentHandle document, TextSpan range)
        => new(Array.Empty<BackendCodeFixInfo>());

    /// <summary>
    /// Resolves the complete, all-or-nothing edit set of a code fix handle against the exact
    /// snapshot it was minted from. A handle from a foreign snapshot yields a failure.
    /// </summary>
    BackendCodeFixResolution ResolveCodeFix(BackendSnapshot snapshot, BackendCodeFixHandle fix)
        => new BackendCodeFixFailure("Code fixes are not supported by this language backend.");

    /// <summary>
    /// Returns the whole-document batch of editor annotations for <paramref name="document"/> from
    /// the captured <paramref name="snapshot"/>. The backend computes every count, title and
    /// command argument in one pass over its own per-snapshot indexes; the core never re-counts
    /// references or recomputes a layout. A null or default <paramref name="options"/> selects the
    /// recommended set. A document with no annotations yields an empty list.
    /// </summary>
    IReadOnlyList<BackendCodeLensInfo> GetCodeLenses(BackendSnapshot snapshot, BackendDocumentHandle document, BackendCodeLensOptions? options = null)
        => Array.Empty<BackendCodeLensInfo>();

    /// <summary>
    /// Returns the inline annotations whose position falls inside <paramref name="requestedRange"/>
    /// (an absolute UTF-16 code-unit span over the captured snapshot text) from the same immutable
    /// <paramref name="snapshot"/>. Work is restricted to the requested range. Every hint comes
    /// from a resolved compiler fact: a construct the backend could not resolve contributes no
    /// hint rather than a guess (§17, §62). A null or default <paramref name="options"/> selects the
    /// recommended set.
    /// </summary>
    IReadOnlyList<BackendInlayHint> GetInlayHints(BackendSnapshot snapshot, BackendDocumentHandle document, TextSpan requestedRange, BackendInlayHintOptions? options = null)
        => Array.Empty<BackendInlayHint>();

    /// <summary>
    /// Returns the semantic occurrences bound to the same declaration as <paramref name="position"/>
    /// (an absolute UTF-16 code-unit offset), each classified as a read, a write, both, or a
    /// declaration. Returns an empty list when nothing binds there; there is deliberately no
    /// same-spelling fallback (§41).
    /// </summary>
    IReadOnlyList<BackendDocumentHighlight> GetDocumentHighlights(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        => Array.Empty<BackendDocumentHighlight>();

    /// <summary>
    /// Returns the foldable regions of <paramref name="document"/> from the captured
    /// <paramref name="snapshot"/>. Regions come from parsed syntax and comment tokens, so the
    /// protocol layer never scans braces itself (§43, §56).
    /// </summary>
    IReadOnlyList<BackendFoldingRange> GetFoldingRanges(BackendSnapshot snapshot, BackendDocumentHandle document)
        => Array.Empty<BackendFoldingRange>();

    /// <summary>
    /// Returns one smart-selection chain per requested position, in request order. A null entry
    /// means the position has no meaningful enclosing region; the result is aligned with the
    /// request so the protocol layer can answer position-for-position (§45).
    /// </summary>
    IReadOnlyList<BackendSelectionRange?> GetSelectionRanges(BackendSnapshot snapshot, BackendDocumentHandle document, IReadOnlyList<int> positions)
        => positions.Select(static _ => (BackendSelectionRange?)null).ToArray();

    /// <summary>
    /// Returns the complete object layout of the type at <paramref name="position"/> (an absolute
    /// UTF-16 code-unit offset) for the active target, or null when no type declaration binds
    /// there. The numbers are the compiler's own; nothing here is recomputed or ABI-guessed (§21).
    /// </summary>
    BackendTypeLayoutInspection? GetTypeLayoutAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        => null;

    /// <summary>
    /// Returns the complete object layout of the type named by <paramref name="subject"/>, re-resolved
    /// against the current snapshot, or null when the subject no longer names a type the compiler can
    /// lay out. This is the refresh path: a client that stored a subject asks again after the project
    /// changes, and a renamed or removed type becomes unavailable instead of showing stale numbers
    /// (§27, §28, §42).
    /// </summary>
    BackendTypeLayoutInspection? GetTypeLayoutBySubject(BackendSnapshot snapshot, BackendDocumentHandle document, string subject)
        => null;

    /// <summary>
    /// Returns the source declarations of the semantic type of the value or expression at
    /// <paramref name="position"/> (an absolute UTF-16 code-unit offset). This backs
    /// textDocument/typeDefinition, which differs from textDocument/definition: a local resolves to
    /// the type it stores rather than to the local's own declaration. The compiler's own type is the
    /// only source; an unresolved or ambiguous position yields no target rather than a text guess (§2-§5).
    /// </summary>
    BackendDefinitionResult? GetTypeDefinitions(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        => null;

    /// <summary>
    /// Returns the concrete places that satisfy the contract named by <paramref name="symbol"/> from
    /// the same immutable <paramref name="snapshot"/>: the extension blocks that implement an
    /// interface (or one of its members), and the concrete types that conform to a protocol (or
    /// provide one of its members) by the compiler's own conformance rules. This backs
    /// textDocument/implementation; structural protocol conformance is decided by the compiler, never
    /// inferred from method names. A symbol that is not a contract, or a contract with no
    /// implementation, yields no target (§6-§11).
    /// </summary>
    BackendDefinitionResult? GetImplementations(BackendSnapshot snapshot, BackendSymbolHandle symbol)
        => null;

    /// <summary>
    /// Returns the contract at <paramref name="position"/> if it is a declared interface or protocol,
    /// or null otherwise. This backs textDocument/prepareTypeHierarchy; a concrete type is not a
    /// hierarchy item because structural conformance is not declared inheritance (§12, §13).
    /// </summary>
    BackendTypeHierarchyResult? PrepareTypeHierarchy(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
        => null;

    /// <summary>
    /// Returns the contracts that the contract named by <paramref name="key"/> directly inherits.
    /// <paramref name="document"/> anchors the re-resolution. Only declared contract bases are
    /// returned; a concrete conformer is never a supertype or subtype (§13, §14, §15).
    /// </summary>
    BackendTypeHierarchyResult GetSupertypes(BackendSnapshot snapshot, DocumentUri document, string key)
        => BackendTypeHierarchyResult.Empty;

    /// <summary>
    /// Returns the contracts that directly declare the contract named by <paramref name="key"/> as a
    /// base. <paramref name="document"/> anchors the re-resolution. Direct children only; the client
    /// follows further requests to walk the tree (§14).
    /// </summary>
    BackendTypeHierarchyResult GetSubtypes(BackendSnapshot snapshot, DocumentUri document, string key)
        => BackendTypeHierarchyResult.Empty;
}