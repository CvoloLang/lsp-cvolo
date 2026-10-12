using Cvolo.LanguageServer.Core.Scheduling;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Scheduling;

/// <summary>
/// V8/V9: the ordered input writer applies inputs strictly in ascending sequence,
/// and the read barrier waits for earlier input publication only.
/// </summary>
public sealed class OrderedInputQueueTests
{
    private static IngressSequence Seq(long value) => new(value);

    [Fact]
    public void InOrder_InputsApplyInSequence_OnTheCallingThread()
    {
        var queue = new OrderedInputQueue();
        var applied = new List<long>();

        queue.Admit(Seq(1));
        queue.Admit(Seq(2));
        queue.Attach(Seq(1), () => { applied.Add(1); return InputDisposition.Applied; });
        queue.Attach(Seq(2), () => { applied.Add(2); return InputDisposition.Applied; });

        Assert.Equal(new long[] { 1, 2 }, applied);
    }

    [Fact]
    public void OutOfOrder_LaterInputIsDeferredUntilEarlierInputAttaches()
    {
        var queue = new OrderedInputQueue();
        var applied = new List<long>();

        queue.Admit(Seq(1));
        queue.Admit(Seq(2));

        queue.Attach(Seq(2), () => { applied.Add(2); return InputDisposition.Applied; });
        Assert.Empty(applied);

        queue.Attach(Seq(1), () => { applied.Add(1); return InputDisposition.Applied; });

        Assert.Equal(new long[] { 1, 2 }, applied);
    }

    [Fact]
    public async Task Barrier_WaitsForEarlierInput_ThenSatisfies()
    {
        var queue = new OrderedInputQueue();
        queue.Admit(Seq(1));

        Task<BarrierOutcome> wait = queue.WaitForInputsBeforeAsync(Seq(2), TimeSpan.FromSeconds(5));
        Assert.False(wait.IsCompleted);

        queue.Attach(Seq(1), () => InputDisposition.Applied);

        Assert.Equal(BarrierOutcome.Satisfied, await wait);
    }

    [Fact]
    public async Task Barrier_NoEarlierInput_SatisfiesImmediately()
    {
        var queue = new OrderedInputQueue();
        Assert.Equal(BarrierOutcome.Satisfied, await queue.WaitForInputsBeforeAsync(Seq(5), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Barrier_DoesNotWaitForLaterInputs()
    {
        var queue = new OrderedInputQueue();
        queue.Admit(Seq(7));
        queue.Attach(Seq(7), () => InputDisposition.Applied);

        Assert.Equal(BarrierOutcome.Satisfied, await queue.WaitForInputsBeforeAsync(Seq(5), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Barrier_TimesOut_WhenEarlierInputNeverSettles()
    {
        var queue = new OrderedInputQueue();
        queue.Admit(Seq(1));

        Assert.Equal(BarrierOutcome.TimedOut, await queue.WaitForInputsBeforeAsync(Seq(2), TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task Barrier_ReleasesWhenEarlierInputSettlesWithoutApplying()
    {
        var queue = new OrderedInputQueue();
        queue.Admit(Seq(1));

        Task<BarrierOutcome> wait = queue.WaitForInputsBeforeAsync(Seq(2), TimeSpan.FromSeconds(5));
        queue.Settle(Seq(1), InputDisposition.Rejected);

        Assert.Equal(BarrierOutcome.Satisfied, await wait);
        Assert.Equal(1, queue.Metrics.Rejected);
    }

    [Fact]
    public async Task Complete_CompletesOutstandingBarriers()
    {
        var queue = new OrderedInputQueue();
        queue.Admit(Seq(1));

        Task<BarrierOutcome> wait = queue.WaitForInputsBeforeAsync(Seq(2), TimeSpan.FromSeconds(30));
        queue.Complete();

        Assert.Equal(BarrierOutcome.Completed, await wait);
    }

    [Fact]
    public void AppliedAndRejected_AreCounted()
    {
        var queue = new OrderedInputQueue();
        queue.Admit(Seq(1));
        queue.Admit(Seq(2));
        queue.Attach(Seq(1), () => InputDisposition.Applied);
        queue.Attach(Seq(2), () => InputDisposition.Rejected);

        InputQueueMetrics metrics = queue.Metrics;
        Assert.Equal(2, metrics.Admitted);
        Assert.Equal(1, metrics.Applied);
        Assert.Equal(1, metrics.Rejected);
        Assert.Equal(2, metrics.MaxQueued);
        Assert.Equal(Seq(2), queue.SettledThrough);
    }
}
