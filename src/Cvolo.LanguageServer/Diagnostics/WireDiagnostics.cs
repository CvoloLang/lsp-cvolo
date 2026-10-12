using Microsoft.VisualStudio.LanguageServer.Protocol;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Server-side wire shape for <c>textDocument/publishDiagnostics</c>. The pinned
/// protocol library's <c>Diagnostic</c> predates <c>relatedInformation</c>, so
/// the server serializes its own superset payload. <see cref="Version"/> is the
/// optional LSP 3.16+ document version this set was computed against; it is
/// omitted (null value handling ignores it) when the client did not advertise
/// <c>versionSupport</c> or no version is known.
/// </summary>
internal sealed record PublishDiagnosticsPayload(Uri Uri, int? Version, DiagnosticPayload[] Diagnostics);

/// <summary>
/// LSP 3.17 diagnostic payload with related information support.
/// </summary>
internal sealed record DiagnosticPayload(
    LspRange Range,
    DiagnosticSeverity Severity,
    string? Code,
    string? Source,
    string? Message,
    DiagnosticRelatedInformationPayload[]? RelatedInformation);

/// <summary>
/// LSP 3.17 <c>DiagnosticRelatedInformation</c>.
/// </summary>
internal sealed record DiagnosticRelatedInformationPayload(Location Location, string Message);
