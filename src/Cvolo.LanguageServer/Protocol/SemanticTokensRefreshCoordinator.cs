using Cvolo.LanguageServer.Logging;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Coalesces global semantic-token refresh notifications. Edits to an open file
/// naturally invalidate that file in VS Code, but other open files can also be
/// affected by a project change. A short quiet period prevents refresh storms.
/// </summary>
internal sealed class SemanticTokensRefreshCoordinator(Func<bool> enabled, ILspLogger logger)
{
    private static readonly TimeSpan EditQuietPeriod = TimeSpan.FromMilliseconds(350);
    private readonly object _stateLock = new();
    private JsonRpc? _rpc;
    private bool _pending;
    private bool _running;
    private bool _stopped;
    private long _revision;

    public void Attach(JsonRpc rpc)
    {
        lock (_stateLock)
            _rpc = rpc;
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            _stopped = true;
            _pending = false;
            _revision++;
        }
    }

    public void RequestRefresh()
    {
        if (!enabled())
            return;
        lock (_stateLock)
        {
            if (_stopped)
                return;
            _revision++;
            _pending = true;
            if (_running)
                return;
            _running = true;
        }
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                long revision;
                lock (_stateLock)
                {
                    if (_stopped || !_pending)
                        return;
                    _pending = false;
                    revision = _revision;
                }

                await Task.Delay(EditQuietPeriod).ConfigureAwait(false);
                JsonRpc? rpc;
                lock (_stateLock)
                {
                    if (_stopped)
                        return;
                    if (revision != _revision)
                        continue;
                    rpc = _rpc;
                }
                if (rpc is null)
                    return;
                try
                {
                    await rpc.InvokeWithParameterObjectAsync<object?>(
                        "workspace/semanticTokens/refresh", null).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Warning($"semantic token refresh failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"semantic token refresh worker failed: {ex.Message}");
        }
        finally
        {
            bool restart;
            lock (_stateLock)
            {
                _running = false;
                restart = _pending && !_stopped;
                if (restart)
                    _running = true;
            }
            if (restart)
                _ = Task.Run(DrainAsync);
        }
    }
}
