using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using global::Cvolo.Observability.Abstractions;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Converts backend-neutral diagnostics into wire diagnostics and publishes
/// them through the <see cref="DiagnosticSink"/>. One <see cref="LineIndex"/> is
/// built per captured document text and reused for every primary and related
/// span mapped against that document.
/// </summary>
internal sealed class DiagnosticPublisher(
    ILspLogger logger,
    DiagnosticSink sink,
    ICompilerObserver? observer = null) : IDiagnosticPublisher
{
    private const string SourceName = "cvolo";
    private const string PublishAttemptEvent = "diagnostics.publish_attempt";
    private const string PublishSentEvent = "diagnostics.publish_sent";
    private const string PublishFaultedEvent = "diagnostics.publish_faulted";
    private const string DiagnosticsDiscardedEvent = "diagnostics.discarded";
    private const string ClearSentEvent = "diagnostics.clear_sent";
    private const string PublishDecisionEvent = "decision.diagnostics.publish";

    private readonly ICompilerObserver _observer = observer ?? NoopCompilerObserver.Instance;

    /// <summary>
    /// Records the actual publication boundary. No source text, document URI, path,
    /// diagnostic message or exception message is stored in the telemetry event.
    /// </summary>
    private void Emit(string eventCode, string outcome, string? reason = null,
        IReadOnlyList<KeyValuePair<string, string>>? attributes = null)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0))
            return;

        _observer.Emit(new CompilerEvent
        {
            Event = eventCode,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            Outcome = outcome,
            Reason = reason,
            Attributes = attributes,
        });
    }

    private void EmitDecision(string outcome, string? reason = null)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Decision, DiagnosticDetail.L0))
            return;

        _observer.Emit(new CompilerEvent
        {
            Event = PublishDecisionEvent,
            Kind = CompilerEventKind.Decision,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            Outcome = outcome,
            Reason = reason,
        });
    }

    public void Publish(DiagnosticRunContext context, BackendDiagnosticRun run)
    {
        IReadOnlyList<PublishDiagnosticsPayload> payloads = BuildPayloads(context, run);
        var total = 0;
        foreach (PublishDiagnosticsPayload payload in payloads)
        {
            total += payload.Diagnostics.Length;
        }

        logger.Info($"[diag] publishing {total} diagnostic(s) across {payloads.Count} document(s).");
        EmitDecision("publish");

        // A successful enqueue is not a successful JSON-RPC notification. Each payload
        // reports its own terminal result from the per-URI transport lane.
        foreach (PublishDiagnosticsPayload payload in payloads)
        {
            PublishPayload(payload, clear: payload.Diagnostics.Length == 0);
        }
    }

    private void PublishPayload(PublishDiagnosticsPayload payload, bool clear, string? reason = null)
    {
        // The disabled path stays as cheap as ordinary publication: no captured callback,
        // extra telemetry payload, or per-notification diagnostic allocation (§30, §59).
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0))
        {
            sink.Publish(payload);
            return;
        }

        var diagnosticCount = payload.Diagnostics.Length;
        Emit(PublishAttemptEvent, "attempted", reason, PayloadCount(diagnosticCount, 1));
        try
        {
            sink.Publish(payload, result =>
            {
                switch (result.State)
                {
                    case DiagnosticPublicationState.TransportAccepted:
                        // "sent" is a completed JSON-RPC notification task, not a client ACK.
                        Emit(clear ? ClearSentEvent : PublishSentEvent,
                            "sent", reason, PayloadCount(diagnosticCount, 1, result.Sequence));
                        break;

                    case DiagnosticPublicationState.Faulted:
                        Emit(PublishFaultedEvent, "faulted",
                            result.Error?.GetType().Name ?? "transport_fault",
                            PayloadCount(diagnosticCount, 1, result.Sequence));
                        break;

                    case DiagnosticPublicationState.Superseded:
                        Emit(DiagnosticsDiscardedEvent, "discarded", "publication_superseded",
                            PayloadCount(diagnosticCount, 1, result.Sequence));
                        break;

                    case DiagnosticPublicationState.Cancelled:
                        Emit(DiagnosticsDiscardedEvent, "discarded", "publication_cancelled",
                            PayloadCount(diagnosticCount, 1, result.Sequence));
                        break;
                }
            });
        }
        catch (Exception ex)
        {
            Emit(PublishFaultedEvent, "faulted", ex.GetType().Name, PayloadCount(diagnosticCount, 1));
            throw;
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string>> PayloadCount(
        int diagnostics, int documents, long? sequence = null)
    {
        if (sequence is null)
        {
            return
            [
                new("diagnosticCount", diagnostics.ToString()),
                new("documentCount", documents.ToString()),
            ];
        }

        return
        [
            new("diagnosticCount", diagnostics.ToString()),
            new("documentCount", documents.ToString()),
            new("publicationSequence", sequence.Value.ToString()),
        ];
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
        EmitDecision("clear");
        foreach (DiagnosticPublishTarget target in targets)
        {
            PublishPayload(new PublishDiagnosticsPayload(ToUri(target.Uri), null, Array.Empty<DiagnosticPayload>()), clear: true);
        }
    }

    public void PublishEmpty(DocumentUri uri)
    {
        EmitDecision("clear");
        PublishPayload(new PublishDiagnosticsPayload(ToUri(uri), null, Array.Empty<DiagnosticPayload>()), clear: true);
    }

    public void PublishUnavailable(IReadOnlyList<DiagnosticPublishTarget> targets)
    {
        logger.Warning(
            $"[diag] analysis unavailable for {targets.Count} document(s); clearing marks as an explicit unavailability signal, not a clean result.");
        EmitDecision("clear", "analysis_unavailable");
        foreach (DiagnosticPublishTarget target in targets)
        {
            // Clear stale marks, but the caller records unavailability separately so this
            // empty publication is never interpreted as "validated with zero diagnostics".
            PublishPayload(new PublishDiagnosticsPayload(ToUri(target.Uri), null, Array.Empty<DiagnosticPayload>()),
                clear: true, reason: "analysis_unavailable");
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
