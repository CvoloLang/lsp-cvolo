using Cvolo.LanguageServer.Core.Scheduling;
using Cvolo.LanguageServer.Tests.TestSupport;
using StreamJsonRpc;
using System.Collections.Concurrent;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Scheduling;

internal sealed class PingTarget
{
    public ConcurrentQueue<(int Index, long Seq)> Observed { get; } = new();

    [JsonRpcMethod("ping")]
    public void Ping(int index) => Observed.Enqueue((index, IngressScope.Current.Value));
}

/// <summary>
/// V3/V4: the ingress synchronization context stamps every inbound frame in
/// receive order, and the stamp is observable at handler entry.
/// </summary>
public sealed class IngressSynchronizationContextTests
{
    [Fact]
    public async Task Document_is_admitted_at_transport_read_before_handler_dispatch()
    {
        var pipeline = new IngressPipeline();
        var admitted = new List<(long Seq, string Kind)>();
        pipeline.DocumentIngressAdmitted = (seq, kind) => admitted.Add((seq, kind));

        pipeline.ObserveInboundRpc("textDocument/didOpen", notification: true);
        pipeline.ObserveInboundRpc("textDocument/hover", notification: false);
        Assert.Equal(1L, Assert.Single(admitted).Seq);
        Assert.Equal("did_open", admitted[0].Kind);
        Assert.Equal(1, pipeline.Inputs.Metrics.Admitted);

        // The read request cannot bypass an older didOpen handler that is
        // delayed before attaching to the ordered writer.
        Task<BarrierOutcome> barrier = pipeline.Inputs.WaitForInputsBeforeAsync(
            new IngressSequence(2), TimeSpan.FromSeconds(3));
        Assert.False(barrier.IsCompleted);
        Assert.Equal(1L, pipeline.AssignDispatch().Value);
        Assert.Equal(2L, pipeline.AssignDispatch().Value);
        pipeline.Inputs.Attach(new IngressSequence(1), () => InputDisposition.Applied);
        Assert.Equal(BarrierOutcome.Satisfied, await barrier);
        Assert.False(pipeline.PairingStatus.Incomplete);
        Assert.Equal(0, pipeline.PairingStatus.Pending);
    }

    [Fact]
    public void Unmatched_dispatch_marks_incomplete_instead_of_guessing()
    {
        var pipeline = new IngressPipeline();
        Assert.Equal(1L, pipeline.AssignDispatch().Value);
        Assert.True(pipeline.PairingStatus.Incomplete);
        pipeline.ObserveInboundRpc("textDocument/didClose", notification: true);
        Assert.Equal(2L, pipeline.AssignDispatch().Value);
        Assert.Equal(1, pipeline.Inputs.Metrics.Admitted);
    }

    [Fact]
    public async Task Frames_AreStampedInReceiveOrder()
    {
        var target = new PingTarget();
        using var session = ProtocolSession.Start(configureRpc: rpc => rpc.AddLocalRpcTarget(target));

        for (int i = 0; i < 20; i++)
        {
            session.Client.SendRawJson($"{{\"jsonrpc\":\"2.0\",\"method\":\"ping\",\"params\":[{i}]}}");
        }

        await session.Client.WaitUntilAsync(() => target.Observed.Count >= 20, "twenty ping frames observed", TimeSpan.FromSeconds(5));

        var observed = target.Observed.ToArray();
        var dump = string.Join(", ", observed.Select(o => $"{o.Index}->{o.Seq}"));
        Assert.True(observed.Length == 20, $"observed {observed.Length}: [{dump}]");
        foreach (var (index, seq) in observed)
        {
            Assert.Equal(index + 1, seq);
        }
    }
}
