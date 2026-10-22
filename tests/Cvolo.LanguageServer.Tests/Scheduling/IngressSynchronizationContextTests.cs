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
