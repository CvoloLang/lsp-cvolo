using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// One anchored action the server has vouched for. It carries exactly the fences the server can
/// actually verify and nothing it cannot: the captured coherent snapshot of the open document, the
/// kind of lens, the anchor offset into that snapshot's text, and whether the record names an action
/// the client may now perform. The document session, version and project snapshot live inside the
/// captured <see cref="SemanticRequestContext"/>; no identity the server does not have is invented
/// (§21, §39).
/// </summary>
internal sealed record AnchoredActionRecord(
    DocumentUri Uri,
    BackendCodeLensKind Kind,
    int AnchorOffset,
    SemanticRequestContext Context,
    bool IsAction);

/// <summary>
/// Session-scoped, bounded store of opaque anchored-lens tokens. A token is a random handle with no
/// meaning on its own: everything a click needs is kept here, on the server, and is revalidated
/// against the live document store at the moment it is used. A lens token may be used more than once
/// while it stays current — the editor may re-render the same clickable lens — but it stops resolving
/// the instant the document, session or project it was minted from is no longer current. An action
/// token is consumed on validation, so a performed action can never be replayed (§21, §39).
/// </summary>
internal sealed class AnchoredActionRegistry
{
    private const int MaxEntries = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, AnchoredActionRecord> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>Stores a record and returns the opaque token that names it.</summary>
    public string Mint(AnchoredActionRecord record)
    {
        string token = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _entries[token] = record;
            _order.Enqueue(token);
            while (_entries.Count > MaxEntries && _order.TryDequeue(out string? oldest))
            {
                _entries.Remove(oldest);
            }
        }

        return token;
    }

    /// <summary>Looks a record up without consuming it. Lens tokens are read this way.</summary>
    public bool TryGet(string token, out AnchoredActionRecord record)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(token, out record!);
        }
    }

    /// <summary>Looks a record up and removes it, so the token cannot be used a second time.</summary>
    public bool TryConsume(string token, out AnchoredActionRecord record)
    {
        lock (_gate)
        {
            return _entries.Remove(token, out record!);
        }
    }

    /// <summary>Drops every token, called on shutdown.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
        }
    }
}
