using Cvolo.LanguageServer.Logging;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// One coalescing server-to-client refresh request. At most one request is in flight and one
/// follow-up is pending; the request is only sent when the client advertised support for it, and no
/// new work starts after shutdown. There is no timer and no polling: a refresh is a consequence of a
/// concrete event, never a background loop (§59, §60).
/// </summary>
internal sealed class ClientRefreshRequest(string method, Func<bool> supported, ILspLogger logger)
{
    private static readonly TimeSpan EditQuietPeriod = TimeSpan.FromMilliseconds(350);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private JsonRpc? _rpc;
    private bool _pending;
    private bool _stopped;
    private long _revision;

    /// <summary>Attaches the transport the request is sent over.</summary>
    public void Attach(JsonRpc rpc) => _rpc = rpc;

    /// <summary>Stops starting or following up refresh work (shutdown/disposal).</summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            _stopped = true;
            _pending = false;
        }
    }

    /// <summary>
    /// Requests one refresh. Coalesces concurrent requests and dispatches asynchronously, so a
    /// document synchronization or a configuration notification is never blocked on the client.
    /// </summary>
    public void Request()
    {
        if (!supported())
        {
            return;
        }

        lock (_stateLock)
        {
            if (_stopped)
            {
                return;
            }

            _revision++;
            _pending = true;
        }

        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            while (true)
            {
                long revision;
                lock (_stateLock)
                {
                    if (!_pending || _stopped)
                    {
                        _pending = false;
                        return;
                    }

                    _pending = false;
                    revision = _revision;
                }

                // Edits invalidate lenses and hints continuously; only dispatch once the edit
                // stream has been quiet so a pause never observes a stale request storm (§59, §60).
                await Task.Delay(EditQuietPeriod).ConfigureAwait(false);

                lock (_stateLock)
                {
                    if (_stopped)
                    {
                        return;
                    }

                    if (revision != _revision)
                    {
                        continue; // a newer request arrived while quieting; drain that instead
                    }
                }

                JsonRpc? rpc = _rpc;
                if (rpc is null)
                {
                    return;
                }

                try
                {
                    await rpc.InvokeWithParameterObjectAsync<object?>(method, null).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Warning($"{method} failed: {ex.Message}");
                }

                if (_stopped)
                {
                    return;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Server-to-client refresh coordinator for the editor-intelligence features. A configuration change
/// or a semantic target change asks for a lens refresh and a hint refresh; each is sent only when the
/// client supports it, and each is requested at most once while the previous request is in flight
/// (§59, §60, §65).
/// </summary>
internal sealed class EditorIntelligenceRefreshCoordinator(
    Func<bool> codeLensRefreshSupported,
    Func<bool> inlayHintRefreshSupported,
    ILspLogger logger)
{
    private readonly ClientRefreshRequest _codeLens = new("workspace/codeLens/refresh", codeLensRefreshSupported, logger);

    private readonly ClientRefreshRequest _inlayHints = new("workspace/inlayHint/refresh", inlayHintRefreshSupported, logger);

    public void Attach(JsonRpc rpc)
    {
        _codeLens.Attach(rpc);
        _inlayHints.Attach(rpc);
    }

    public void Stop()
    {
        _codeLens.Stop();
        _inlayHints.Stop();
    }

    /// <summary>Requests a refresh of both the lenses and the hints currently on screen.</summary>
    public void RequestRefresh()
    {
        _codeLens.Request();
        _inlayHints.Request();
    }
}
