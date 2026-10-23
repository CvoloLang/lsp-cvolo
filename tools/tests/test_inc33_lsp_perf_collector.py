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
