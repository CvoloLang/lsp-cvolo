"""Unit smoke tests for real-process collector protocol primitives.

These tests do not create or publish synthetic measured performance evidence.
"""
import io
import importlib.util
import json
import unittest
from pathlib import Path

SRC = Path(__file__).resolve().parents[1] / "collect_inc33_lsp_perf.py"
spec = importlib.util.spec_from_file_location("inc33_lsp_perf_collector", SRC)
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


class RealCollectorContractTests(unittest.TestCase):
    def test_actual_supported_modes_do_not_claim_repro(self):
        self.assertEqual(("off", "basic", "detailed"), collector.MODES)
        self.assertNotIn("repro", collector.MODES)

    def test_rotating_execution_order(self):
        observed = [collector.alternating_modes(i) for i in range(6)]
        self.assertEqual(["off", "basic", "detailed"], observed[0])
        self.assertEqual(["basic", "detailed", "off"], observed[1])
        self.assertEqual(["detailed", "off", "basic"], observed[2])
        self.assertEqual(observed[0], observed[3])

    def test_exploratory_measurements_do_not_claim_normative_pass(self):
        raw = {
            "runs": [
                {"samplesUs": {
                    "off": [100, 100, 100],
                    "basic": [101, 101, 101],
                    "detailed": [105, 105, 105],
                }},
            ],
        }
        report = collector.exploratory_summary(raw)
        self.assertEqual("DESCRIPTIVE_ONLY", report["status"])
        self.assertEqual(["repro"], report["missingModes"])
        self.assertFalse(report["normativeRegressionPass"])
        self.assertFalse(report["confidenceBoundedBudgetVerdict"])
        self.assertEqual(100, report["perMode"]["off"]["p95Us"])
        self.assertEqual(1, report["perMode"]["basic"]["observedP95DeltaPercent"])
        self.assertEqual(3, report["perMode"]["detailed"]["sampleCount"])
        self.assertFalse(report["noisyBaseline"])
        self.assertEqual("INCONCLUSIVE", report["observedLatencyBudgetVerdict"])

    def test_noisy_short_lsp_baseline_forces_inconclusive(self):
        raw = {
            "runs": [
                {"samplesUs": {
                    "off": [100 + (25 if i % 2 else 0)],
                    "basic": [110], "detailed": [111],
                }} for i in range(8)
            ],
        }
        report = collector.exploratory_summary(raw)
        self.assertTrue(report["noisyBaseline"])
        self.assertGreater(report["baselineRelativeP95RunSpread"], .08)
        self.assertFalse(report["confidenceBoundedBudgetVerdict"])
        self.assertEqual("INCONCLUSIVE", report["observedLatencyBudgetVerdict"])

    def test_exploratory_percentiles_reject_invalid_or_absent_samples(self):
        record = {"runs": [{"samplesUs": {
            "off": [10], "basic": [11], "detailed": [12]}}]}
        record["runs"][0]["samplesUs"]["basic"] = [float("nan")]
        with self.assertRaisesRegex(collector.BenchmarkFailure, "invalid_exploratory_samples"):
            collector.exploratory_summary(record)

    def test_debounce_free_document_symbol_workload_is_explicit(self):
        self.assertEqual(("completion", "document_symbol"), collector.WORKLOADS)
        method, params = collector.workload_request(
            "document_symbol", "file:///synthetic/main.cvl")
        self.assertEqual("textDocument/documentSymbol", method)
        self.assertEqual({"textDocument": {"uri": "file:///synthetic/main.cvl"}}, params)
        self.assertTrue(collector.valid_workload_result(
            "document_symbol", [{"name": "Main", "kind": 12}]))
        self.assertFalse(collector.valid_workload_result("document_symbol", []))
        self.assertFalse(collector.valid_workload_result("document_symbol", None))
        self.assertFalse(collector.valid_workload_result("completion", []))

    def test_unsupported_workload_rejected_before_server_start(self):
        with self.assertRaisesRegex(collector.BenchmarkFailure,
                                    "unsupported_benchmark_workload"):
            collector.collect(["does-not-exist"], workload="fabricated")

    def test_failure_report_is_classified_and_hides_paths(self):
        error = collector.BenchmarkFailure(
            "C:/Users/private/path/file.cvl and source contents")
        report = collector.incomplete_collection_evidence(error)
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertFalse(report["endToEndRegressionGatePassed"])
        self.assertTrue(report["noMeasuredSamplesPublished"])
        self.assertNotIn("private", json.dumps(report))

    def test_header_delimited_frame_parser(self):
        payload = {"jsonrpc": "2.0", "id": 1, "result": {"x": 4}}
        self.assertEqual(payload, collector.read_frame(io.BytesIO(collector.frame(payload))))

    def test_short_pipe_reads_are_combined_into_one_frame(self):
        class FragmentedReader:
            def __init__(self, data):
                self.inner = io.BytesIO(data)
            def readline(self, size=-1):
                return self.inner.readline(size)
            def read(self, size=-1):
                return self.inner.read(min(size, 3))
        payload = {"jsonrpc": "2.0", "id": 2, "result": {"items": []}}
        self.assertEqual(payload,
            collector.read_frame(FragmentedReader(collector.frame(payload))))

    def test_did_open_readiness_requires_applied_settlement(self):
        class StubProcess:
            def __init__(self, outcomes):
                self.outcomes = iter(outcomes)
            def call(self, method):
                self.method = method
                return 1, {"events": next(self.outcomes)}
        pending = [{"kind": "did_open", "stage": "handler_entered"}]
        applied = [{"kind": "did_open", "stage": "handler_settled",
                    "outcome": "applied"}]
        stub = StubProcess([pending, applied])
        collector.wait_for_document_applied(stub, 2)
        self.assertEqual("cvolo/observability/flightRecorderSnapshot.v1", stub.method)

        rejected = StubProcess([[{"kind": "did_open", "stage": "handler_settled",
                                  "outcome": "rejected"}]])
        with self.assertRaisesRegex(collector.BenchmarkFailure,
                                    "benchmark_document_open_rejected"):
            collector.wait_for_document_applied(rejected, 2)

    def test_cumulative_resource_delta_and_signed_memory_gauges(self):
        def snapshot(ticks, cpu, allocated, gc0, working_set):
            return {
                "protocolVersion": 1,
                "scope": "same_process_cumulative_non_atomic",
                "monotonicTicks": ticks,
                "monotonicFrequency": 10_000_000,
                "processCpuTimeTicks": cpu,
                "totalAllocatedBytes": allocated,
                "managedHeapBytes": 70000 + ticks // 100,
                "workingSetBytes": working_set,
                "gen0Collections": gc0,
                "gen1Collections": 0,
                "gen2Collections": 0,
                "resourceAttributionToRequestProven": False,
                "isolatedCpuMeasurementProven": False,
                "peakMemoryObserved": False,
            }
        before = snapshot(10000, 30000, 100000, 2, 4000000)
        after = snapshot(1010000, 80000, 200000, 3, 3900000)
        result = collector.resource_deltas(before, after, 50)
        self.assertEqual(100000, result["totalAllocatedBytesDelta"])
        self.assertEqual(2000, result["allocatedBytesPerRequest"])
        self.assertEqual(1, result["gen0CollectionsDelta"])
        self.assertEqual(4000000, result["workingSetBytesBefore"])
        self.assertEqual(3900000, result["workingSetBytesAfter"])
        self.assertFalse(result["peakMemoryObserved"])
        self.assertFalse(result["regressionBudgetPass"])
        self.assertEqual("process_wide_not_per_request", result["attribution"])

    def test_resource_provenance_and_non_monotonic_counters_fail_closed(self):
        snap = {
            "protocolVersion": 1,
            "scope": "same_process_cumulative_non_atomic",
            "monotonicTicks": 100000,
            "monotonicFrequency": 10000000,
            "processCpuTimeTicks": 900000,
            "totalAllocatedBytes": 250000,
            "managedHeapBytes": 50000,
            "workingSetBytes": 5000000,
            "gen0Collections": 2,
            "gen1Collections": 0,
            "gen2Collections": 0,
            "resourceAttributionToRequestProven": False,
            "isolatedCpuMeasurementProven": False,
            "peakMemoryObserved": False,
        }
        future = dict(snap, monotonicTicks=200000)
        for name, bad in (
            ("totalAllocatedBytes", 200000),
            ("processCpuTimeTicks", 300000),
            ("gen0Collections", 1),
            ("workingSetBytes", "private source"),
            ("monotonicFrequency", 0),
            ("resourceAttributionToRequestProven", True),
        ):
            modified = dict(future, **{name: bad})
            with self.subTest(field=name):
                with self.assertRaises(collector.BenchmarkFailure):
                    collector.resource_deltas(snap, modified, 50)
        with self.assertRaises(collector.BenchmarkFailure):
            collector.resource_deltas(snap, future, 0)

    def test_duplicate_content_length_is_rejected(self):
        sample = b"Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}"
        with self.assertRaises(collector.BenchmarkFailure):
            collector.read_frame(io.BytesIO(sample))

    def test_response_length_limits(self):
        sample = b"Content-Length: 99999999\r\n\r\n"
        with self.assertRaises(collector.BenchmarkFailure):
            collector.read_frame(io.BytesIO(sample))

    def test_no_user_payload_is_in_runner_status(self):
        self.assertNotIn("sourceText", collector.MODES)
        self.assertEqual("off", collector.alternating_modes(0)[0])


if __name__ == "__main__":
    unittest.main()
