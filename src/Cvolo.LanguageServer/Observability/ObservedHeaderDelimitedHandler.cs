using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Header-delimited message handler that records the JSON-RPC request id as each incoming message
/// crosses the transport boundary. It must derive from <see cref="HeaderDelimitedMessageHandler"/>
/// and call <c>base.ReadCoreAsync</c>: a wrapper around another handler instance (including a
/// <c>MessageHandlerBase</c> delegating to an inner handler) stops the read loop after the first
/// message because the base class owns the sequential-read state. This shape is verified by the
/// request-id correlation experiment.
/// </summary>
internal sealed class ObservedHeaderDelimitedHandler : HeaderDelimitedMessageHandler
{
	private readonly RpcObservability? _observability;
	private readonly Func<bool>? _isRejectedInitializePending;
	private readonly Action? _rejectedInitializeFlushed;
	private bool _notifyAfterFlush;

	public ObservedHeaderDelimitedHandler(
		Stream writeStream,
		Stream readStream,
		IJsonRpcMessageFormatter formatter,
		RpcObservability? observability,
		Func<bool>? isRejectedInitializePending = null,
		Action? rejectedInitializeFlushed = null)
		: base(writeStream, readStream, formatter)
	{
		_observability = observability;
		_isRejectedInitializePending = isRejectedInitializePending;
		_rejectedInitializeFlushed = rejectedInitializeFlushed;
	}

	protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
	{
		var message = await base.ReadCoreAsync(cancellationToken).ConfigureAwait(false);

		if (_observability is not null && message is JsonRpcRequest request)
		{
			if (request.RequestId.IsEmpty)
				_observability.OnNotificationReceived(request.Method);
			else
				_observability.OnRequestReceived(request);
		}

		return message;
	}

	/// <summary>
	/// A ResponseSent callback does not guarantee the JSON-RPC frame was flushed
	/// to stdout. Only flag the actual error response while the server has
	/// rejected initialize for a dead client; do not terminate for unrelated
	/// outbound notifications or successful responses.
	/// </summary>
	protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
	{
		base.Write(content, cancellationToken);
		_notifyAfterFlush = content is JsonRpcError
			&& _isRejectedInitializePending?.Invoke() == true;
	}

	protected override async ValueTask FlushAsync(CancellationToken cancellationToken)
	{
		await base.FlushAsync(cancellationToken).ConfigureAwait(false);
		if (_notifyAfterFlush)
		{
			_notifyAfterFlush = false;
			_rejectedInitializeFlushed?.Invoke();
		}
	}
}
