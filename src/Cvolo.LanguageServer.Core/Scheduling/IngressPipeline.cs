namespace Cvolo.LanguageServer.Core.Scheduling;

/// <summary>
/// Binds actual inbound JSON-RPC read order to the dispatch ordinal and
/// registers known document inputs BEFORE any handler can race a read request.
/// Payloads, URIs and arbitrary request text are never retained here.
/// </summary>
public sealed class IngressPipeline
{
    private const int MaxPendingFrames = 4096;
    private readonly object _gate = new();
    private readonly Queue<IngressSequence> _pending = new();
    private bool _pairingIncomplete;
    private long _read;
    private long _dispatched;

    public IngressSequencer Sequencer { get; } = new();
    public OrderedInputQueue Inputs { get; } = new();
    public Action<long, string>? DocumentIngressAdmitted { get; set; }

    public (long Read, long Dispatched, int Pending, bool Incomplete) PairingStatus
    {
        get
        {
            lock (_gate)
                return (_read, _dispatched, _pending.Count, _pairingIncomplete);
        }
    }

    /// <summary>
    /// Called once for each parsed inbound request/notification by the
    /// HeaderDelimited reader, before the StreamJsonRpc handler fan-out.
    /// Responses do not enter the inbound request dispatcher.
    /// </summary>
    public void ObserveInboundRpc(string? method, bool notification)
    {
        string? kind = notification ? method switch
        {
            "textDocument/didOpen" => "did_open",
            "textDocument/didChange" => "did_change",
            "textDocument/didClose" => "did_close",
            _ => null,
        } : null;

        IngressSequence sequence;
        lock (_gate)
        {
            sequence = Sequencer.Next();
            _read++;
            if (_pending.Count >= MaxPendingFrames)
            {
                _pending.Dequeue();
                _pairingIncomplete = true;
            }
            if (kind is not null)
                Inputs.Admit(sequence);
            _pending.Enqueue(sequence);
        }

        if (kind is not null)
        {
            try { DocumentIngressAdmitted?.Invoke(sequence.Value, kind); }
            catch (Exception) { /* Instrumentation never affects protocol. */ }
        }
    }

    public IngressSequence AssignDispatch()
    {
        lock (_gate)
        {
            _dispatched++;
            if (_pending.Count != 0)
                return _pending.Dequeue();
            // An unmatched callback gets a distinct identity, and the
            // evidence is explicitly incomplete rather than guessed.
            _pairingIncomplete = true;
            return Sequencer.Next();
        }
    }
