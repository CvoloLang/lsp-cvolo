using Cvolo.LanguageServer.Tests.TestSupport;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>Real JSON-RPC tests of the initial versioned recording control protocol.</summary>
public sealed class RecordingControlPlaneTests
{
    private static (CompilerObserver Observer, BoundedEventQueue Queue) CreateObserver()
    {
        var options = new ObservabilityOptions
        {
            Mode = ObservabilityMode.Off,
            Emitter = "recording-control-tests",
            QueueCapacity = 20_000,
        };
        var queue = new BoundedEventQueue(options.QueueCapacity);
        var observer = new CompilerObserver(
            options,
            queue: queue,
            catalog: EventCatalog.CreateKernelCatalog());
        return (observer, queue);
    }

    [Fact]
    public async Task Start_is_acknowledged_with_actual_server_identity_and_epoch()
    {
        var (observer, queue) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync();

        JObject initial = await session.Client.GetRecordingStatusAsync();
        Assert.True((bool)initial["supported"]!);
        Assert.Equal("off", (string?)initial["effectiveMode"]);
        Assert.Equal(0, (long)initial["recordingEpoch"]!);

        JObject accepted = await session.Client.SetRecordingPolicyAsync("detailed");
        Assert.Equal("applied", (string?)accepted["status"]);
        Assert.Equal("detailed", (string?)accepted["requestedMode"]);
        Assert.Equal("detailed", (string?)accepted["effectiveMode"]);
        Assert.Equal(1, (long)accepted["recordingEpoch"]!);
        Assert.Equal(observer.Identity.ServerInstanceId, (string?)accepted["serverInstanceId"]);
        Assert.Equal(ObservabilityMode.Detailed, observer.Mode);
        Assert.NotNull(accepted["appliedAtUtc"]);
        Assert.Null(accepted["effectiveFromSequence"]?.Type == JTokenType.Null
            ? null : accepted["effectiveFromSequence"]);

        var trace = new List<StampedEvent>();
        queue.DrainAll(trace);
        Assert.Contains(trace, x =>
            x.Event.Event == "recording.mode_changed" && x.RecordingEpoch == 1);
        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog.MissingFields);
        // setRecording was received while Off, so its response must not become
        // an orphan rpc.response.sent without requestUid.
        Assert.DoesNotContain(trace, x => x.Event.Event == "rpc.response.sent"
            && string.IsNullOrEmpty(x.Event.RequestUid));

        // The next request is genuinely observed on both sides of the RPC
        // boundary, with the same non-fabricated server-scoped identity.
        await session.Client.TryUnknownRequestAsync();
        var afterStart = new List<StampedEvent>();
        queue.DrainAll(afterStart);
        var inbound = Assert.Single(afterStart, x =>
            x.Event.Event == "rpc.request.received"
            && x.Event.Attributes?.Any(a => a.Key == "method" && a.Value == "cvolo/unknownRequest") == true);
        var outbound = Assert.Single(afterStart, x => x.Event.Event == "rpc.response.sent");
        Assert.NotNull(inbound.Event.RequestUid);
        Assert.Equal(inbound.Event.RequestUid, outbound.Event.RequestUid);
        Assert.Equal(0, observer.Catalog.MissingFields);
    }

    [Fact]
    public async Task Stop_disables_new_request_recording_and_acknowledges_next_epoch()
    {
        var (observer, queue) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync();
        var started = await session.Client.SetRecordingPolicyAsync("basic");
        Assert.Equal("applied", (string?)started["status"]);
        Assert.Equal(1, (long)started["recordingEpoch"]!);

        var stopped = await session.Client.SetRecordingPolicyAsync("off");
        Assert.Equal("applied", (string?)stopped["status"]);
        Assert.Equal("off", (string?)stopped["effectiveMode"]);
        Assert.Equal(2, (long)stopped["recordingEpoch"]!);
        Assert.Equal(ObservabilityMode.Off, observer.Mode);

        var events = new List<StampedEvent>();
        queue.DrainAll(events);
        Assert.Contains(events, x =>
            x.Event.Event == "recording.mode_changed" && x.RecordingEpoch == 2);

        await session.Client.TryUnknownRequestAsync();
        events.Clear();
        queue.DrainAll(events);
        Assert.DoesNotContain(events, x => x.Event.Event == "rpc.request.received"
            && x.Event.Attributes?.Any(a =>
                a.Key == "method" && a.Value == "cvolo/unknownRequest") == true);
    }

    [Fact]
    public async Task Consent_and_unsupported_mode_are_rejected_without_changing_epoch()
    {
        var (observer, _) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync();

        JObject source = await session.Client.SetRecordingPolicyAsync("detailed", captureSources: true);
        Assert.Equal("rejected", (string?)source["status"]);
        Assert.Equal("consent_required", (string?)source["reason"]);

        JObject forensic = await session.Client.SetRecordingPolicyAsync("repro");
        Assert.Equal("rejected", (string?)forensic["status"]);
        Assert.Equal("forensic_consent_required", (string?)forensic["reason"]);

        JObject invalidVersion = await session.Client.SetRecordingPolicyAsync("basic", protocolVersion: 2);
        Assert.Equal("rejected", (string?)invalidVersion["status"]);
        Assert.Equal("unsupported_protocol_version", (string?)invalidVersion["reason"]);

        JObject current = await session.Client.GetRecordingStatusAsync();
        Assert.Equal("off", (string?)current["effectiveMode"]);
        Assert.Equal(0, (long)current["recordingEpoch"]!);
        Assert.Equal(ObservabilityMode.Off, observer.Mode);
    }

    [Fact]
    public async Task Noop_observer_cannot_report_a_successful_start()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync();

        JObject current = await session.Client.GetRecordingStatusAsync();
        Assert.False((bool)current["supported"]!);

        JObject response = await session.Client.SetRecordingPolicyAsync("detailed");
        Assert.Equal("rejected", (string?)response["status"]);
        Assert.Equal("observer_unavailable", (string?)response["reason"]);
        Assert.Equal("off", (string?)response["effectiveMode"]);
    }
}
