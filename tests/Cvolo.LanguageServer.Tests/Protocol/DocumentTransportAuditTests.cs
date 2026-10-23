using Cvolo.LanguageServer.Observability;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Read/handler count comparisons must never imply concrete frame identity or
/// dropped frames, even when observed totals coincide.
/// </summary>
public sealed class DocumentTransportAuditTests
{
    [Fact]
    public void Identical_counts_do_not_prove_correlated_frames()
    {
        var audit = new DocumentTransportAudit();
        audit.OnTransportRead("textDocument/didOpen");
        audit.OnHandlerEntered("did_open");
        audit.OnHandlerSettled("did_open", "applied");
        var report = audit.Inspect();
        Assert.True(report.CountersConcordantAtObservation);
        Assert.Equal(1, report.TransportReads);
        Assert.Equal(1, report.HandlerEntries);
        Assert.Equal(1, report.HandlerSettlements);
        Assert.False(report.IndividualFrameIdentityProven);
        Assert.False(report.ClosedTransportProducerProven);
        Assert.False(report.DocumentReplayCompleteProven);
    }

    [Fact]
    public void Unsettled_or_undispatched_frame_is_not_declared_lost()
    {
        var audit = new DocumentTransportAudit();
        audit.OnTransportRead("textDocument/didChange");
        var report = audit.Inspect();
        Assert.False(report.CountersConcordantAtObservation);
        Assert.Equal(1, report.Lanes[1].ReadsWithoutObservedEntry);
        Assert.Equal("unclassified_in_flight_or_unobserved", report.GapInterpretation);
        Assert.False(report.UnmatchedCountersProveDroppedFrames);
        audit.OnHandlerEntered("did_change");
        Assert.Equal(1, audit.Inspect().Lanes[1].EntriesWithoutObservedSettlement);
        audit.OnHandlerSettled("did_change", "rejected");
        Assert.True(audit.Inspect().CountersConcordantAtObservation);
    }

    [Fact]
    public void Direct_handler_invocation_is_not_reported_as_dropped_wire_frame()
    {
        var audit = new DocumentTransportAudit();
        audit.OnHandlerEntered("did_close");
        audit.OnHandlerSettled("did_close", "rejected");
        var report = audit.Inspect();
        Assert.Equal(1, report.Lanes[2].EntriesWithoutObservedRead);
        Assert.False(report.UnmatchedCountersProveDroppedFrames);
        Assert.False(report.IndividualFrameIdentityProven);
    }

    [Fact]
    public void Invalid_method_names_and_outcomes_never_enter_audit()
    {
        var audit = new DocumentTransportAudit();
        audit.OnTransportRead("file:///secret.cvl");
        audit.OnHandlerEntered("SECRET_SOURCE");
        audit.OnHandlerSettled("did_open", "SECRET_EXCEPTION");
        Assert.Equal(0, audit.Inspect().TransportReads);
        Assert.Equal(0, audit.Inspect().HandlerEntries);
        Assert.Equal(0, audit.Inspect().HandlerSettlements);
    }

    [Fact]
    public void Bounded_counters_saturate_with_explicit_incomplete_flag()
    {
        var audit = new DocumentTransportAudit(maxObservationsPerLane: 2);
        for (int i = 0; i < 5; i++)
        {
            audit.OnTransportRead("textDocument/didOpen");
            audit.OnHandlerEntered("did_open");
            audit.OnHandlerSettled("did_open", "applied");
        }
        var report = audit.Inspect();
        Assert.Equal(2, report.TransportReads);
        Assert.Equal(2, report.HandlerEntries);
        Assert.Equal(2, report.HandlerSettlements);
        Assert.True(report.CounterOverflowed);
        Assert.False(report.CountersConcordantAtObservation);
        Assert.False(report.DocumentReplayCompleteProven);
    }

    [Fact]
    public void Parallel_notifications_preserve_bounded_process_counts()
    {
        var audit = new DocumentTransportAudit();
        Parallel.For(0, 250, _ =>
        {
            audit.OnTransportRead("textDocument/didChange");
            audit.OnHandlerEntered("did_change");
            audit.OnHandlerSettled("did_change", "applied");
        });
        var report = audit.Inspect();
        Assert.True(report.CountersConcordantAtObservation);
        Assert.Equal(250, report.Lanes[1].TransportReads);
        Assert.Equal(250, report.Lanes[1].HandlerEntries);
        Assert.Equal(250, report.Lanes[1].HandlerSettlements);
        Assert.False(report.IndividualFrameIdentityProven);
    }
}
