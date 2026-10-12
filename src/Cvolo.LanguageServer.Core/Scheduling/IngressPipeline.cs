namespace Cvolo.LanguageServer.Core.Scheduling;

/// <summary>
/// Owns the serialized ingress boundary introduced by INC "LSP Request
/// Scheduling". The ingress <c>SynchronizationContext</c> assigns the next
/// <see cref="IngressSequence"/> to every inbound frame in receive order; document
/// input notifications then admit themselves into the <see cref="Inputs"/> writer
/// order from their handler, and read requests wait at the same barrier before
/// capturing state.
/// </summary>
public sealed class IngressPipeline
{
    /// <summary>Monotonic sequence authority for admission.</summary>
    public IngressSequencer Sequencer { get; } = new();

    /// <summary>The serialized document-input writer order and read barrier.</summary>
    public OrderedInputQueue Inputs { get; } = new();
}
