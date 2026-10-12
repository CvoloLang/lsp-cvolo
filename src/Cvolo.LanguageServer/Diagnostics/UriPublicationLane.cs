namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// One on-wire emission lane per <c>WireUriKey</c> (INC "LSP Request
/// Scheduling"). Publications for a URI are serialized so the transport can
/// never accept an older diagnostic set after a newer one. While a send is in
/// flight, newer publications supersede any pending unsent publication, so only
/// the latest state is emitted next. Each enqueue is ordered by a monotonic
/// <see cref="LastEnqueuedSequence"/>.
/// </summary>
internal sealed class UriPublicationLane(Func<PublishDiagnosticsPayload, long, Task> send, Action<Exception>? onFault = null)
{
    private readonly Func<PublishDiagnosticsPayload, long, Task> _send = send;
    private readonly Action<Exception>? _onFault = onFault;
    private readonly Lock _gate = new();

    private PublishDiagnosticsPayload? _pending;
    private long _pendingSequence;
    private bool _sending;
    private long _enqueued;
    private long _published;
    private long _dropped;

    /// <summary>Monotonic sequence assigned to the most recently enqueued publication.</summary>
    public long LastEnqueuedSequence
    {
        get
        {
            lock (_gate)
            {
                return _enqueued;
            }
        }
    }

    /// <summary>Highest sequence whose transport acceptance has completed.</summary>
    public long LastPublishedSequence
    {
        get
        {
            lock (_gate)
            {
                return _published;
            }
        }
    }

    /// <summary>Count of pending, unsent publications superseded before transmission.</summary>
    public long DroppedCount
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>
    /// Atomically records a publication as the latest desired state for this URI
    /// and ensures the drainer is running. Returns the assigned monotonic
    /// publication sequence.
    /// </summary>
    public long Enqueue(PublishDiagnosticsPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        lock (_gate)
        {
            if (_pending is not null)
            {
                _dropped++;
            }

            long sequence = ++_enqueued;
            _pending = payload;
            _pendingSequence = sequence;
            if (!_sending)
            {
                _sending = true;
                _ = Task.Run(DrainAsync);
            }

            return sequence;
        }
    }

    /// <summary>Stops accepting work; a no-op for a lane that already drained.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _pending = null;
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            PublishDiagnosticsPayload payload;
            long sequence;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _sending = false;
                    return;
                }

                payload = _pending;
                sequence = _pendingSequence;
                _pending = null;
            }

            try
            {
                await _send(payload, sequence).ConfigureAwait(false);
                lock (_gate)
                {
                    if (sequence > _published)
                    {
                        _published = sequence;
                    }
                }
            }
            catch (Exception ex)
            {
                _onFault?.Invoke(ex);
            }
        }
    }
}
