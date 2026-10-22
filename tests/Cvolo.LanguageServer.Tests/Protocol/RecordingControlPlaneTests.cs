using Cvolo.LanguageServer.Tests.TestSupport;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Cvolo.LanguageServer.Observability;
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
    public async Task Off_to_detailed_creates_a_real_JSONL_writer_and_stop_drains_its_events()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cvolo-obs-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "trace.jsonl");
        try
        {
            using var recording = LspObservabilitySession.StartForTesting(file);
            Assert.False(File.Exists(file));
            using var server = ProtocolSession.Start(observer: recording.Observer, recordingSession: recording);
            await server.Client.InitializeAsync();

            JObject start = await server.Client.SetRecordingPolicyAsync("detailed");
            Assert.Equal("applied", (string?)start["status"]);
            Assert.Equal("active", (string?)start["writerState"]);
            Assert.True((bool)start["diskRecordingActive"]!);
            Assert.Equal(1, (long)start["recordingEpoch"]!);
            Assert.True(File.Exists(file));

            await server.Client.TryUnknownRequestAsync();
            JObject stop = await server.Client.SetRecordingPolicyAsync("off");
            Assert.Equal("applied", (string?)stop["status"]);
            Assert.Equal("off", (string?)stop["effectiveMode"]);
            Assert.Equal(2, (long)stop["recordingEpoch"]!);
            Assert.Equal("idle", (string?)stop["writerState"]);
            Assert.False((bool)stop["diskRecordingActive"]!);
            Assert.False((bool)stop["replayIncomplete"]!);
            Assert.True((long)stop["lastDurableProducerSequence"]! >= 0);

            JObject checkpoint = await server.Client.GetRecordingCheckpointAsync();
            Assert.Equal("idle", (string?)checkpoint["writerState"]);
            Assert.True((bool)checkpoint["closedForInspection"]!);
            Assert.False((bool)checkpoint["replayIncomplete"]!);
            Assert.Empty((JArray)checkpoint["incompleteReasons"]!);
            Assert.Equal(2, (long)checkpoint["recordingEpoch"]!);
            Assert.True((long)checkpoint["lastDurableProducerSequence"]! >= 0);
            Assert.True((long)checkpoint["writtenLines"]! > 0);
            Assert.Equal(0, (long)checkpoint["droppedP0"]!);
            Assert.Equal(0, (long)checkpoint["droppedP1"]!);
            Assert.Equal(0, (long)checkpoint["droppedP2"]!);
            Assert.Equal(0, (long)checkpoint["writerLost"]!);
            Assert.True((bool)checkpoint["exportSupported"]!);

            JObject archive = await server.Client.ExportRecordingAsync(
                (string)checkpoint["serverInstanceId"]!, (long)checkpoint["recordingEpoch"]!);
            Assert.Equal("exported", (string?)archive["status"]);
            Assert.True((int)archive["segmentCount"]! >= 1);
            Assert.False((bool)archive["completenessVerified"]!);
            string archivePath = (string)archive["archivePath"]!;
            Assert.True(File.Exists(archivePath));

            // Independently verify that the manifest hashes the exact
            // uncompressed data actually packaged, not merely the live log.
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var manifestEntry = Assert.Single(zip.Entries, x => x.FullName == "manifest.json");
                byte[] manifestBytes;
                using (Stream input = manifestEntry.Open())
                using (var buffer = new MemoryStream())
                {
                    input.CopyTo(buffer);
                    manifestBytes = buffer.ToArray();
                }
                Assert.Equal(
                    Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                    (string?)archive["manifestSha256"]);
                var manifest = JObject.Parse(Encoding.UTF8.GetString(manifestBytes));
                Assert.Equal("cvolo.diagnostic-bundle", (string?)manifest["format"]);
                Assert.False((bool)manifest["completenessVerified"]!);
                Assert.False((bool)manifest["privacyAuditVerified"]!);
                Assert.Equal((long)checkpoint["recordingEpoch"]!,
                    (long)manifest["checkpoint"]!["recordingEpoch"]!);

                var segmentList = (JArray)manifest["segments"]!;
                Assert.Equal((int)archive["segmentCount"]!, segmentList.Count);
                foreach (var segment in segmentList)
                {
                    string entryName = (string)segment["entry"]!;
                    Assert.StartsWith("events/segment-", entryName);
                    var logEntry = Assert.Single(zip.Entries, e => e.FullName == entryName);
                    using Stream data = logEntry.Open();
                    using var bytes = new MemoryStream();
                    data.CopyTo(bytes);
                    Assert.Equal((long)segment["sizeBytes"]!, bytes.Length);
                    Assert.Equal(
                        (string?)segment["sha256"],
                        Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
                }
            }

            JObject duplicate = await server.Client.ExportRecordingAsync(
                (string)checkpoint["serverInstanceId"]!, (long)checkpoint["recordingEpoch"]! + 1);
            Assert.Equal("rejected", (string?)duplicate["status"]);
            Assert.Equal("recording_epoch_mismatch", (string?)duplicate["reason"]);
            Assert.True(checkpoint["producerBarrierSequence"] is null or { Type: JTokenType.Null });

            string jsonl = File.ReadAllText(file);
            Assert.Contains("recording.mode_changed", jsonl, StringComparison.Ordinal);
            Assert.Contains("rpc.request.received", jsonl, StringComparison.Ordinal);

            // A successful Stop must release the writer's Windows file handle.
            // Starting a second epoch must append through a NEW writer, not
            // reuse the disposed one and silently discard subsequent events.
            JObject restarted = await server.Client.SetRecordingPolicyAsync("basic");
            Assert.Equal("applied", (string?)restarted["status"]);
            Assert.Equal("active", (string?)restarted["writerState"]);
            Assert.Equal(3, (long)restarted["recordingEpoch"]!);
            await server.Client.TryUnknownRequestAsync();

            JObject stoppedAgain = await server.Client.SetRecordingPolicyAsync("off");
            Assert.Equal("applied", (string?)stoppedAgain["status"]);
            Assert.Equal(4, (long)stoppedAgain["recordingEpoch"]!);
            Assert.Equal("idle", (string?)stoppedAgain["writerState"]);
            Assert.False((bool)stoppedAgain["replayIncomplete"]!);

            JObject afterRestartCheckpoint = await server.Client.GetRecordingCheckpointAsync();
            Assert.Equal(4, (long)afterRestartCheckpoint["recordingEpoch"]!);
            Assert.True((long)afterRestartCheckpoint["writtenLines"]! >
                (long)checkpoint["writtenLines"]!);
            Assert.True((long)afterRestartCheckpoint["lastDurableProducerSequence"]! >=
                (long)checkpoint["lastDurableProducerSequence"]!);
            Assert.True((bool)afterRestartCheckpoint["closedForInspection"]!);

            string afterRestart = File.ReadAllText(file);
            Assert.True(afterRestart.Length > jsonl.Length);
            Assert.Contains("rpc.request.received", afterRestart, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Export_is_rejected_while_recording_is_active_or_has_no_disk_sink()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cvolo-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var recording = LspObservabilitySession.StartForTesting(
                Path.Combine(directory, "trace.jsonl"));
            using var server = ProtocolSession.Start(
                observer: recording.Observer, recordingSession: recording);
            await server.Client.InitializeAsync();

            JObject before = await server.Client.GetRecordingCheckpointAsync();
            JObject beforeExport = await server.Client.ExportRecordingAsync(
                (string)before["serverInstanceId"]!, (long)before["recordingEpoch"]!);
            Assert.Equal("rejected", (string?)beforeExport["status"]);
            Assert.Equal("writer_not_closed", (string?)beforeExport["reason"]);

            JObject start = await server.Client.SetRecordingPolicyAsync("detailed");
            Assert.Equal("applied", (string?)start["status"]);
            JObject during = await server.Client.GetRecordingCheckpointAsync();
            JObject unsafeExport = await server.Client.ExportRecordingAsync(
                (string)during["serverInstanceId"]!, (long)during["recordingEpoch"]!);
            Assert.Equal("rejected", (string?)unsafeExport["status"]);
            Assert.Equal("recording_active", (string?)unsafeExport["reason"]);

            JObject wrongSession = await server.Client.ExportRecordingAsync(
                "wrong-server-instance", (long)during["recordingEpoch"]!);
            Assert.Equal("rejected", (string?)wrongSession["status"]);
            Assert.Equal("server_instance_mismatch", (string?)wrongSession["reason"]);

            Assert.Empty(Directory.GetFiles(directory, "*.zip"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }

        var (observer, _) = CreateObserver();
        using var collector = ProtocolSession.Start(observer: observer);
        await collector.Client.InitializeAsync();
        JObject status = await collector.Client.GetRecordingCheckpointAsync();
        JObject rejected = await collector.Client.ExportRecordingAsync(
            (string)status["serverInstanceId"]!, (long)status["recordingEpoch"]!);
        Assert.Equal("rejected", (string?)rejected["status"]);
        Assert.Equal("no_disk_sink", (string?)rejected["reason"]);
    }

    [Fact]
    public async Task Writer_open_failure_rejects_start_without_changing_the_effective_epoch()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cvolo-obs-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Passing an existing directory as the JSONL file path fails on
            // both Windows and Unix, without relying on filesystem permissions.
            using var recording = LspObservabilitySession.StartForTesting(directory);
            using var server = ProtocolSession.Start(observer: recording.Observer, recordingSession: recording);
            await server.Client.InitializeAsync();

            JObject ack = await server.Client.SetRecordingPolicyAsync("detailed");
            Assert.Equal("rejected", (string?)ack["status"]);
            Assert.Equal("writer_open_failed", (string?)ack["reason"]);
            Assert.Equal("off", (string?)ack["effectiveMode"]);
            Assert.Equal(0, (long)ack["recordingEpoch"]!);
            Assert.False((bool)ack["diskRecordingActive"]!);
            Assert.Equal(ObservabilityMode.Off, recording.Observer.Mode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Checkpoint_reports_queue_loss_even_without_an_exportable_disk_sink()
    {
        var queue = new BoundedEventQueue(1);
        var observer = new CompilerObserver(
            new ObservabilityOptions
            {
                Mode = ObservabilityMode.Off,
                Emitter = "checkpoint-overflow-test",
                QueueCapacity = 1,
            },
            queue: queue,
            catalog: EventCatalog.CreateKernelCatalog());

        using var server = ProtocolSession.Start(observer: observer);
        await server.Client.InitializeAsync();
        var eventRecord = new CompilerEvent
        {
            Event = "session.started",
            Kind = CompilerEventKind.Lifecycle,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P0,
        };
        observer.EmitLifecycle(eventRecord);
        observer.EmitLifecycle(eventRecord); // bounded P0 lane drops the second

        JObject checkpoint = await server.Client.GetRecordingCheckpointAsync();
        Assert.Equal(1, (long)checkpoint["droppedP0"]!);
        Assert.Equal(0, (long)checkpoint["droppedP1"]!);
        Assert.Equal(0, (long)checkpoint["droppedP2"]!);
        Assert.Equal(1, (int)checkpoint["pendingEvents"]!);
        Assert.True((bool)checkpoint["replayIncomplete"]!);
        Assert.False((bool)checkpoint["closedForInspection"]!);
        var reasons = ((JArray)checkpoint["incompleteReasons"]!).Values<string>().ToArray();
        Assert.Contains("queue_loss", reasons);
        Assert.Contains("no_disk_sink", reasons);
        Assert.True(checkpoint["lastDurableProducerSequence"] is null or { Type: JTokenType.Null });
        Assert.False((bool)checkpoint["exportSupported"]!);
    }

    [Fact]
    public async Task User_marker_has_a_unique_id_and_only_a_bounded_machine_category()
    {
        var (observer, queue) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync();

        JObject disabled = await session.Client.AddDiagnosticMarkerAsync("definition");
        Assert.Equal("rejected", (string?)disabled["status"]);
        Assert.Equal("recording_off", (string?)disabled["reason"]);

        JObject started = await session.Client.SetRecordingPolicyAsync("detailed");
        Assert.Equal("applied", (string?)started["status"]);
        var before = new List<StampedEvent>();
        queue.DrainAll(before);

        JObject bad = await session.Client.AddDiagnosticMarkerAsync("source:secret-path");
        Assert.Equal("rejected", (string?)bad["status"]);
        Assert.Equal("unsupported_category", (string?)bad["reason"]);

        JObject first = await session.Client.AddDiagnosticMarkerAsync("definition");
        JObject second = await session.Client.AddDiagnosticMarkerAsync("completion");
        Assert.Equal("queued_best_effort", (string?)first["status"]);
        Assert.Equal("queued_best_effort", (string?)second["status"]);
        Assert.False((bool)first["durable"]!);
        Assert.Equal((long)started["recordingEpoch"]!, (long)first["recordingEpoch"]!);
        Assert.NotEqual((string?)first["markerId"], (string?)second["markerId"]);

        var recorded = new List<StampedEvent>();
        queue.DrainAll(recorded);
        var markers = recorded.Where(x => x.Event.Event == "diagnostic.marker").ToArray();
        Assert.Equal(2, markers.Length);
        Assert.Equal((string?)first["markerId"], markers[0].Event.ClientActionId);
        Assert.Equal("added", markers[0].Event.Outcome);
        Assert.Equal(EventPriority.P0, markers[0].Event.Priority);
        Assert.Equal("definition", Assert.Single(markers[0].Event.Attributes!).Value);
        Assert.Equal("completion", Assert.Single(markers[1].Event.Attributes!).Value);
        Assert.All(markers, marker => Assert.Equal(0, observer.Catalog!.Validate(marker.Event).Count));
        Assert.Equal(0, observer.Catalog!.UnknownEvents);
        Assert.Equal(0, observer.Catalog.MissingFields);
    }

    [Fact]
    public async Task Capture_consent_is_scope_bound_revocable_and_never_enables_source_capture()
    {
        var (observer, _) = CreateObserver();
        using var server = ProtocolSession.Start(observer: observer);
        await server.Client.InitializeAsync();
        string serverId = observer.Identity.ServerInstanceId;

        JObject denied = await server.Client.GrantCaptureConsentAsync(
            serverId, ["file:///C:/project/main.cvl"], true, false, 60, userConfirmed: false);
        Assert.Equal("rejected", (string?)denied["status"]);
        Assert.Equal("explicit_user_confirmation_required", (string?)denied["reason"]);

        JObject grant = await server.Client.GrantCaptureConsentAsync(
            serverId, ["file:///C:/project/main.cvl"], true, false, 60);
        Assert.Equal("registered_not_capturing", (string?)grant["status"]);
        Assert.False((bool)grant["captureActive"]!);
        Assert.False((bool)grant["capturePipelineSupported"]!);
        Assert.NotNull(grant["leaseId"]);

        JObject status = await server.Client.GetCaptureConsentStatusAsync();
        Assert.Equal(1, (int)status["scopedDocumentCount"]!);
        Assert.True((bool)status["sourcePermissionRequested"]!);
        Assert.False((bool)status["pathPermissionRequested"]!);
        Assert.Equal("uri_syntax_only", (string?)status["scopeVerification"]);
        Assert.False((bool)status["captureActive"]!);

        JObject notImplemented = await server.Client.SetRecordingPolicyAsync("repro");
        Assert.Equal("rejected", (string?)notImplemented["status"]);
        Assert.Equal("sensitive_capture_not_implemented", (string?)notImplemented["reason"]);
        Assert.Equal(ObservabilityMode.Off, observer.Mode);
        Assert.Equal(0, observer.Identity.RecordingEpoch);

        JObject wrong = await server.Client.RevokeCaptureConsentAsync("other-server");
        Assert.Equal("rejected", (string?)wrong["status"]);
        Assert.Equal("server_instance_mismatch", (string?)wrong["reason"]);

        JObject revoked = await server.Client.RevokeCaptureConsentAsync(serverId);
        Assert.Equal("revoked", (string?)revoked["status"]);
        Assert.False((bool)revoked["captureActive"]!);
        JObject after = await server.Client.GetCaptureConsentStatusAsync();
        Assert.Equal("none", (string?)after["status"]);
        Assert.Equal(0, (int)after["scopedDocumentCount"]!);
        Assert.False((bool)after["existingArchivesErased"]!);

        JObject reproWithoutConsent = await server.Client.SetRecordingPolicyAsync("repro");
        Assert.Equal("forensic_consent_required", (string?)reproWithoutConsent["reason"]);
    }

    [Fact]
    public async Task Explicit_capture_journal_requires_lease_and_never_enables_disk_repro()
    {
        var (observer, _) = CreateObserver();
        using var server = ProtocolSession.Start(observer: observer);
        await server.Client.InitializeAsync();
        string serverId = observer.Identity.ServerInstanceId;
        JObject grant = await server.Client.GrantCaptureConsentAsync(
            serverId, ["file:///C:/project/main.cvl"], true, false, 60);

        JObject bad = await server.Client.ArmCaptureJournalAsync(serverId, "consent-wrong");
        Assert.Equal("rejected", (string?)bad["status"]);
        Assert.Equal("consent_lease_mismatch", (string?)bad["reason"]);

        JObject armed = await server.Client.ArmCaptureJournalAsync(
            serverId, (string)grant["leaseId"]!);
        Assert.Equal("armed_in_memory", (string?)armed["status"]);
        Assert.True((bool)armed["journalActive"]!);
        Assert.False((bool)armed["sourceExportSupported"]!);
        JObject status = await server.Client.GetCaptureConsentStatusAsync();
        Assert.True((bool)status["journalActive"]!);
        Assert.Equal(0, (int)status["capturedSnapshots"]!);
        Assert.False((bool)status["sourceExportSupported"]!);

        JObject notRepro = await server.Client.SetRecordingPolicyAsync("repro");
        Assert.Equal("rejected", (string?)notRepro["status"]);
        Assert.Equal("sensitive_capture_not_implemented", (string?)notRepro["reason"]);

        await server.Client.RevokeCaptureConsentAsync(serverId);
        JObject revoked = await server.Client.GetCaptureConsentStatusAsync();
        Assert.False((bool)revoked["journalActive"]!);
        Assert.Equal(0, (int)revoked["capturedSnapshots"]!);
    }

    [Fact]
    public async Task Transport_document_witness_is_exposed_by_real_rpc_without_source()
    {
        var (observer, _) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync();
        string serverId = observer.Identity.ServerInstanceId;
        JObject granted = await session.Client.GrantCaptureConsentAsync(
            serverId, ["file:///C:/project/main.cvl"], true, false, 60);
        await session.Client.ArmCaptureJournalAsync(serverId, (string)granted["leaseId"]!);
        // This unknown file is NOT scoped, but the raw transport can count
        // the fixed method without reading or retaining its source or URI.
        await session.Client.NotifyDidChangeAsync(
            new Uri("file:///C:/project/not-consented.cvl"), 2,
            new Microsoft.VisualStudio.LanguageServer.Protocol.TextDocumentContentChangeEvent
            {
                Text = "SECRET_UNSCOPED_PAYLOAD",
            });
        await session.Client.DrainNotificationsAsync();
        JObject result = await session.Client.VerifyCaptureJournalAsync();
        Assert.True((long?)result["transportWitness"]?["didChange"] >= 1);
        Assert.Equal("unscoped_transport", (string?)result["transportWitness"]?["scope"]);
        Assert.False((bool)result["transportWitness"]!["transportToHandlerCorrelationProven"]!);
        Assert.DoesNotContain("SECRET_UNSCOPED_PAYLOAD", result.ToString());
        Assert.DoesNotContain("not-consented.cvl", result.ToString());
    }

    [Fact]
    public async Task Seal_journal_RPC_exposes_only_verified_internal_closure()
    {
        var (observer, _) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync();
        string serverId = observer.Identity.ServerInstanceId;
        JObject granted = await session.Client.GrantCaptureConsentAsync(
            serverId, ["file:///C:/project/sealed.cvl"], true, false, 60);
        string lease = (string)granted["leaseId"]!;
        await session.Client.ArmCaptureJournalAsync(serverId, lease);
        JObject ack = await session.Client.SealCaptureJournalAsync(serverId, lease);
        Assert.Equal("sealed_in_memory", (string?)ack["status"]);
        Assert.Equal("satisfied", (string?)ack["registeredInputBarrier"]);
        Assert.True((bool)ack["internalProducerClosed"]!);
        Assert.False((bool)ack["transportProducerClosed"]!);
        Assert.False((bool)ack["durableCheckpointAvailable"]!);
        Assert.True((bool)(await session.Client.GetCaptureConsentStatusAsync())["journalSealed"]!);
        JObject evidence = await session.Client.VerifyCaptureJournalAsync();
        Assert.True((bool)evidence["inMemoryJournalSealed"]!);
        Assert.False((bool)evidence["replayComplete"]!);
        Assert.DoesNotContain("sealed.cvl", evidence.ToString());
    }

    [Fact]
    public async Task Read_barrier_checkpoint_is_explicitly_not_producer_closure()
    {
        var (observer, _) = CreateObserver();
        using var session = ProtocolSession.Start(observer: observer);
        await session.Client.InitializeAsync();

        JObject evidence = await session.Client.VerifyCaptureCheckpointAsync();
        JObject barrier = Assert.IsType<JObject>(evidence["verificationBarrier"]);
        Assert.Equal("satisfied", (string?)barrier["status"]);
        Assert.True((long?)barrier["requestIngressSequence"] > 0);
        Assert.False((bool)barrier["transportToHandlerCompleteProven"]!);
        Assert.False((bool)barrier["durableProducerClosureProven"]!);
        Assert.False((bool)evidence["replayComplete"]!);
        Assert.NotEqual("complete", (string?)evidence["documentReplayCompleteness"]);
    }

    [Fact]
    public async Task Flight_recorder_tracks_pretrigger_frames_while_recording_is_off()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync();

        await session.Client.NotifyDidChangeAsync(
            new Uri("file:///C:/project/secret-document.cvl"), 2,
            new Microsoft.VisualStudio.LanguageServer.Protocol.TextDocumentContentChangeEvent
            {
                Text = "SECRET_FLIGHT_SOURCE",
            });
        await session.Client.DrainNotificationsAsync();

        JObject before = await session.Client.GetFlightRecorderSnapshotAsync();
        Assert.Equal("untriggered", (string?)before["status"]);
        Assert.False((bool)before["sourceContentCaptured"]!);
        Assert.Contains((JArray)before["events"]!, e =>
            (string?)e["kind"] == "did_change" &&
            (string?)e["stage"] == "transport_read");

        JObject marked = await session.Client.TriggerFlightRecorderAsync("diagnostics");
        Assert.Equal("captured_in_memory", (string?)marked["status"]);
        Assert.False((bool)marked["durablySaved"]!);

        JObject after = await session.Client.GetFlightRecorderSnapshotAsync();
        Assert.Equal((string?)marked["incidentId"], (string?)after["incidentId"]);
        Assert.True((int)after["retainedPretriggerEvents"]! > 0);
        Assert.True((bool)after["triggerRetained"]!);
        Assert.False((bool)after["durableCheckpointAvailable"]!);
        Assert.False((bool)after["transportToHandlerCorrelationProven"]!);
        Assert.DoesNotContain("SECRET_FLIGHT_SOURCE", after.ToString());
        Assert.DoesNotContain("secret-document.cvl", after.ToString());
    }

    [Fact]
    public async Task Incident_index_is_safe_temporal_only_for_user_markers()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync();
        await session.Client.NotifyDidChangeAsync(
            new Uri("file:///C:/private/SECRET_NAME.cvl"), 3,
            new Microsoft.VisualStudio.LanguageServer.Protocol.TextDocumentContentChangeEvent
            {
                Text = "PRIVATE_SOURCE_FOR_INDEX",
            });
        await session.Client.DrainNotificationsAsync();
        var triggered = await session.Client.TriggerFlightRecorderAsync("other");
        JObject index = await session.Client.GetFlightIncidentIndexAsync();

        Assert.False((bool)index["sourceContentCaptured"]!);
        Assert.Equal("no_root_cause_claimed", (string?)index["rootCausePolicy"]);
        var incident = Assert.Single((JArray)index["incidents"]!);
        Assert.Equal((string?)triggered["incidentId"], (string?)incident["incidentId"]);
        Assert.Equal("unknown", (string?)incident["rootCause"]);
        Assert.Null(incident["firstObservedTriggerSource"]?.Type == JTokenType.Null
            ? null : incident["firstObservedTriggerSource"]);
        Assert.All((JArray)incident["candidates"]!, candidate =>
            Assert.Equal("temporal_only", (string?)candidate["association"]));
        string json = index.ToString();
        Assert.DoesNotContain("PRIVATE_SOURCE_FOR_INDEX", json);
        Assert.DoesNotContain("SECRET_NAME.cvl", json);
    }

    [Fact]
    public async Task Flight_trigger_is_validated_even_without_normal_recording()
    {
        using var session = ProtocolSession.Start();
        await session.Client.InitializeAsync();
        JObject invalid = await session.Client.TriggerFlightRecorderAsync("user-supplied-secret");
        Assert.Equal("rejected", (string?)invalid["status"]);
        Assert.Equal("unsupported_category", (string?)invalid["reason"]);
        JObject wrongVersion = await session.Client.TriggerFlightRecorderAsync(
            "other", protocolVersion: 4);
        Assert.Equal("unsupported_protocol_version", (string?)wrongVersion["reason"]);
        Assert.Null((string?)wrongVersion["incidentId"]);
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
