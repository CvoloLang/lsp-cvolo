using Cvolo.LanguageServer.Core;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Read-request admission barrier (INC "LSP Request Scheduling"). A read request
/// waits here until every earlier admitted document input for its project has been
/// applied or definitively rejected, so a completion/hover/tokens request never
/// captures stale text that a queued didChange is about to publish. The wait is
/// bounded; on timeout the request fails with a recoverable typed non-success
/// rather than guessing at semantics.
/// </summary>
internal static class RequestBarrier
{
    /// <summary>
    /// Admission wait budget. Numerical latency thresholds are not frozen by the
    /// INC spec; this is a bounded, measured default (INC 2).
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static async Task WaitAsync(DocumentStore store, CancellationToken cancellationToken)
    {
        bool satisfied = await store.WaitForInputsAsync(Timeout, cancellationToken).ConfigureAwait(false);
        if (!satisfied)
        {
            var exception = new LocalRpcException("The request could not be admitted before the input barrier timed out.")
            {
                ErrorCode = ProtocolErrorCodes.RequestFailed,
                ErrorData = new CvoloErrorData(CvoloErrorData.InputBarrierTimeout, retryable: true),
            };
            throw exception;
        }
    }
}
