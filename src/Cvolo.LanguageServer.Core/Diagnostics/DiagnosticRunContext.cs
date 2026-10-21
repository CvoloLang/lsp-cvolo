using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Core.Diagnostics;

/// <summary>
/// The immutable description of one diagnostic analysis job: the backend
/// project, the single captured project snapshot every diagnostic must derive
/// from, and the open-document publication targets.
/// </summary>
internal sealed record DiagnosticRunContext(
    BackendProject Project,
    BackendSnapshot CurrentProjectSnapshot,
    IReadOnlyList<DiagnosticPublishTarget> Targets)
{
    /// <summary>
    /// Analysis that produced this diagnostic run. Set by the worker after capture
    /// and carried unchanged through the publication callback. Null for callers
    /// outside an observed analysis; never invent a correlation id.
    /// </summary>
    public string? AnalysisId { get; init; }
}

/// <summary>
/// One open document eligible to receive diagnostics, pinned to the session
/// lifetime and version observed when the run was captured.
/// </summary>
internal sealed record DiagnosticPublishTarget(
    DocumentUri Uri,
    DocumentSessionId SessionId,
    DocumentVersion Version,
    BackendDocumentHandle Document);
