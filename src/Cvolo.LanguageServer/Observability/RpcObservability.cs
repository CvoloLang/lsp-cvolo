using System.Collections.Concurrent;
using Cvolo.Observability.Abstractions;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Correlation between JSON-RPC request ids at the transport boundary and compiler telemetry
/// events. StreamJsonRpc exposes no ambient request context to a registered target method, so the
/// request identity is captured where it is actually observable: the message handler records the
/// incoming id, and the formatter callback reports the response id. The map between them lives
/// here, never in a static singleton, so two server instances cannot share correlation state.
/// Numeric and string ids stay distinct end to end.
/// </summary>
internal sealed class RpcObservability : IDisposable
{
	private readonly ICompilerObserver _observer;
	private readonly ConcurrentDictionary<string, string> _pendingRequestUids = new(StringComparer.Ordinal);
	private long _requestCounter;
	private JsonRpc? _rpc;
	private bool _disposed;

	public RpcObservability(ICompilerObserver observer)
	{
		ArgumentNullException.ThrowIfNull(observer);
		_observer = observer;
	}

	private bool Enabled => _observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0);

	/// <summary>Records one incoming request and allocates its trace-level request identity.</summary>
	public void OnRequestReceived(JsonRpcRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!Enabled)
			return;

		var key = KeyOf(request.RequestId);
		var requestUid = $"req-{Interlocked.Increment(ref _requestCounter)}";
		_pendingRequestUids[key] = requestUid;

		_observer.Emit(new CompilerEvent
		{
			Event = "rpc.request.received",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L0,
			Priority = EventPriority.P1,
			Outcome = "received",
			RequestUid = requestUid,
			RpcRequestId = ToRpcRequestId(request.RequestId),
			Attributes = [new KeyValuePair<string, string>("method", request.Method)],
		});
	}

	/// <summary>Records one incoming notification (no id, no response expected).</summary>
	public void OnNotificationReceived(string method)
	{
		if (!Enabled)
			return;

		_observer.Emit(new CompilerEvent
		{
			Event = "rpc.notification.received",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L0,
			Priority = EventPriority.P1,
			Outcome = "received",
			Attributes = [new KeyValuePair<string, string>("method", method)],
		});
	}

	/// <summary>Pairs a written response with the request identity captured on receipt.</summary>
	public void OnResponseSent(RequestId requestId, bool success)
	{
		if (!Enabled)
			return;

		var key = KeyOf(requestId);
		var hasRequest = _pendingRequestUids.TryRemove(key, out var requestUid);

		_observer.Emit(new CompilerEvent
		{
			Event = "rpc.response.sent",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L0,
			Priority = EventPriority.P1,
			Outcome = success ? "success" : "error",
			RequestUid = requestUid,
			RpcRequestId = ToRpcRequestId(requestId),
			Attributes =
			[
				new("method", hasRequest ? "response" : "response_without_received_request"),
			],
		});
	}

	/// <summary>Subscribes to response completion on the server's RPC instance.</summary>
	public void Attach(JsonRpc rpc)
	{
		ArgumentNullException.ThrowIfNull(rpc);
		if (_disposed)
			return;

		_rpc = rpc;
		if (rpc is IJsonRpcFormatterCallbacks callbacks)
			callbacks.ResponseSent += OnResponseSent;
	}

	private void OnResponseSent(object? sender, JsonRpcResponseEventArgs args)
		=> OnResponseSent(args.RequestId, args.IsSuccessfulResponse);

	private static string KeyOf(RequestId id)
		=> id.Number is { } number
			? $"n:{number}"
			: id.String is { } text
				? $"s:{text}"
				: "empty";

	private static RpcRequestId ToRpcRequestId(RequestId id)
		=> id.Number is { } number
			? RpcRequestId.Number(number)
			: RpcRequestId.Text(id.String ?? string.Empty);

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		if (_rpc is IJsonRpcFormatterCallbacks callbacks)
			callbacks.ResponseSent -= OnResponseSent;
		_rpc = null;
		_pendingRequestUids.Clear();
	}
}
