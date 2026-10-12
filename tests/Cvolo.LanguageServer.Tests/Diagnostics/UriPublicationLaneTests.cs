using Cvolo.LanguageServer.Diagnostics;
using System.Collections.Concurrent;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

public class UriPublicationLaneTests
{
    private static PublishDiagnosticsPayload Payload(string uri, int? version = null)
        => new(new Uri(uri), version, Array.Empty<DiagnosticPayload>());

    [Fact]
    public async Task Enqueue_SerializesSendsPerUri()
    {
        var started = new ConcurrentQueue<(PublishDiagnosticsPayload Payload, long Sequence)>();
        var gates = new List<TaskCompletionSource>();
        int inFlight = 0;
        int maxInFlight = 0;

        Task Send(PublishDiagnosticsPayload payload, long sequence)
        {
            Interlocked.Increment(ref inFlight);
            maxInFlight = Math.Max(maxInFlight, Volatile.Read(ref inFlight));
            started.Enqueue((payload, sequence));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gates)
            {
                gates.Add(gate);
            }

            return gate.Task.ContinueWith(_ => Interlocked.Decrement(ref inFlight), TaskScheduler.Default);
        }

        var lane = new UriPublicationLane(Send);

        lane.Enqueue(Payload("file:///d:/a.cvl"));
        await WaitUntil(() => !started.IsEmpty);

        lane.Enqueue(Payload("file:///d:/a.cvl"));
        lane.Enqueue(Payload("file:///d:/a.cvl"));

        Release(gates, 0);
        await WaitUntil(() => started.Count >= 2);

        Release(gates, 1);
        await WaitUntil(() => lane.DroppedCount == 1);

        Assert.Equal(1, maxInFlight);
        Assert.Equal(3, lane.LastEnqueuedSequence);
        Assert.Equal(2, started.Count);
    }

    [Fact]
    public async Task Enqueue_SupersedesPendingUnsentEntry()
    {
        var started = new ConcurrentQueue<long>();
        var gates = new List<TaskCompletionSource>();

        Task Send(PublishDiagnosticsPayload payload, long sequence)
        {
            started.Enqueue(sequence);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gates)
            {
                gates.Add(gate);
            }

            return gate.Task;
        }

        var lane = new UriPublicationLane(Send);

        lane.Enqueue(Payload("file:///d:/a.cvl"));
        await WaitUntil(() => started.Count == 1);

        lane.Enqueue(Payload("file:///d:/a.cvl"));
        lane.Enqueue(Payload("file:///d:/a.cvl"));

        Release(gates, 0);
        await WaitUntil(() => started.Count == 2);

        Release(gates, 1);
        await WaitUntil(() => lane.LastPublishedSequence == 3);

        Assert.Equal(new long[] { 1, 3 }, started.ToArray());
        Assert.Equal(1, lane.DroppedCount);
    }

    [Fact]
    public async Task Enqueue_NeverOldAfterNew_PublishesAscendingAfterSupersession()
    {
        var published = new ConcurrentQueue<long>();
        var gates = new List<TaskCompletionSource>();

        Task Send(PublishDiagnosticsPayload payload, long sequence)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gates)
            {
                gates.Add(gate);
            }

            return gate.Task.ContinueWith(
                _ => published.Enqueue(sequence),
                TaskScheduler.Default);
        }

        var lane = new UriPublicationLane(Send);

        lane.Enqueue(Payload("file:///d:/a.cvl", 0));
        await WaitUntil(() => gates.Count == 1);

        for (int i = 1; i < 5; i++)
        {
            lane.Enqueue(Payload("file:///d:/a.cvl", i));
        }

        Release(gates, 0);
        await WaitUntil(() => gates.Count == 2);

        Release(gates, 1);
        await WaitUntil(() => lane.LastPublishedSequence == 5);

        long[] order = published.ToArray();
        Assert.Equal(new long[] { 1, 5 }, order);
        for (int i = 1; i < order.Length; i++)
        {
            Assert.True(order[i] > order[i - 1]);
        }
    }

    [Fact]
    public async Task Complete_ClearsPendingEntry()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ConcurrentQueue<long>();

        Task Send(PublishDiagnosticsPayload payload, long sequence)
        {
            started.Enqueue(sequence);
            return gate.Task;
        }

        var lane = new UriPublicationLane(Send);
        lane.Enqueue(Payload("file:///d:/a.cvl"));
        await WaitUntil(() => started.Count == 1);

        lane.Enqueue(Payload("file:///d:/a.cvl"));
        lane.Complete();
        gate.SetResult();

        await Task.Delay(50);

        Assert.Equal(new long[] { 1 }, started.ToArray());
    }

    private static void Release(List<TaskCompletionSource> gates, int index)
    {
        lock (gates)
        {
            gates[index].TrySetResult();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the expected condition.");
            }

            await Task.Delay(5);
        }
    }
}
