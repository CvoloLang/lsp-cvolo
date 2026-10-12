namespace Cvolo.LanguageServer.Core.Scheduling;

/// <summary>
/// Assigns strictly increasing <see cref="IngressSequence"/> values. Callers must
/// invoke <see cref="Next"/> from the single frame-admission path (the read loop
/// or the ingress synchronization context) so the assigned order equals receive
/// order.
/// </summary>
public sealed class IngressSequencer
{
    private long _current;

    /// <summary>Returns the most recently assigned sequence.</summary>
    public IngressSequence Current => new(Volatile.Read(ref _current));

    /// <summary>Assigns and returns the next sequence.</summary>
    public IngressSequence Next()
    {
        return new IngressSequence(Interlocked.Increment(ref _current));
    }
}
