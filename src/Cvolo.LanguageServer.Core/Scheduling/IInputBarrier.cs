namespace Cvolo.LanguageServer.Core.Scheduling;

/// <summary>
/// The read barrier guarding ordered input publication (INC "LSP Request
/// Scheduling"). A request admitted with sequence <c>q</c> captures state only
/// after the input writer has applied or definitively rejected every earlier
/// relevant input event. The barrier waits for input <em>publication</em>, never
/// for backend semantic readiness, and is separately bounded and measured.
/// </summary>
public interface IInputBarrier
{
    /// <summary>
    /// Completes <see cref="BarrierOutcome.Satisfied"/> once every admitted input
    /// with a sequence strictly earlier than <paramref name="requestSequence"/> has
    /// been applied or rejected, or <see cref="BarrierOutcome.TimedOut"/> when the
    /// admission wait exceeds <paramref name="timeout"/>.
    /// </summary>
    Task<BarrierOutcome> WaitForInputsBeforeAsync(IngressSequence requestSequence, TimeSpan timeout, CancellationToken cancellationToken = default);
}
