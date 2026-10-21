using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;
using Cvolo.LanguageServer.Tests.TestSupport;
using System.Collections.Concurrent;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

/// <summary>
/// Regression coverage: enqueue is not transport acceptance; superseded and faulted
/// notifications must never be misreported as successfully sent.
/// </summary>
public sealed class DiagnosticPublicationOutcomeTests
{
    private static PublishDiagnosticsPayload Payload(int? version = null)
        => new(new Uri("file:///d:/telemetry.cvl"), version, Array.Empty<DiagnosticPayload>());

    [Fact]
    public async Task Callback_IsNotSuccessfulUntilAsyncTransportCompletes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = new ConcurrentQueue<DiagnosticPublicationResult>();
        var lane = new UriPublicationLane((_, _) =>
        {
            started.TrySetResult();
            return gate.Task;
        });

        long id = lane.Enqueue(Payload(), outcomes.Enqueue);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(outcomes);
        Assert.Equal(0, lane.LastPublishedSequence);

        gate.SetResult();
        await WaitUntil(() => outcomes.Count == 1);
        Assert.Equal(id, Assert.Single(outcomes).Sequence);
        Assert.Equal(DiagnosticPublicationState.TransportAccepted, Assert.Single(outcomes).State);
        Assert.Equal(id, lane.LastPublishedSequence);
    }

    [Fact]
    public async Task SupersededPendingPublication_IsNeverMarkedAccepted()
    {
        var firstGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ConcurrentQueue<long>();
        var outcomes = new ConcurrentQueue<DiagnosticPublicationResult>();

        Task Send(PublishDiagnosticsPayload _, long sequence)
        {
            started.Enqueue(sequence);
            return sequence == 1 ? firstGate.Task : Task.CompletedTask;
        }

        var lane = new UriPublicationLane(Send);
        lane.Enqueue(Payload(1), outcomes.Enqueue);
        await WaitUntil(() => started.Count == 1);

        lane.Enqueue(Payload(2), outcomes.Enqueue);
        lane.Enqueue(Payload(3), outcomes.Enqueue);

        await WaitUntil(() => outcomes.Any(x => x.Sequence == 2));
        Assert.Equal(DiagnosticPublicationState.Superseded,
            outcomes.Single(x => x.Sequence == 2).State);
        Assert.DoesNotContain(outcomes, x => x.Sequence == 2 && x.State == DiagnosticPublicationState.TransportAccepted);

        firstGate.SetResult();
        await WaitUntil(() => outcomes.Count == 3);
        Assert.Equal(new long[] { 1, 3 }, started.ToArray());
        Assert.Equal(3, lane.LastPublishedSequence);
        Assert.Equal(1, lane.DroppedCount);
    }

    [Fact]
    public async Task FaultedNotification_ReportsFaultNotSent_AndDoesNotStopLane()
    {
        var outcomes = new ConcurrentQueue<DiagnosticPublicationResult>();
        var faults = new ConcurrentQueue<Exception>();
        var lane = new UriPublicationLane(
            (_, sequence) => sequence == 1
                ? Task.FromException(new InvalidOperationException("simulated-notification-fault"))
                : Task.CompletedTask,
            faults.Enqueue);

        lane.Enqueue(Payload(1), outcomes.Enqueue);
        await WaitUntil(() => outcomes.Count == 1);
        var first = Assert.Single(outcomes);
        Assert.Equal(DiagnosticPublicationState.Faulted, first.State);
        Assert.IsType<InvalidOperationException>(first.Error);
        Assert.Equal(0, lane.LastPublishedSequence);
        Assert.Single(faults);

        lane.Enqueue(Payload(2), outcomes.Enqueue);
        await WaitUntil(() => outcomes.Count == 2);
        Assert.Equal(DiagnosticPublicationState.TransportAccepted,
            outcomes.Single(x => x.Sequence == 2).State);
        Assert.Equal(2, lane.LastPublishedSequence);
    }

    [Fact]
    public async Task SinkWithoutAttachedRpc_DoesNotClaimSuccessfulPublication()
    {
        var outcomes = new ConcurrentQueue<DiagnosticPublicationResult>();
        var sink = new DiagnosticSink(new RecordingLspLogger());

        sink.Publish(Payload(), outcomes.Enqueue);

        await WaitUntil(() => outcomes.Count == 1);
        var result = Assert.Single(outcomes);
        Assert.Equal(DiagnosticPublicationState.Faulted, result.State);
        Assert.IsType<InvalidOperationException>(result.Error);
    }

    [Fact]
    public async Task Publisher_DoesNotReportClearSent_WhenSinkHasNoRpc()
    {
        var options = new ObservabilityOptions
        {
            Mode = ObservabilityMode.Detailed,
            Emitter = "diagnostic-publication-tests",
        };
        var observer = new CompilerObserver(options);
        var logger = new RecordingLspLogger();
        var publisher = new DiagnosticPublisher(logger, new DiagnosticSink(logger), observer);

        publisher.PublishEmpty(DocumentUri.Create(Path.GetFullPath("telemetry.cvl")));
        await WaitUntil(() => observer.Queue.Count >= 3);

        var events = new List<StampedEvent>();
        observer.Queue.DrainAll(events);
        Assert.Contains(events, x => x.Event.Event == "diagnostics.publish_attempt");
        Assert.Contains(events, x => x.Event.Event == "diagnostics.publish_faulted");
        Assert.DoesNotContain(events, x => x.Event.Event == "diagnostics.clear_sent");
    }

    [Fact]
    public async Task FailingTelemetryCallback_CannotBlockTheNextPublication()
    {
        var seen = new ConcurrentQueue<long>();
        var lane = new UriPublicationLane((_, _) => Task.CompletedTask);
        lane.Enqueue(Payload(1), _ => throw new InvalidOperationException("observer-fault"));
        await WaitUntil(() => lane.LastPublishedSequence == 1);

        lane.Enqueue(Payload(2), result => seen.Enqueue(result.Sequence));
        await WaitUntil(() => seen.Count == 1);
        Assert.Equal(2, Assert.Single(seen));
        Assert.Equal(2, lane.LastPublishedSequence);
    }

    [Fact]
    public async Task Complete_ReportsPendingPublicationAsCancelled()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = new ConcurrentQueue<DiagnosticPublicationResult>();
        var lane = new UriPublicationLane((_, _) =>
        {
            started.TrySetResult();
            return gate.Task;
        });

        lane.Enqueue(Payload(1), outcomes.Enqueue);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lane.Enqueue(Payload(2), outcomes.Enqueue);
        lane.Complete();
        await WaitUntil(() => outcomes.Any(x => x.Sequence == 2));
        Assert.Equal(DiagnosticPublicationState.Cancelled,
            outcomes.Single(x => x.Sequence == 2).State);

        gate.SetResult();
        await WaitUntil(() => outcomes.Count == 2);
        Assert.Equal(DiagnosticPublicationState.TransportAccepted,
            outcomes.Single(x => x.Sequence == 1).State);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for diagnostic publication result.");
            await Task.Delay(5);
        }
    }
}
