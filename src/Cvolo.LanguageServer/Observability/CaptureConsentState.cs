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
    private readonly Func<DateTimeOffset> _utcNow = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private HashSet<string>? _documents;
    private string? _leaseId;
    private DateTimeOffset? _expiresAt;
    private bool _allowSources;
    private bool _allowPaths;
    private bool _expired;

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
                ExistingArchivesErased: false);
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
    bool ExistingArchivesErased);

internal sealed record CaptureConsentAck(
    int ProtocolVersion,
    string Status,
    string? Reason,
    string? LeaseId,
    DateTimeOffset? ExpiresAtUtc,
    bool CaptureActive,
    bool CapturePipelineSupported);
