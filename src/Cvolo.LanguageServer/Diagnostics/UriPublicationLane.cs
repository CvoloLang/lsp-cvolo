namespace Cvolo.LanguageServer.Diagnostics;

/// <summary>
/// Terminal state of one enqueue. "TransportAccepted" means the asynchronous JSON-RPC
/// notification API completed successfully, not that the client acknowledged processing it.
/// </summary>
internal enum DiagnosticPublicationState
{
    TransportAccepted,
    Faulted,
    Superseded,
    Cancelled,
}

internal readonly record struct DiagnosticPublicationResult(
    long Sequence,
    DiagnosticPublicationState State,
    Exception? Error = null);

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
    private Action<DiagnosticPublicationResult>? _pendingCompletion;
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
    /// Records a publication as the latest desired state. An optional completion callback
    /// receives exactly one terminal outcome for this enqueue: accepted, faulted,
    /// superseded, or cancelled before transmission. Callbacks never run under the lock.
    /// </summary>
    public long Enqueue(PublishDiagnosticsPayload payload, Action<DiagnosticPublicationResult>? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        Action<DiagnosticPublicationResult>? displaced = null;
        long displacedSequence = 0;
        long sequence;

        lock (_gate)
        {
            if (_pending is not null)
            {
                _dropped++;
                displaced = _pendingCompletion;
                displacedSequence = _pendingSequence;
            }

            sequence = ++_enqueued;
            _pending = payload;
            _pendingSequence = sequence;
            _pendingCompletion = onCompleted;
            if (!_sending)
            {
                _sending = true;
                _ = Task.Run(DrainAsync);
            }
        }

        Report(displaced, new DiagnosticPublicationResult(displacedSequence, DiagnosticPublicationState.Superseded));
        return sequence;
    }

    /// <summary>Discards a pending unsent entry, reporting cancellation (not supersession).</summary>
    public void Complete()
    {
        Action<DiagnosticPublicationResult>? displaced;
        long displacedSequence;
        lock (_gate)
        {
            displaced = _pendingCompletion;
            displacedSequence = _pendingSequence;
            _pending = null;
            _pendingCompletion = null;
        }

        Report(displaced, new DiagnosticPublicationResult(displacedSequence, DiagnosticPublicationState.Cancelled));
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            PublishDiagnosticsPayload payload;
            Action<DiagnosticPublicationResult>? completion;
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
                completion = _pendingCompletion;
                _pending = null;
                _pendingCompletion = null;
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
                Report(completion, new DiagnosticPublicationResult(sequence, DiagnosticPublicationState.TransportAccepted));
            }
            catch (Exception ex)
            {
                try
                {
                    _onFault?.Invoke(ex);
                }
                catch
                {
                    // Logging/observability must never terminate the per-URI send loop.
                }
                Report(completion, new DiagnosticPublicationResult(sequence, DiagnosticPublicationState.Faulted, ex));
            }
        }
    }

    private static void Report(Action<DiagnosticPublicationResult>? callback, DiagnosticPublicationResult result)
    {
        try
        {
            callback?.Invoke(result);
        }
        catch
        {
            // Recording must never influence the diagnostic publication pipeline.
        }
    }
}
