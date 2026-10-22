namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// In-memory, process-scoped intent to permit narrowly scoped sensitive capture.
/// Registering a lease is not activation: the current LSP deliberately has NO
/// source/path capture pipeline, and this object never reads or writes files.
/// Document URIs are retained only in memory and never returned over RPC or
/// emitted as observability events. Revoke removes them immediately.
/// </summary>
internal sealed class CaptureConsentState(Func<DateTimeOffset>? clock = null)
{
    internal const int Version = 1;
    internal const int MaxTtlSeconds = 900;
    internal const int MaxDocuments = 32;
    internal const int MaxJournalSnapshots = 64;
    internal const int MaxSnapshotChars = 256 * 1024;
    internal const int MaxJournalChars = 1024 * 1024;
    private readonly Func<DateTimeOffset> _utcNow = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private HashSet<string>? _documents;
    private string? _leaseId;
    private DateTimeOffset? _expiresAt;
    private bool _allowSources;
    private bool _allowPaths;
    private bool _expired;
    private bool _journalArmed;
    private bool _journalIncomplete;
    private int _journalChars;
    private readonly List<CapturedDocumentSnapshot> _journal = [];

    internal CaptureConsentStatus Status()
    {
        lock (_gate)
        {
            ExpireIfNeeded();
            return new(
                Version,
                _documents is null ? (_expired ? "expired" : "none") : "registered_not_capturing",
                _leaseId,
                _documents?.Count ?? 0,
                _expiresAt,
                _documents is not null && _allowSources,
                _documents is not null && _allowPaths,
                CaptureActive: false,
                CapturePipelineSupported: false,
                ScopeVerification: "uri_syntax_only",
                ExistingArchivesErased: false,
                JournalActive: _journalArmed,
                CapturedSnapshots: _journal.Count,
                CapturedCharacters: _journalChars,
                JournalIncomplete: _journalIncomplete,
                InMemoryJournalSupported: true,
                SourceExportSupported: false);
        }
    }

    internal CaptureConsentAck Grant(CaptureConsentGrant? request, string? serverInstanceId)
    {
        lock (_gate)
        {
            ExpireIfNeeded();
            CaptureConsentAck Reject(string reason) =>
                new(Version, "rejected", reason, null, _expiresAt,
                    CaptureActive: false, CapturePipelineSupported: false);

            if (request is null || request.ProtocolVersion != Version)
                return Reject("unsupported_protocol_version");
            if (string.IsNullOrEmpty(serverInstanceId) ||
                !string.Equals(request.ServerInstanceId, serverInstanceId, StringComparison.Ordinal))
                return Reject("server_instance_mismatch");
            if (!request.UserConfirmed)
                return Reject("explicit_user_confirmation_required");
            if (!request.CaptureSources && !request.CapturePaths)
                return Reject("empty_capture_permissions");
            if (request.TtlSeconds is < 10 or > MaxTtlSeconds)
                return Reject("invalid_consent_ttl");
            if (request.DocumentUris is null || request.DocumentUris.Length is < 1 or > MaxDocuments)
                return Reject("invalid_consent_scope");

            var proposed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in request.DocumentUris)
            {
                if (string.IsNullOrEmpty(name) || name.Length > 2048
                    || !Uri.TryCreate(name, UriKind.Absolute, out var uri)
                    || !uri.IsFile || uri.IsUnc
                    || uri.Fragment.Length != 0 || uri.Query.Length != 0
                    || uri.LocalPath.Length == 0)
                    return Reject("invalid_consent_scope");

                proposed.Add(uri.AbsoluteUri);
            }

            if (proposed.Count == 0)
                return Reject("invalid_consent_scope");

            // Failed validation never replaces an existing valid lease.
            // A new grant cannot inherit sensitive text from an earlier scope.
            Clear();
            _documents = proposed;
            _allowSources = request.CaptureSources;
            _allowPaths = request.CapturePaths;
            _expired = false;
            _leaseId = "consent-" + Guid.NewGuid().ToString("N");
            _expiresAt = _utcNow().AddSeconds(request.TtlSeconds);
            return new(Version, "registered_not_capturing", null, _leaseId, _expiresAt,
                CaptureActive: false, CapturePipelineSupported: false);
        }
    }

    internal CaptureConsentAck Revoke(CaptureConsentRevoke? request, string? serverInstanceId)
    {
        lock (_gate)
        {
            if (request is null || request.ProtocolVersion != Version)
                return new(Version, "rejected", "unsupported_protocol_version", null, null, false, false);
            if (string.IsNullOrEmpty(serverInstanceId) ||
                !string.Equals(request.ServerInstanceId, serverInstanceId, StringComparison.Ordinal))
                return new(Version, "rejected", "server_instance_mismatch", null, null, false, false);

            Clear();
            _expired = false;
            return new(Version, "revoked", null, null, null,
                CaptureActive: false, CapturePipelineSupported: false);
        }
    }

    /// <summary>
    /// Explicitly arms an in-memory snapshot journal for this lease. It does
    /// not enable repro mode or persist text. Existing pre-arm history is
    /// unknown, and an exportable replay manifest is not yet available.
    /// </summary>
    internal CaptureJournalAck Arm(CaptureJournalArm? request, string? serverInstanceId)
    {
        lock (_gate)
        {
            ExpireIfNeeded();
            CaptureJournalAck Reject(string reason) => new(
                Version, "rejected", reason, false, _journal.Count,
                _journalChars, _journalIncomplete, false);

            if (request is null || request.ProtocolVersion != Version)
                return Reject("unsupported_protocol_version");
            if (string.IsNullOrEmpty(serverInstanceId)
                || !string.Equals(request.ServerInstanceId, serverInstanceId, StringComparison.Ordinal))
                return Reject("server_instance_mismatch");
            if (_documents is null || _leaseId is null)
                return Reject("consent_not_active");
            if (!string.Equals(request.LeaseId, _leaseId, StringComparison.Ordinal))
                return Reject("consent_lease_mismatch");
            if (!_allowSources)
                return Reject("source_permission_required");

            if (!_journalArmed)
            {
                _journal.Clear();
                _journalChars = 0;
                // We cannot reconstruct pre-arm document changes. The future
                // replay verifier must see this unknown prefix explicitly.
                _journalIncomplete = true;
                _journalArmed = true;
            }
            return new(Version, "armed_in_memory", null, true, _journal.Count,
                _journalChars, _journalIncomplete, false);
        }
    }

    /// <summary>
    /// Called only after a didOpen or didChange is accepted by DocumentStore.
    /// Consent, scope, TTL and byte limits are all checked atomically against
    /// revoke before retaining even a reference to the source string.
    /// </summary>
    internal void CaptureCommitted(Uri? uri, int version, string? sourceText)
    {
        if (uri is null || sourceText is null)
            return;
        lock (_gate)
        {
            ExpireIfNeeded();
            if (!_journalArmed || !_allowSources || _documents is null
                || !uri.IsAbsoluteUri || !uri.IsFile
                || !_documents.Contains(uri.AbsoluteUri))
                return;

            // Retain only bounded already-open editor state. Never open a
            // workspace file from disk or copy content into telemetry events.
            if (sourceText.Length > MaxSnapshotChars
                || _journal.Count >= MaxJournalSnapshots
                || sourceText.Length > MaxJournalChars - _journalChars)
            {
                _journalIncomplete = true;
                return;
            }

            CapturedDocumentSnapshot? previous = null;
            for (int i = _journal.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_journal[i].DocumentUri, uri.AbsoluteUri,
                    StringComparison.Ordinal))
                {
                    previous = _journal[i];
                    break;
                }
            }
            if (previous is not null && version <= previous.Version)
            {
                if (version < previous.Version)
                    _journalIncomplete = true;
                return;
            }

            _journal.Add(new(uri.AbsoluteUri, version, sourceText));
            _journalChars += sourceText.Length;
        }
    }

    /// <summary>Internal verifier access; never exposed by a JSON-RPC method.</summary>
    internal IReadOnlyList<CapturedDocumentSnapshot> CopyJournalForVerification()
    {
        lock (_gate)
        {
            ExpireIfNeeded();
            return _journal.ToArray();
        }
    }

    internal bool Allows(string documentUri, bool sources)
    {
        lock (_gate)
        {
            ExpireIfNeeded();
            if (_documents is null ||
                !Uri.TryCreate(documentUri, UriKind.Absolute, out var uri))
                return false;
            return (sources ? _allowSources : _allowPaths) &&
                _documents.Contains(uri.AbsoluteUri);
        }
    }

    private void ExpireIfNeeded()
    {
        if (_expiresAt is { } end && _utcNow() >= end)
        {
            Clear();
            _expired = true;
        }
    }

    private void Clear()
    {
        _journal.Clear();
        _journalChars = 0;
        _journalArmed = false;
        _journalIncomplete = false;
        _documents?.Clear();
        _documents = null;
        _leaseId = null;
        _expiresAt = null;
        _allowPaths = false;
        _allowSources = false;
    }
}

internal sealed class CaptureConsentGrant
{
    public int ProtocolVersion { get; set; }
    public string? ServerInstanceId { get; set; }
    public bool UserConfirmed { get; set; }
    public bool CaptureSources { get; set; }
    public bool CapturePaths { get; set; }
    public int TtlSeconds { get; set; }
    public string[]? DocumentUris { get; set; }
}

internal sealed class CaptureConsentRevoke
{
    public int ProtocolVersion { get; set; }
    public string? ServerInstanceId { get; set; }
}

internal sealed record CaptureConsentStatus(
    int ProtocolVersion,
    string Status,
    string? LeaseId,
    int ScopedDocumentCount,
    DateTimeOffset? ExpiresAtUtc,
    bool SourcePermissionRequested,
    bool PathPermissionRequested,
    bool CaptureActive,
    bool CapturePipelineSupported,
    string ScopeVerification,
    bool ExistingArchivesErased,
    bool JournalActive,
    int CapturedSnapshots,
    int CapturedCharacters,
    bool JournalIncomplete,
    bool InMemoryJournalSupported,
    bool SourceExportSupported);

internal sealed record CaptureConsentAck(
    int ProtocolVersion,
    string Status,
    string? Reason,
    string? LeaseId,
    DateTimeOffset? ExpiresAtUtc,
    bool CaptureActive,
    bool CapturePipelineSupported);

internal sealed class CaptureJournalArm
{
    public int ProtocolVersion { get; set; }
    public string? ServerInstanceId { get; set; }
    public string? LeaseId { get; set; }
}

internal sealed record CaptureJournalAck(
    int ProtocolVersion,
    string Status,
    string? Reason,
    bool JournalActive,
    int CapturedSnapshots,
    int CapturedCharacters,
    bool JournalIncomplete,
    bool SourceExportSupported);

/// <summary>Private in-memory source; never serialized by the RPC layer.</summary>
internal sealed record CapturedDocumentSnapshot(
    string DocumentUri, int Version, string Text);
