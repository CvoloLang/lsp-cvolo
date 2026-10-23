using Cvolo.LanguageServer.Observability;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Volatile bounded pretrigger evidence, including privacy and loss contracts.
/// No real workspace sources are used.
/// </summary>
public sealed class LspFlightRecorderTests
{
    [Fact]
    public void Safe_event_vocabulary_drops_arbitrary_method_and_outcome_strings()
    {
        var recorder = new LspFlightRecorder();
        recorder.OnTransportNotification("textDocument/SECRET_PATH.cvl");
        recorder.OnHandlerEntered("private source", 2);
        recorder.OnHandlerSettled("did_change", "SECRET_EXCEPTION_TEXT", 3);
        var empty = recorder.Inspect();
        Assert.Empty(empty.Events);
        Assert.False(empty.SourceContentCaptured);

        recorder.OnTransportNotification("textDocument/didOpen");
        recorder.OnHandlerEntered("did_open", 5);
        recorder.OnHandlerSettled("did_open", "rejected", 5);
        var report = recorder.Inspect();
        Assert.Equal(3, report.RetainedEvents);
        Assert.Equal("did_open", report.Events[0].Kind);
        Assert.Equal("handler_settled", report.Events[2].Stage);
    }

    [Fact]
    public void Failure_automatically_triggers_in_memory_incident()
    {
        var recorder = new LspFlightRecorder();
        recorder.OnTransportNotification("textDocument/didChange");
        recorder.OnHandlerEntered("did_change", 12);
        recorder.OnHandlerSettled("did_change", "faulted", 12);

        var report = recorder.Inspect();
        Assert.Equal("incident_observed_in_memory", report.Status);
        Assert.Equal("document_handler_faulted", report.IncidentReason);
        Assert.Equal(3, report.RetainedPretriggerEvents);
        Assert.True(report.TriggerRetained);
        Assert.False(report.DurableCheckpointAvailable);
    }

    [Fact]
    public void Incident_index_causally_links_only_the_triggering_fault()
    {
        var recorder = new LspFlightRecorder();
        recorder.OnTransportNotification("textDocument/didChange");
        recorder.OnHandlerEntered("did_change", 9);
        recorder.OnHandlerSettled("did_change", "faulted", 9);
        var index = FlightIncidentAnalyzer.Analyze(recorder.Inspect());
        var incident = Assert.Single(index.Incidents);
        Assert.Equal("document_handler_faulted", incident.TriggerReason);
        Assert.Equal("unknown", incident.RootCause);
        Assert.NotNull(incident.FirstObservedTriggerSource);
        Assert.Single(incident.Candidates, e => e.Association == "causal"
            && e.Explanation == "automatic_trigger_source");
        Assert.Contains(incident.Candidates, e => e.Association == "temporal_only");
        Assert.Equal("volatile_only", index.Durability);
    }

    [Fact]
    public void Multiple_manual_markers_remain_temporal_and_ordered()
    {
        var recorder = new LspFlightRecorder();
        recorder.OnTransportNotification("textDocument/didOpen");
        var first = recorder.Trigger(new FlightRecorderTriggerRequest
        {
            ProtocolVersion = 1, Category = "other",
        }, true);
        recorder.OnTransportNotification("textDocument/didClose");
        var second = recorder.Trigger(new FlightRecorderTriggerRequest
        {
            ProtocolVersion = 1, Category = "diagnostics",
        }, true);
        var index = FlightIncidentAnalyzer.Analyze(recorder.Inspect());
        Assert.Equal(2, index.Incidents.Count);
        Assert.Equal(first.IncidentId, index.Incidents[0].IncidentId);
        Assert.Equal(second.IncidentId, index.Incidents[1].IncidentId);
        Assert.All(index.Incidents, incident =>
        {
            Assert.Equal("unknown", incident.RootCause);
            Assert.Null(incident.FirstObservedTriggerSource);
            Assert.All(incident.Candidates, candidate =>
                Assert.Equal("temporal_only", candidate.Association));
        });
    }

    [Fact]
    public void Evicted_history_is_never_reported_as_complete_incident_evidence()
    {
        var recorder = new LspFlightRecorder();
        recorder.OnTransportNotification("textDocument/didOpen");
        for (int i = 0; i < LspFlightRecorder.MaxEvents + 20; i++)
            recorder.OnTransportNotification("textDocument/didChange");
        recorder.Trigger(new FlightRecorderTriggerRequest
        {
            ProtocolVersion = 1, Category = "other",
        }, true);
        var index = FlightIncidentAnalyzer.Analyze(recorder.Inspect());
        Assert.True(index.IncidentHistoryIncomplete);
        Assert.False(Assert.Single(index.Incidents).EvidenceComplete);
        Assert.True(index.Incidents[0].WindowTruncated);
    }


    [Fact]
    public void Private_checkpoint_is_written_verified_and_recovered_by_new_store()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cvolo-flight-" + Guid.NewGuid().ToString("N"));
        try
        {
            var flight = new LspFlightRecorder();
            flight.OnTransportNotification("textDocument/didOpen");
            flight.OnHandlerEntered("did_open", 13);
            flight.OnHandlerSettled("did_open", "faulted", 13);
            var writer = new FlightCheckpointStore(directory);
            var write = writer.Write(flight.Inspect());
            Assert.Equal("verified_file_published", write.Status);
            Assert.True(write.VerifiedAfterWrite);
            Assert.False(write.DirectorySyncProven);
            Assert.False(write.PendingWriteSurvivesCrash);

            var newProcessStore = new FlightCheckpointStore(directory);
            var recovered = newProcessStore.RecoverLatest();
            Assert.True(recovered.ValidCheckpointRecovered);
            Assert.Equal(flight.Inspect().IncidentId, recovered.IncidentId);
            Assert.False(recovered.AllProcessHistoryRecovered);
            byte[] bytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(directory, "*.json")));
            Assert.True(FlightCheckpointStore.Validate(bytes));
            Assert.DoesNotContain("C:/", System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Tampered_checkpoint_is_never_reported_as_recovered()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cvolo-flight-" + Guid.NewGuid().ToString("N"));
        try
        {
            var flight = new LspFlightRecorder();
            flight.OnHandlerSettled("did_change", "faulted", 9);
            var store = new FlightCheckpointStore(directory);
            Assert.True(store.Write(flight.Inspect()).FilePublished);
            string path = Assert.Single(Directory.GetFiles(directory, "*.json"));
            byte[] bytes = File.ReadAllBytes(path);
            using var json = System.Text.Json.JsonDocument.Parse(bytes);
            string changed = System.Text.Encoding.UTF8.GetString(bytes)
                .Replace("document_handler_faulted", "manual");
            File.WriteAllText(path, changed);
            Assert.Equal("no_valid_checkpoint", new FlightCheckpointStore(directory).RecoverLatest().Status);
            Assert.False(FlightCheckpointStore.Validate(System.Text.Encoding.UTF8.GetBytes(changed)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Untriggered_history_cannot_be_persisted()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cvolo-flight-" + Guid.NewGuid().ToString("N"));
        var flight = new LspFlightRecorder();
        flight.OnHandlerEntered("did_open", 2);
        var status = new FlightCheckpointStore(directory).Write(flight.Inspect());
        Assert.Equal("no_retained_incident", status.FailureReason);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Recovery_skips_corrupted_latest_file_and_uses_previous_verified_checkpoint()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cvolo-flight-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new LspFlightRecorder();
            first.OnHandlerSettled("did_open", "faulted", 1);
            var store = new FlightCheckpointStore(directory);
            Assert.True(store.Write(first.Inspect()).FilePublished);
            string earlier = Assert.Single(Directory.GetFiles(directory, "*.json"));
            var later = new LspFlightRecorder();
            later.OnTransportNotification("textDocument/didChange");
            later.OnHandlerSettled("did_change", "faulted", 2);
            Assert.True(store.Write(later.Inspect()).FilePublished);
            string newest = Assert.Single(Directory.GetFiles(directory, "*.json")
                .Where(path => path != earlier));
            File.WriteAllText(newest, "{ broken");
            var result = new FlightCheckpointStore(directory).RecoverLatest();
            Assert.Equal("verified_metadata_recovered", result.Status);
            Assert.Equal(first.Inspect().IncidentId, result.IncidentId);
            Assert.True(result.ValidCheckpointRecovered);
            Assert.False(result.AllProcessHistoryRecovered);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Repeated_incidents_keep_no_more_than_eight_published_files()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cvolo-flight-" + Guid.NewGuid().ToString("N"));
        try
        {
            var recorder = new LspFlightRecorder();
            var store = new FlightCheckpointStore(directory);
            for (int i = 0; i < FlightCheckpointStore.MaxFiles + 4; i++)
            {
                recorder.OnHandlerSettled("did_change", "faulted", i + 1);
                Assert.True(store.Write(recorder.Inspect()).FilePublished);
            }
            Assert.Equal(FlightCheckpointStore.MaxFiles,
                Directory.GetFiles(directory, "*.json").Length);
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Performance_report_uses_monotonic_clock_and_union_of_overlapping_handlers()
    {
        DateTimeOffset utc = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
        long ticks = 0;
        long millisecond = System.Diagnostics.Stopwatch.Frequency / 1000;
        var recorder = new LspFlightRecorder(() => utc, () => ticks);
        recorder.OnHandlerEntered("did_change", 1); // [0, 20]
        ticks += 10 * millisecond;
        recorder.OnHandlerEntered("did_change", 2); // [10, 30]
        ticks += 10 * millisecond;
        recorder.OnHandlerSettled("did_change", "applied", 1);
        ticks += 10 * millisecond;
        recorder.OnHandlerSettled("did_change", "rejected", 2);
        var report = FlightPerformanceAnalyzer.Analyze(recorder.Inspect());

        Assert.Equal("observed_only", report.Coverage);
        Assert.Equal(2, report.MatchedDurations);
        Assert.Equal(1, report.RejectedSettlements);
        Assert.InRange(report.SumObservedHandlerTimeMs, 39, 41);
        Assert.InRange(report.ObservedBusyTimeMs, 29, 31);
        Assert.InRange(report.P95HandlerTimeMs!.Value, 19, 21);
        Assert.False(report.EndToEndLatencyProven);
        Assert.False(report.CausalCriticalPathProven);
        Assert.False(report.AllOperationsCapturedProven);
    }

    [Fact]
    public void Missing_entry_clock_regression_and_unmatched_settlement_are_partial_evidence()
    {
        long ticks = 1000;
        var recorder = new LspFlightRecorder(
            monotonicTicks: () => ticks);
        recorder.OnHandlerEntered("did_open", 2);
        ticks = 900;
        recorder.OnHandlerSettled("did_open", "faulted", 2);
        recorder.OnHandlerSettled("did_change", "rejected", 77);
        recorder.OnHandlerEntered("did_close", 4);
        var report = FlightPerformanceAnalyzer.Analyze(recorder.Inspect());

        Assert.Equal("partial", report.Coverage);
        Assert.Equal(1, report.InvalidClockPairs);
        Assert.Equal(1, report.UnmatchedSettlements);
        Assert.Equal(1, report.UnsettledHandlerEntries);
        Assert.Equal(1, report.FaultedSettlements);
        Assert.Equal(0, report.MatchedDurations);
    }

    [Fact]
    public void Overwritten_history_never_claims_complete_performance_coverage()
    {
        var recorder = new LspFlightRecorder();
        recorder.OnHandlerEntered("did_open", 1);
        for (int i = 0; i < LspFlightRecorder.MaxEvents + 3; i++)
            recorder.OnTransportNotification("textDocument/didChange");
        recorder.OnHandlerSettled("did_open", "applied", 1);
        var report = FlightPerformanceAnalyzer.Analyze(recorder.Inspect());
        Assert.Equal("partial", report.Coverage);
        Assert.True(report.SnapshotHistoryTruncated);
        Assert.Equal(1, report.UnmatchedSettlements);
        Assert.False(report.CausalCriticalPathProven);
    }

    [Fact]
    public void Delayed_older_incident_cannot_replace_newer_recovery_or_status()
    {
        DateTimeOffset now = new(2026, 10, 23, 11, 0, 0, TimeSpan.Zero);
        string directory = Path.Combine(Path.GetTempPath(),
            "cvolo-flight-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            var recorder = new LspFlightRecorder(() => now);
            recorder.OnHandlerSettled("did_open", "faulted", 1);
            FlightRecorderSnapshot older = recorder.Inspect();
            now = now.AddSeconds(1);
            recorder.OnHandlerSettled("did_change", "faulted", 2);
            FlightRecorderSnapshot newer = recorder.Inspect();
            var writer = new FlightCheckpointStore(directory, () => now);

            // Emulate Task.Run scheduling: the newer incident completes first.
            Assert.True(writer.Write(newer).VerifiedAfterWrite);
            Assert.True(writer.Write(older).VerifiedAfterWrite);
            Assert.Equal(newer.IncidentId, writer.Inspect().IncidentId);

            var recovered = new FlightCheckpointStore(directory).RecoverLatest();
            Assert.True(recovered.ValidCheckpointRecovered);
            Assert.Equal(newer.IncidentId, recovered.IncidentId);
            Assert.False(recovered.AllProcessHistoryRecovered);
            Assert.Equal(2, Directory.GetFiles(directory, "*.json").Length);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Reverse_order_publication_prunes_oldest_incidents_by_event_sequence()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "cvolo-flight-retain-" + Guid.NewGuid().ToString("N"));
        try
        {
            var recorder = new LspFlightRecorder();
            var captures = new List<FlightRecorderSnapshot>();
            for (int i = 0; i < FlightCheckpointStore.MaxFiles + 4; i++)
            {
                recorder.OnHandlerSettled("did_change", "faulted", i + 1);
                captures.Add(recorder.Inspect());
            }
            var writer = new FlightCheckpointStore(directory);
            foreach (var capture in captures.AsEnumerable().Reverse())
                Assert.True(writer.Write(capture).VerifiedAfterWrite);

            Assert.Equal(FlightCheckpointStore.MaxFiles,
                Directory.GetFiles(directory, "*.json").Length);
            Assert.Equal(captures[^1].IncidentId,
                new FlightCheckpointStore(directory).RecoverLatest().IncidentId);
            Assert.Equal(captures[^1].IncidentId, writer.Inspect().IncidentId);
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Concurrent_faults_emit_atomically_adjacent_trigger_edges()
    {
        var recorder = new LspFlightRecorder();
        Parallel.For(0, 32, i =>
            recorder.OnHandlerSettled("did_change", "faulted", i + 1));
        var snapshot = recorder.Inspect();
        var events = snapshot.Events.ToArray();
        Assert.Equal(64, events.Length);
        for (int i = 1; i < events.Length; i += 2)
        {
            Assert.Equal("handler_settled", events[i - 1].Stage);
            Assert.Equal("faulted", events[i - 1].Outcome);
            Assert.Equal("incident", events[i].Kind);
            Assert.Equal("trigger", events[i].Stage);
            Assert.Equal(events[i - 1].Sequence + 1, events[i].Sequence);
        }
        var report = FlightCausalAnalyzer.Analyze(snapshot);
        Assert.Equal(32, report.LinkedAutomaticFaultTriggers);
        Assert.Equal(32, report.MissingHandlerEntries);
        Assert.Equal(32, report.DirectEdges.Count);
        Assert.False(report.RootCauseProven);
        Assert.False(report.EndToEndCriticalPathProven);
    }

    [Fact]
    public void Causal_report_matches_only_unique_handler_pairs()
    {
        long ticks = 0;
        long ms = System.Diagnostics.Stopwatch.Frequency / 1000;
        var recorder = new LspFlightRecorder(monotonicTicks: () => ticks);
        recorder.OnHandlerEntered("did_open", 4);
        ticks += 20 * ms;
        recorder.OnHandlerSettled("did_open", "applied", 4);
        recorder.OnHandlerEntered("did_change", 7);
        recorder.OnHandlerEntered("did_change", 7);
        ticks += 10 * ms;
        recorder.OnHandlerSettled("did_change", "rejected", 7);
        recorder.OnHandlerSettled("did_close", "rejected", 9);
        var result = FlightCausalAnalyzer.Analyze(recorder.Inspect());
        Assert.Equal("partial", result.Coverage);
        Assert.Equal(1, result.MatchedHandlerLifecycleEdges);
        Assert.Equal(3, result.AmbiguousDuplicateEvents);
        Assert.Equal(1, result.MissingHandlerEntries);
        Assert.Single(result.DirectEdges);
        Assert.Equal("handler_lifecycle", result.DirectEdges[0].Relation);
        Assert.Equal(4L, result.DirectEdges[0].IngressSequence);
        Assert.InRange(result.LongestObservedHandlerMs!.Value, 19, 21);
        Assert.Equal("unknown", result.RootCause);
        Assert.False(result.CompleteDocumentHistoryProven);
    }

    [Fact]
    public void Causal_report_rejects_missing_ingress_as_edge_proof()
    {
        var recorder = new LspFlightRecorder();
        recorder.OnHandlerEntered("did_open", null);
        recorder.OnHandlerSettled("did_open", "applied", null);
        var report = FlightCausalAnalyzer.Analyze(recorder.Inspect());
        Assert.Equal("partial", report.Coverage);
        Assert.Equal(2, report.UnknownIngressEvents);
        Assert.Empty(report.DirectEdges);
        Assert.False(report.TransportToHandlerCorrelationProven);
    }

    [Fact]
    public void Ring_is_bounded_and_reports_overwritten_prefix()
    {
        var recorder = new LspFlightRecorder();
        for (int i = 0; i < LspFlightRecorder.MaxEvents + 12; i++)
            recorder.OnTransportNotification("textDocument/didChange");

        var report = recorder.Inspect();
        Assert.Equal(LspFlightRecorder.MaxEvents, report.RetainedEvents);
        Assert.Equal(12, report.EvictedEvents);
        Assert.True(report.HistoryPrefixTruncated);
        Assert.False(report.PreProcessHistoryRecoverable);
    }

    [Fact]
    public void Ring_expires_metadata_without_sleeping()
    {
        DateTimeOffset now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var recorder = new LspFlightRecorder(() => now);
        recorder.OnTransportNotification("textDocument/didOpen");
        now += LspFlightRecorder.Retention + TimeSpan.FromSeconds(1);
        Assert.Empty(recorder.Inspect().Events);
        Assert.Equal(1, recorder.Inspect().EvictedEvents);
        Assert.True(recorder.Inspect().HistoryPrefixTruncated);
    }

    [Fact]
    public void Manual_trigger_requires_supported_version_and_category()
    {
        var recorder = new LspFlightRecorder();
        Assert.Equal("server_not_initialized",
            recorder.Trigger(new FlightRecorderTriggerRequest
            {
                ProtocolVersion = 1, Category = "other",
            }, false).Reason);
        Assert.Equal("unsupported_category",
            recorder.Trigger(new FlightRecorderTriggerRequest
            {
                ProtocolVersion = 1, Category = "PRIVATE_NOTE",
            }, true).Reason);
        Assert.Equal("unsupported_protocol_version",
            recorder.Trigger(new FlightRecorderTriggerRequest
            {
                ProtocolVersion = 3, Category = "other",
            }, true).Reason);
        var accepted = recorder.Trigger(new FlightRecorderTriggerRequest
        {
            ProtocolVersion = 1, Category = "definition",
        }, true);
        Assert.Equal("captured_in_memory", accepted.Status);
        Assert.False(accepted.DurablySaved);
        Assert.NotNull(accepted.IncidentId);
    }
}
