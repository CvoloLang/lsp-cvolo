using Cvolo.LanguageServer.Observability;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

public sealed class CaptureConsentStateTests
{
    private const string ServerId = "server-instance-A";
    private const string Document = "file:///C:/project/main.cvl";

    [Fact]
    public void Ttl_expiration_erases_scope_without_sleeping_or_granting_capture()
    {
        DateTimeOffset now = new(2026, 10, 22, 10, 0, 0, TimeSpan.Zero);
        var state = new CaptureConsentState(() => now);
        var accepted = state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1,
            ServerInstanceId = ServerId,
            UserConfirmed = true,
            CaptureSources = true,
            TtlSeconds = 10,
            DocumentUris = [Document],
        }, ServerId);
        Assert.Equal("registered_not_capturing", accepted.Status);
        Assert.True(state.Allows(Document, sources: true));
        Assert.False(state.Allows("file:///C:/project/other.cvl", sources: true));
        Assert.False(state.Allows(Document, sources: false));
        Assert.False(state.Status().CaptureActive);
        now = now.AddSeconds(11);
        Assert.False(state.Allows(Document, sources: true));
        Assert.Equal("expired", state.Status().Status);
        Assert.Null(state.Status().LeaseId);
        Assert.Equal(0, state.Status().ScopedDocumentCount);
    }

    [Fact]
    public void Invalid_scope_and_overlong_ttl_do_not_replace_a_valid_lease()
    {
        var state = new CaptureConsentState();
        CaptureConsentGrant grant = new()
        {
            ProtocolVersion = 1,
            ServerInstanceId = ServerId,
            UserConfirmed = true,
            CapturePaths = true,
            TtlSeconds = 60,
            DocumentUris = [Document],
        };
        var first = state.Grant(grant, ServerId);
        Assert.Equal("registered_not_capturing", first.Status);

        grant.DocumentUris = ["https://example.com/private.cvl"];
        Assert.Equal("invalid_consent_scope", state.Grant(grant, ServerId).Reason);
        grant.DocumentUris = [Document];
        grant.TtlSeconds = 901;
        Assert.Equal("invalid_consent_ttl", state.Grant(grant, ServerId).Reason);
        Assert.Equal(first.LeaseId, state.Status().LeaseId);
        Assert.True(state.Allows(Document, sources: false));
        Assert.False(state.Allows(Document, sources: true));
    }

    [Fact]
    public void Revoke_is_idempotent_and_clears_source_and_path_scope()
    {
        var state = new CaptureConsentState();
        state.Grant(new CaptureConsentGrant
        {
            ProtocolVersion = 1,
            ServerInstanceId = ServerId,
            UserConfirmed = true,
            CaptureSources = true,
            CapturePaths = true,
            TtlSeconds = 100,
            DocumentUris = [Document],
        }, ServerId);
        Assert.True(state.Allows(Document, sources: true));
        Assert.True(state.Allows(Document, sources: false));

        var wrongServer = state.Revoke(new CaptureConsentRevoke
        {
            ProtocolVersion = 1, ServerInstanceId = "other"
        }, ServerId);
        Assert.Equal("rejected", wrongServer.Status);
        Assert.True(state.Allows(Document, sources: true));

        for (int i = 0; i < 2; i++)
        {
            var response = state.Revoke(new CaptureConsentRevoke
            {
                ProtocolVersion = 1, ServerInstanceId = ServerId
            }, ServerId);
            Assert.Equal("revoked", response.Status);
            Assert.False(state.Allows(Document, sources: true));
            Assert.False(state.Allows(Document, sources: false));
        }
    }
}
