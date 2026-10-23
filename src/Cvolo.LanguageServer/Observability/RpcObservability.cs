using System.Collections.Concurrent;
using Cvolo.Observability;
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
internal sealed class RpcObservability : IDisposable, IActivityTracingStrategy
{
	// Async-flow-local correlation. No global map and no cross-request guessing.
	private static readonly AsyncLocal<string?> RequestUidContext = new();
	internal static string? CurrentRequestUid => RequestUidContext.Value;

	private readonly ICompilerObserver _observer;
	private readonly ConcurrentDictionary<CorrelationKey, string> _pendingRequestUids = new();
	private string? _serverInstanceId;
	private long _requestCounter;
	private JsonRpc? _rpc;
	private bool _disposed;

	public RpcObservability(ICompilerObserver observer)
	{
		ArgumentNullException.ThrowIfNull(observer);
		_observer = observer;
		// Reuse the same real process identity as the event writer. The abstraction
		// also permits lightweight custom observers without an identity object.
		_serverInstanceId = (observer as CompilerObserver)?.Identity.ServerInstanceId;
	}

	private bool Enabled => _observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0);

	private string ServerInstanceId
	{
		get
		{
			var existing = Volatile.Read(ref _serverInstanceId);
			if (existing is not null)
				return existing;

			// No GUID is generated until recording is enabled. The CAS keeps a
			// single identity even when requests arrive concurrently.
			var candidate = Guid.NewGuid().ToString("N");
			return Interlocked.CompareExchange(ref _serverInstanceId, candidate, null) ?? candidate;
		}
	}

	/// <summary>Records one incoming request and allocates its trace-level request identity.</summary>
	public void OnRequestReceived(JsonRpcRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!Enabled)
			return;

		var key = KeyOf(request.RequestId);
		// A local counter alone would collide across LSP processes and test hosts.
		// The typed protocol id remains separate in RpcRequestId.
		var requestUid = $"{ServerInstanceId}/rpc/{Interlocked.Increment(ref _requestCounter)}";
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
		var enabled = Enabled;
		if (!enabled && _pendingRequestUids.IsEmpty)
			return;

		// A response can finish after recording was disabled. Release its pending
		// correlation even then, without emitting an event in the disabled epoch.
		var key = KeyOf(requestId);
		var hasRequest = _pendingRequestUids.TryRemove(key, out var requestUid);
		if (!enabled || !hasRequest)
		{
			// The request may have arrived while recording was Off, then
			// completed after Start enabled it. There is no observed request
			// identity to attach to this response. Never fabricate a UID or emit
			// a malformed paired RPC event; normal requests arriving after the
			// transition are fully correlated.
			return;
		}

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
				new("method", "response"),
			],
		});
	}

	/// <summary>
	/// The strategy is invoked for the actual inbound request by StreamJsonRpc.
	/// An absent correlation stays absent; no inferred match by method or order.
	/// </summary>
	public IDisposable? ApplyInboundActivity(JsonRpcRequest request)
	{
		if (!Enabled || request.RequestId.IsEmpty
			|| !_pendingRequestUids.TryGetValue(KeyOf(request.RequestId), out var requestUid))
			return null;

		var previous = RequestUidContext.Value;
		RequestUidContext.Value = requestUid;
		return new RequestScope(previous);
	}

	/// <summary>No outgoing JSON-RPC headers or protocol messages are modified.</summary>
	public void ApplyOutboundActivity(JsonRpcRequest request)
	{
	}

	private sealed class RequestScope(string? previous) : IDisposable
	{
		public void Dispose() => RequestUidContext.Value = previous;
	}

	/// <summary>Subscribes to response completion on the server's RPC instance.</summary>
	public void Attach(JsonRpc rpc)
	{
		ArgumentNullException.ThrowIfNull(rpc);
		if (_disposed)
			return;

		_rpc = rpc;
		rpc.ActivityTracingStrategy = this;
		if (rpc is IJsonRpcFormatterCallbacks callbacks)
			callbacks.ResponseSent += OnResponseSent;
	}

	private void OnResponseSent(object? sender, JsonRpcResponseEventArgs args)
		=> OnResponseSent(args.RequestId, args.IsSuccessfulResponse);

	// Use a typed value key: no temporary formatted strings on every
	// receive/dispatch/response lookup. The tag ensures JSON-RPC numeric
	// request 123 is never confused with string request "123".
	private readonly record struct CorrelationKey(long Number, string? Text, bool IsNumber);

	private static CorrelationKey KeyOf(RequestId id)
		=> id.Number is { } number
			? new CorrelationKey(number, null, true)
			: new CorrelationKey(0, id.String, false);

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
