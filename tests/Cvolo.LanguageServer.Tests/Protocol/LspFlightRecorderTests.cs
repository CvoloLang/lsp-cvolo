using Cvolo.LanguageServer.Observability;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Volatile bounded pretrigger evidence, including privacy and loss contracts.
/// No real workspace sources are used.
/// </summary>
public sealed class LspFlightRecorderTests
{
    [Fact]
    public void Safe_event_vocabulary_drops_arbitrary_method_and_outcome_strings()
    {
        var recorder = new LspFlightRecorder();
        recorder.OnTransportNotification("textDocument/SECRET_PATH.cvl");
        recorder.OnHandlerEntered("private source", 2);
        recorder.OnHandlerSettled("did_change", "SECRET_EXCEPTION_TEXT", 3);
        var empty = recorder.Inspect();
        Assert.Empty(empty.Events);
        Assert.False(empty.SourceContentCaptured);

        recorder.OnTransportNotification("textDocument/didOpen");
        recorder.OnHandlerEntered("did_open", 5);
        recorder.OnHandlerSettled("did_open", "rejected", 5);
        var report = recorder.Inspect();
        Assert.Equal(3, report.RetainedEvents);
        Assert.Equal("did_open", report.Events[0].Kind);
        Assert.Equal("handler_settled", report.Events[2].Stage);
    }

    [Fact]
    public void Failure_automatically_triggers_in_memory_incident()
    {
        var recorder = new LspFlightRecorder();
        recorder.OnTransportNotification("textDocument/didChange");
        recorder.OnHandlerEntered("did_change", 12);
        recorder.OnHandlerSettled("did_change", "faulted", 12);

        var report = recorder.Inspect();
        Assert.Equal("incident_observed_in_memory", report.Status);
        Assert.Equal("document_handler_faulted", report.IncidentReason);
        Assert.Equal(3, report.RetainedPretriggerEvents);
        Assert.True(report.TriggerRetained);
        Assert.False(report.DurableCheckpointAvailable);
    }

    [Fact]
    public void Ring_is_bounded_and_reports_overwritten_prefix()
    {
        var recorder = new LspFlightRecorder();
        for (int i = 0; i < LspFlightRecorder.MaxEvents + 12; i++)
            recorder.OnTransportNotification("textDocument/didChange");

        var report = recorder.Inspect();
        Assert.Equal(LspFlightRecorder.MaxEvents, report.RetainedEvents);
        Assert.Equal(12, report.EvictedEvents);
        Assert.True(report.HistoryPrefixTruncated);
        Assert.False(report.PreProcessHistoryRecoverable);
    }

    [Fact]
    public void Ring_expires_metadata_without_sleeping()
    {
        DateTimeOffset now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var recorder = new LspFlightRecorder(() => now);
        recorder.OnTransportNotification("textDocument/didOpen");
        now += LspFlightRecorder.Retention + TimeSpan.FromSeconds(1);
        Assert.Empty(recorder.Inspect().Events);
        Assert.Equal(1, recorder.Inspect().EvictedEvents);
        Assert.True(recorder.Inspect().HistoryPrefixTruncated);
    }

    [Fact]
    public void Manual_trigger_requires_supported_version_and_category()
    {
        var recorder = new LspFlightRecorder();
        Assert.Equal("server_not_initialized",
            recorder.Trigger(new FlightRecorderTriggerRequest
            {
                ProtocolVersion = 1, Category = "other",
            }, false).Reason);
        Assert.Equal("unsupported_category",
            recorder.Trigger(new FlightRecorderTriggerRequest
            {
                ProtocolVersion = 1, Category = "PRIVATE_NOTE",
            }, true).Reason);
        Assert.Equal("unsupported_protocol_version",
            recorder.Trigger(new FlightRecorderTriggerRequest
            {
                ProtocolVersion = 3, Category = "other",
            }, true).Reason);
        var accepted = recorder.Trigger(new FlightRecorderTriggerRequest
        {
            ProtocolVersion = 1, Category = "definition",
        }, true);
        Assert.Equal("captured_in_memory", accepted.Status);
        Assert.False(accepted.DurablySaved);
        Assert.NotNull(accepted.IncidentId);
    }
}
