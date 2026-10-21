using Cvolo.LanguageServer.Logging;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using StreamJsonRpc;
using System.Collections.Concurrent;

namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Server-to-client notification channel for <c>textDocument/publishDiagnostics</c>.
/// The JSON-RPC endpoint is attached after construction, and the related
/// information and version capabilities are captured from <c>initialize</c>.
/// Publications are serialized through one <see cref="UriPublicationLane"/> per
/// URI so the transport can never accept an older diagnostic set after a newer
/// one. Publishing is fire-and-forget and never blocks the JSON-RPC receive
/// loop; asynchronous failures are observed and logged rather than crashing the
/// server.
/// </summary>
internal sealed class DiagnosticSink(ILspLogger? logger = null)
{
    private readonly ILspLogger _logger = logger ?? new NullLspLogger();
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<Uri, UriPublicationLane> _lanes = new();
    private JsonRpc? _rpc;
    private bool _relatedInformationSupported;
    private bool _versionSupported;

    /// <summary>Whether the client advertised related diagnostic information.</summary>
    public bool RelatedInformationSupported
    {
        get
        {
            lock (_lock)
            {
                return _relatedInformationSupported;
            }
        }
    }

    /// <summary>Whether the client advertised <c>publishDiagnostics.versionSupport</c>.</summary>
    public bool VersionSupported
    {
        get
        {
            lock (_lock)
            {
                return _versionSupported;
            }
        }
    }

    public void SetRelatedInformationSupported(bool supported)
    {
        lock (_lock)
        {
            _relatedInformationSupported = supported;
        }
    }

    public void SetVersionSupported(bool supported)
    {
        lock (_lock)
        {
            _versionSupported = supported;
        }
    }

    public void Attach(JsonRpc rpc)
    {
        lock (_lock)
        {
            _rpc = rpc;
        }
    }

    /// <summary>
    /// Queues a notification without blocking its caller. The optional callback is invoked
    /// once when the JSON-RPC send task succeeds, faults, or the queued publication is
    /// superseded without sending. Success does not assert client delivery/acknowledgement.
    /// </summary>
    public void Publish(PublishDiagnosticsPayload parameters, Action<DiagnosticPublicationResult>? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        UriPublicationLane lane = _lanes.GetOrAdd(parameters.Uri, _ => new UriPublicationLane(SendAsync, OnLaneFault));
        lane.Enqueue(parameters, onCompleted);
    }

    private void OnLaneFault(Exception ex)
    {
        _logger.Warning($"Publishing diagnostics failed: {ex.Message}");
    }

    private Task SendAsync(PublishDiagnosticsPayload payload, long sequence)
    {
        JsonRpc? rpc;
        lock (_lock)
        {
            rpc = _rpc;
        }

        if (rpc is null)
        {
            const string message = "publishDiagnostics requested before the JSON-RPC channel was attached";
            _logger.Warning($"[diag] {message}; notification dropped.");
            // The lane must see a failure, not a successfully completed task.
            return Task.FromException(new InvalidOperationException(message));
        }

        try
        {
            Task notification = rpc.NotifyWithParameterObjectAsync(Methods.TextDocumentPublishDiagnosticsName, payload);
            _logger.Debug($"[diag] queued publishDiagnostics #{sequence} for {payload.Uri} ({payload.Diagnostics.Length} diagnostic(s)).");
            return notification;
        }
        catch (Exception ex)
        {
            // The lane owns fault reporting. Never claim a successful notification here.
            return Task.FromException(ex);
        }
    }

    /// <summary>
    /// Observes a fire-and-forget notification so a later fault is logged
    /// instead of surfacing as an unobserved task exception. Synchronous
    /// completion is inspected inline; otherwise a fault-only continuation
    /// records the failure without blocking the caller.
    /// </summary>
    internal static void ObserveNotification(Task notification, ILspLogger logger)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(logger);

        if (notification.IsCompleted)
        {
            if (notification.IsFaulted)
            {
                LogFault(notification.Exception, logger);
            }

            return;
        }

        _ = notification.ContinueWith(
            task => LogFault(task.Exception, logger),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void LogFault(Exception? exception, ILspLogger logger)
    {
        var message = exception?.GetBaseException().Message ?? "unknown error";
        logger.Warning($"Publishing diagnostics failed: {message}");
    }
}
