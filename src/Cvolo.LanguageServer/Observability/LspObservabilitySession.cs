using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// Process-scoped observability session for the language server. The server is disabled by default
/// (mirroring the compiler's no-op default): a session only starts when an explicit mode is
/// configured. A JSONL writer is created only when <c>CVOLO_TRACE_PATH</c> names a file. Tests and
/// the VS Code client inject a collector instead of using environment configuration.
/// </summary>
internal sealed class LspObservabilitySession : IDisposable
{
	internal const string ModeEnvironmentVariable = "CVOLO_OBS_MODE";
	internal const string TracePathEnvironmentVariable = "CVOLO_TRACE_PATH";

	private readonly JsonlEventWriter? _writer;
	private int _stopped;

	private LspObservabilitySession(CompilerObserver observer, JsonlEventWriter? writer)
	{
		Observer = observer;
		_writer = writer;
	}

	internal CompilerObserver Observer { get; }

	internal static LspObservabilitySession Start()
	{
		var mode = ParseMode(Environment.GetEnvironmentVariable(ModeEnvironmentVariable));
		var outputPath = Environment.GetEnvironmentVariable(TracePathEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(outputPath))
			outputPath = null;

		var options = new ObservabilityOptions
		{
			Mode = mode,
			Emitter = "lsp",
			OutputPath = outputPath,
			QueueCapacity = 16384,
		};

		var clock = new SystemObservabilityClock();
		var queue = new BoundedEventQueue(options.QueueCapacity);
		var observer = new CompilerObserver(
			options,
			clock: clock,
			queue: queue,
			catalog: EventCatalog.CreateKernelCatalog());

		JsonlEventWriter? writer = null;
		if (outputPath is not null && mode != ObservabilityMode.Off)
			writer = new JsonlEventWriter(queue, observer.Identity, clock, options);

		var session = new LspObservabilitySession(observer, writer);
		session.Emit(new CompilerEvent
		{
			Event = "session.started",
			Kind = CompilerEventKind.Lifecycle,
			Detail = DiagnosticDetail.L0,
			Priority = EventPriority.P0,
			Attributes =
			[
				new("emitter", observer.Identity.Emitter),
				new("sessionId", observer.Identity.SessionId),
				new("serverInstanceId", observer.Identity.ServerInstanceId),
				new("processId", Environment.ProcessId.ToString()),
			],
		});
		return session;
	}

	internal static ObservabilityMode ParseMode(string? raw)
		=> raw?.Trim().ToLowerInvariant() switch
		{
			"basic" => ObservabilityMode.Basic,
			"detailed" => ObservabilityMode.Detailed,
			"repro" => ObservabilityMode.Repro,
			_ => ObservabilityMode.Off,
		};

	/// <summary>Emits one lifecycle event (never lost to the mode gate).</summary>
	internal void Emit(CompilerEvent evt) => Observer.EmitLifecycle(evt);

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _stopped, 1) != 0)
			return;

		Emit(new CompilerEvent
		{
			Event = "session.ended",
			Kind = CompilerEventKind.Lifecycle,
			Detail = DiagnosticDetail.L0,
			Priority = EventPriority.P0,
			Attributes =
			[
				new("sessionId", Observer.Identity.SessionId),
				new("serverInstanceId", Observer.Identity.ServerInstanceId),
			],
		});

		_writer?.Dispose();
	}
}
