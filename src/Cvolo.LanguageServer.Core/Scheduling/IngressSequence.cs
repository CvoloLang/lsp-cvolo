namespace Cvolo.LanguageServer.Core.Scheduling;

/// <summary>
/// A monotonic ordinal assigned to an inbound JSON-RPC frame at the transport
/// admission boundary, before concurrent handler fan-out (INC "LSP Request
/// Scheduling" §Inbound notification FIFO). Handler-entry order is not receive
/// order; this sequence is the receive order.
/// </summary>
public readonly record struct IngressSequence(long Value) : IComparable<IngressSequence>
{
    /// <summary>The sequence assigned before any frame has been admitted.</summary>
    public static IngressSequence None => new(0);

    /// <summary>Whether this is the <see cref="None"/> sentinel.</summary>
    public bool IsNone => Value == 0;

    public int CompareTo(IngressSequence other) => Value.CompareTo(other.Value);

    public static bool operator <(IngressSequence left, IngressSequence right) => left.Value < right.Value;

    public static bool operator >(IngressSequence left, IngressSequence right) => left.Value > right.Value;

    public static bool operator <=(IngressSequence left, IngressSequence right) => left.Value <= right.Value;

    public static bool operator >=(IngressSequence left, IngressSequence right) => left.Value >= right.Value;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
