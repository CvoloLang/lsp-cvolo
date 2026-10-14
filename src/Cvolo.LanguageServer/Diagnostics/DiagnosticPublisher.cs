using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Converts backend-neutral diagnostics into wire diagnostics and publishes
/// them through the <see cref="DiagnosticSink"/>. One <see cref="LineIndex"/> is
/// built per captured document text and reused for every primary and related
/// span mapped against that document.
/// </summary>
internal sealed class DiagnosticPublisher(ILspLogger logger, DiagnosticSink sink) : IDiagnosticPublisher
{
    private const string SourceName = "cvolo";

    public void Publish(DiagnosticRunContext context, BackendDiagnosticRun run)
    {
        IReadOnlyList<PublishDiagnosticsPayload> payloads = BuildPayloads(context, run);
        var total = 0;
        foreach (PublishDiagnosticsPayload payload in payloads)
        {
            total += payload.Diagnostics.Length;
        }

        logger.Info($"[diag] publishing {total} diagnostic(s) across {payloads.Count} document(s).");

        foreach (PublishDiagnosticsPayload payload in payloads)
        {
            sink.Publish(payload);
        }
    }

    /// <summary>
    /// Pure mapping step: converts a backend diagnostic run into wire payloads
    /// without touching the transport, so mapping rules can be unit-tested.
    /// </summary>
    public IReadOnlyList<PublishDiagnosticsPayload> BuildPayloads(DiagnosticRunContext context, BackendDiagnosticRun run)
    {
        var indexes = new Dictionary<DocumentUri, LineIndex>(DocumentUriPathComparer.Instance);
        foreach (var pair in run.DocumentTexts)
        {
            indexes[pair.Key] = new LineIndex(pair.Value);
        }

        var byDocument = new Dictionary<DocumentUri, List<DiagnosticPayload>>(DocumentUriPathComparer.Instance);
        foreach (BackendDiagnostic diagnostic in run.Diagnostics)
        {
            if (!TryMap(diagnostic, indexes, out DiagnosticPayload? mapped))
            {
                logger.Debug($"Skipping diagnostic '{diagnostic.Code}' with an out-of-range span.");
                continue;
            }

            if (!byDocument.TryGetValue(diagnostic.Location.Document, out List<DiagnosticPayload>? list))
            {
                list = new List<DiagnosticPayload>();
                byDocument[diagnostic.Location.Document] = list;
            }

            list.Add(mapped);
        }

        var payloads = new List<PublishDiagnosticsPayload>(context.Targets.Count);
        foreach (DiagnosticPublishTarget target in context.Targets)
        {
            byDocument.TryGetValue(target.Uri, out List<DiagnosticPayload>? list);
            payloads.Add(new PublishDiagnosticsPayload(
                ToUri(target.Uri),
                sink.VersionSupported ? target.Version.Value : null,
                list?.ToArray() ?? Array.Empty<DiagnosticPayload>()));
        }

        return payloads;
    }

    public void PublishEmpty(IReadOnlyList<DiagnosticPublishTarget> targets)
    {
        foreach (DiagnosticPublishTarget target in targets)
        {
            PublishEmpty(target.Uri);
        }
    }

    public void PublishEmpty(DocumentUri uri)
    {
        sink.Publish(new PublishDiagnosticsPayload(ToUri(uri), null, Array.Empty<DiagnosticPayload>()));
    }

    public void PublishUnavailable(IReadOnlyList<DiagnosticPublishTarget> targets)
    {
        logger.Warning(
            $"[diag] analysis unavailable for {targets.Count} document(s); clearing marks as an explicit unavailability signal, not a clean result.");
        foreach (DiagnosticPublishTarget target in targets)
        {
            // Clear stale marks, but the caller records unavailability separately so this
            // empty publication is never interpreted as "validated with zero diagnostics".
            sink.Publish(new PublishDiagnosticsPayload(ToUri(target.Uri), null, Array.Empty<DiagnosticPayload>()));
        }
    }

    private bool TryMap(BackendDiagnostic diagnostic, IReadOnlyDictionary<DocumentUri, LineIndex> indexes, out DiagnosticPayload mapped)
    {
        if (!indexes.TryGetValue(diagnostic.Location.Document, out LineIndex? index)
            || !index.TryGetRange(diagnostic.Location.Span, out TextRange range))
        {
            mapped = null!;
            return false;
        }

        DiagnosticRelatedInformationPayload[]? related = null;
        if (sink.RelatedInformationSupported && diagnostic.RelatedLocations.Count > 0)
        {
            var items = new List<DiagnosticRelatedInformationPayload>(diagnostic.RelatedLocations.Count);
            foreach (BackendDiagnosticLocation location in diagnostic.RelatedLocations)
            {
                if (!indexes.TryGetValue(location.Document, out LineIndex? relatedIndex)
                    || !relatedIndex.TryGetRange(location.Span, out TextRange relatedRange))
                {
                    logger.Debug($"Skipping related location with an unresolvable range in '{location.Document}'.");
                    continue;
                }

                items.Add(new DiagnosticRelatedInformationPayload(
                    new Location
                    {
                        Uri = ToUri(location.Document),
                        Range = ToLspRange(relatedRange),
                    },
                    location.Message ?? string.Empty));
            }

            if (items.Count > 0)
            {
                related = items.ToArray();
            }
        }

        mapped = new DiagnosticPayload(
            ToLspRange(range),
            MapSeverity(diagnostic.Severity),
            diagnostic.Code,
            SourceName,
            diagnostic.Message,
            related);
        return true;
    }

    private static Uri ToUri(DocumentUri uri)
    {
        return new Uri(uri.LocalPath);
    }

    private static LspRange ToLspRange(TextRange range)
    {
        return new LspRange
        {
            Start = new Position(range.Start.Line, range.Start.Character),
            End = new Position(range.End.Line, range.End.Character),
        };
    }

    private static DiagnosticSeverity MapSeverity(BackendDiagnosticSeverity severity)
    {
        return severity switch
        {
            BackendDiagnosticSeverity.Error => DiagnosticSeverity.Error,
            BackendDiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
            BackendDiagnosticSeverity.Info => DiagnosticSeverity.Information,
            BackendDiagnosticSeverity.Hint => DiagnosticSeverity.Hint,
            _ => DiagnosticSeverity.Warning,
        };
    }
}
