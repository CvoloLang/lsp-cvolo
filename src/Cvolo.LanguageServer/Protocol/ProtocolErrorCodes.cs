namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// JSON-RPC error codes used by the protocol foundation.
/// </summary>
internal static class ProtocolErrorCodes
{
    /// <summary>
    /// A request was received before the session was initialized (LSP-specific
    /// code, also used by VS Code's JSON-RPC implementation).
    /// </summary>
    public const int ServerNotInitialized = -32002;

    /// <summary>
    /// The server cancelled a request it was already executing. Only sent when
    /// the client declares it handles server cancellation.
    /// </summary>
    public const int ServerCancelled = -32802;

    public const int RequestCancelled = -32800;
    public const int ContentModified = -32801;
    public const int RequestFailed = -32803;
}

/// <summary>
/// The bounded, typed reason attached to a recoverable request failure. It is
/// published as JSON-RPC error data under the <c>cvoloKind</c> property so the
/// client can distinguish an admission/scheduling outcome from a backend fault
/// without guessing at the message text.
/// </summary>
internal sealed record CvoloErrorData(string cvoloKind, bool retryable, int? retryAfterMs = null)
{
    public const string InputBarrierTimeout = "InputBarrierTimeout";
    public const string SupersededByQueuedInput = "SupersededByQueuedInput";
    public const string ViewChanged = "ViewChanged";
    public const string BackendUnavailable = "BackendUnavailable";
}
