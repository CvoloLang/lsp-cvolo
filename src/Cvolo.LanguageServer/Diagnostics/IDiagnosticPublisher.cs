using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Publication seam for diagnostics. The production implementation maps and
/// sends over JSON-RPC; tests substitute a recording implementation.
/// </summary>
internal interface IDiagnosticPublisher
{
    void Publish(DiagnosticRunContext context, BackendDiagnosticRun run);
    void PublishEmpty(IReadOnlyList<DiagnosticPublishTarget> targets);
    void PublishEmpty(DocumentUri uri);

    /// <summary>
    /// Reports that analysis is currently unavailable and clears any stale marks.
    /// This is deliberately distinct from <see cref="PublishEmpty(IReadOnlyList{DiagnosticPublishTarget})"/>:
    /// an empty set is a claim that the current input was validated and has no diagnostics,
    /// whereas an unavailable result means the server could not analyze the input at all.
    /// Host consumers must not confuse the two.
    /// </summary>
    void PublishUnavailable(IReadOnlyList<DiagnosticPublishTarget> targets);
}
