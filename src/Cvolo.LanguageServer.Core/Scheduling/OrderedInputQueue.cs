namespace Cvolo.LanguageServer.Core.Scheduling;

/// <summary>Result of applying a single admitted input frame.</summary>
public enum InputDisposition
{
    /// <summary>The input was applied to the document session.</summary>
    Applied,

    /// <summary>The input was definitively rejected (stale version, wrong language, closed session, ...).</summary>
    Rejected,
}

/// <summary>Outcome of waiting at the input read barrier.</summary>
public enum BarrierOutcome
{
    /// <summary>Every earlier admitted input was applied or rejected before the timeout.</summary>
    Satisfied,

    /// <summary>The barrier timed out; the caller must answer with a recoverable non-success.</summary>
    TimedOut,

    /// <summary>The queue completed (server shutting down) before the barrier could be satisfied.</summary>
    Completed,
}

/// <summary>Point-in-time counters for the ordered input queue.</summary>
public readonly record struct InputQueueMetrics(
    long Admitted,
    long Applied,
    long Rejected,
    int Queued,
    int MaxQueued,
    long BarrierWaits,
    long BarrierTimeouts);

/// <summary>
/// The single serialized input-writer order for document notifications (INC "LSP
/// Request Scheduling"). Input frames are admitted in receive order and applied
/// strictly in ascending <see cref="IngressSequence"/> order, so
/// didOpen/didChange/didClose never overtake each other. The handler that attaches
/// the earliest outstanding input applies it (and any already-attached successors)
/// on its own thread, so the common in-order case stays synchronous; an input whose
/// handler runs before an earlier one is deferred until the earlier one attaches.
/// Read requests wait at <see cref="WaitForInputsBeforeAsync"/> until every earlier
/// admitted input has been applied or definitively rejected (publication, never
/// semantic readiness).
/// </summary>
public sealed class OrderedInputQueue : IInputBarrier
{
    private readonly object _gate = new();
    private readonly SortedDictionary<long, Entry> _entries = new();
    private readonly List<Waiter> _waiters = new();
    private bool _applying;
    private bool _completed;

    private long _admitted;
    private long _applied;
    private long _rejected;
    private long _lastSettled;
    private int _maxQueued;
    private long _barrierWaits;
    private long _barrierTimeouts;

    private sealed class Entry
    {
        public Func<InputDisposition>? Apply;
        public bool Attached;
        public bool Settled;
    }

    private sealed class Waiter
    {
        public required long ExclusiveTarget;
        public required TaskCompletionSource<BarrierOutcome> Completion;
        public CancellationTokenRegistration Registration;
    }

    /// <summary>The highest sequence that has settled so far.</summary>
    public IngressSequence SettledThrough
    {
        get
        {
            lock (_gate)
            {
                return new IngressSequence(_lastSettled);
            }
        }
    }

    /// <summary>Point-in-time counters.</summary>
    public InputQueueMetrics Metrics
    {
        get
        {
            lock (_gate)
            {
                return new InputQueueMetrics(_admitted, _applied, _rejected, _entries.Count, _maxQueued, _barrierWaits, _barrierTimeouts);
            }
        }
    }

    /// <summary>
    /// Registers an admitted input frame at the frame-admission boundary, before
    /// its handler body runs, so a later request's barrier can observe it even if
    /// its handler has not executed yet.
    /// </summary>
    public void Admit(IngressSequence sequence)
    {
        lock (_gate)
        {
            if (_completed || sequence.IsNone)
            {
                return;
            }

            if (!_entries.ContainsKey(sequence.Value))
            {
                _entries[sequence.Value] = new Entry();
                _admitted++;
                _maxQueued = Math.Max(_maxQueued, _entries.Count);
            }
        }
    }

    /// <summary>
    /// Supplies the application function for an admitted input and applies the
    /// earliest outstanding attached input on the calling thread. If an earlier
    /// input has not been attached yet, the application is deferred until that
    /// earlier handler attaches.
    /// </summary>
    public void Attach(IngressSequence sequence, Func<InputDisposition> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        lock (_gate)
        {
            if (_completed || sequence.IsNone)
            {
                return;
            }

            if (!_entries.TryGetValue(sequence.Value, out Entry? entry))
            {
                entry = new Entry();
                _entries[sequence.Value] = entry;
                _admitted++;
                _maxQueued = Math.Max(_maxQueued, _entries.Count);
            }

            entry.Apply = apply;
            entry.Attached = true;
        }

        ApplyAttachedHead();
    }

    /// <summary>Marks an admitted input settled without applying it (for example, an ignored frame).</summary>
    public void Settle(IngressSequence sequence, InputDisposition disposition)
    {
        lock (_gate)
        {
            if (_completed || sequence.IsNone)
            {
                return;
            }

            if (_entries.TryGetValue(sequence.Value, out Entry? entry) && !entry.Settled)
            {
                entry.Apply = null;
                entry.Settled = true;
                _entries.Remove(sequence.Value);
                Count(disposition);
                _lastSettled = Math.Max(_lastSettled, sequence.Value);
                ReleaseSatisfiedWaiters();
            }
        }
    }

    /// <summary>
    /// Waits until every admitted input with a sequence strictly earlier than
    /// <paramref name="requestSequence"/> has been applied or rejected. Waits for
    /// input publication only, never for backend semantic readiness.
    /// </summary>
    public Task<BarrierOutcome> WaitForInputsBeforeAsync(IngressSequence requestSequence, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _barrierWaits++;
            if (_completed)
            {
                return Task.FromResult(BarrierOutcome.Completed);
            }

            if (IsSatisfied(requestSequence.Value))
            {
                return Task.FromResult(BarrierOutcome.Satisfied);
            }

            var waiter = new Waiter
            {
                ExclusiveTarget = requestSequence.Value,
                Completion = new TaskCompletionSource<BarrierOutcome>(TaskCreationOptions.RunContinuationsAsynchronously),
            };

            if (timeout != Timeout.InfiniteTimeSpan)
            {
                _ = Task.Delay(timeout, CancellationToken.None).ContinueWith(
                    _ => TimeoutWaiter(waiter),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(state => CancelWaiter((Waiter)state!), waiter, useSynchronizationContext: false);
            }

            _waiters.Add(waiter);
            return waiter.Completion.Task;
        }
    }

    /// <summary>Stops the queue and completes all outstanding barriers with <see cref="BarrierOutcome.Completed"/>.</summary>
    public void Complete()
    {
        List<Waiter> waiters;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            waiters = new List<Waiter>(_waiters);
            _waiters.Clear();
        }

        foreach (Waiter waiter in waiters)
        {
            waiter.Registration.Dispose();
            waiter.Completion.TrySetResult(BarrierOutcome.Completed);
        }
    }

    private void TimeoutWaiter(Waiter waiter)
    {
        lock (_gate)
        {
            if (!_waiters.Remove(waiter))
            {
                return;
            }

            _barrierTimeouts++;
        }

        waiter.Registration.Dispose();
        waiter.Completion.TrySetResult(BarrierOutcome.TimedOut);
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_gate)
        {
            if (!_waiters.Remove(waiter))
            {
                return;
            }
        }

        waiter.Completion.TrySetResult(BarrierOutcome.Completed);
    }

    private void ApplyAttachedHead()
    {
        lock (_gate)
        {
            if (_applying || _completed)
            {
                return;
            }

            _applying = true;
        }

        try
        {
            while (true)
            {
                Func<InputDisposition>? apply;
                long sequence;
                lock (_gate)
                {
                    if (_completed || _entries.Count == 0)
                    {
                        break;
                    }

                    KeyValuePair<long, Entry> head = _entries.First();
                    if (!head.Value.Attached)
                    {
                        break;
                    }

                    sequence = head.Key;
                    apply = head.Value.Apply;
                    head.Value.Apply = null;
                    head.Value.Attached = false;
                }

                InputDisposition disposition;
                try
                {
                    disposition = apply?.Invoke() ?? InputDisposition.Rejected;
                }
                catch (Exception)
                {
                    disposition = InputDisposition.Rejected;
                }

                lock (_gate)
                {
                    if (_entries.TryGetValue(sequence, out Entry? entry) && !entry.Settled)
                    {
                        entry.Settled = true;
                        _entries.Remove(sequence);
                        Count(disposition);
                        _lastSettled = Math.Max(_lastSettled, sequence);
                        ReleaseSatisfiedWaiters();
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _applying = false;
            }
        }
    }

    private bool IsSatisfied(long exclusiveTarget)
    {
        foreach (KeyValuePair<long, Entry> pair in _entries)
        {
            if (pair.Key >= exclusiveTarget)
            {
                break;
            }

            if (!pair.Value.Settled)
            {
                return false;
            }
        }

        return true;
    }

    private void ReleaseSatisfiedWaiters()
    {
        if (_waiters.Count == 0)
        {
            return;
        }

        var stillWaiting = new List<Waiter>(_waiters.Count);
        foreach (Waiter waiter in _waiters)
        {
            if (IsSatisfied(waiter.ExclusiveTarget))
            {
                waiter.Registration.Dispose();
                waiter.Completion.TrySetResult(BarrierOutcome.Satisfied);
            }
            else
            {
                stillWaiting.Add(waiter);
            }
        }

        _waiters.Clear();
        _waiters.AddRange(stillWaiting);
    }

    private void Count(InputDisposition disposition)
    {
        if (disposition == InputDisposition.Applied)
        {
            _applied++;
        }
        else
        {
            _rejected++;
        }
    }
}
