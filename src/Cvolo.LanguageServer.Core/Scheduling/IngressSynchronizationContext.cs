namespace Cvolo.LanguageServer.Core.Scheduling;

/// <summary>
/// A <see cref="SynchronizationContext"/> installed on the JSON-RPC endpoint so
/// every inbound frame is dispatched in receive order. StreamJsonRpc invokes the
/// context once per received frame, in the order frames are read, so this is the
/// serialized admission boundary at which an <see cref="IngressSequence"/> can be
/// assigned before concurrent handler fan-out.
/// </summary>
/// <remarks>
/// This does <b>not</b> serialize handlers: each dispatch is stamped and then run
/// on the thread pool (preserving concurrency), and the handler continuation is
/// invoked on that worker thread so it observes its sequence through
/// <see cref="IngressScope.Current"/>. Async continuations of a handler are not
/// routed back through here because the worker runs without a captured context.
/// </remarks>
public sealed class IngressSynchronizationContext(IngressPipeline pipeline) : SynchronizationContext
{
    private readonly IngressPipeline _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);

        // The identity was allocated by the transport reader before handler dispatch.
        IngressSequence sequence = _pipeline.AssignDispatch();

        // Preserve the ambient sequence for callbacks posted from within an
        // already-stamped dispatch (for example a nested Post from a handler).
        IngressSequence parent = IngressScope.Current;

        _ = Task.Run(() =>
        {
            IngressScope.Set(sequence);
            try
            {
                d(state);
            }
            finally
            {
                IngressScope.Set(parent);
            }
        });
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        IngressSequence sequence = _pipeline.AssignDispatch();
        IngressSequence parent = IngressScope.Current;
        IngressScope.Set(sequence);
        try
        {
            d(state);
        }
        finally
        {
            IngressScope.Set(parent);
        }
    }
}
