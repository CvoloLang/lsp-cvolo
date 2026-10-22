"""Independent offline bundle fixtures; no VS Code/Tooling/server needed."""
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest
import zipfile

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "analyze-diagnostic-bundle.py"
spec = importlib.util.spec_from_file_location("cvolo_analyzer", SCRIPT)
analyzer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analyzer)


def event(seq, name, **extra):
    value = dict(schemaVersion=2, schemaMinor=1, emitterInstanceId="emitter-1",
                 recordingEpoch=1, producerSequence=seq,
                 timestampUtc="2026-10-22T10:00:00Z", event=name)
    value.update(extra)
    return value


def make_zip(path, events, mutate=None, extra=None):
    data = ("\n".join(json.dumps(e) for e in events) + "\n").encode()
    manifest = dict(format="cvolo.diagnostic-bundle", formatVersion=1,
                    hashAlgorithm="sha256", integrityScope="uncompressed_archive_members",
                    segments=[dict(entry="events/segment-0000.jsonl",
                                   sizeBytes=len(data), sha256=hashlib.sha256(data).hexdigest())],
                    checkpoint=dict(recordingEpoch=1, replayIncomplete=False),
                    completenessVerified=False, privacyAuditVerified=False)
    if mutate:
        mutate(manifest)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as writer:
        writer.writestr("events/segment-0000.jsonl", data)
        writer.writestr("manifest.json", json.dumps(manifest))
        for key, value in (extra or {}).items():
            writer.writestr(key, value)


class AnalyzerTests(unittest.TestCase):
    def test_marker_is_temporal_not_causal_and_source_not_repeated(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = pathlib.Path(tmp) / "test.zip"
            make_zip(f, [
                event(3, "rpc.response.sent", outcome="success"),
                event(2, "diagnostic.marker", clientActionId="marker-"+"a"*32,
                      attributes=dict(category="definition", source="/secret/source.cvl")),
                event(1, "feature.request.completed", outcome="not_found", reason="symbol_not_found")
            ])
            result = analyzer.analyze(f)
            marker = result["incident-index.json"]["markers"][0]
            self.assertEqual("temporal_only", marker["association"])
            self.assertEqual("insufficient_evidence", marker["rootCause"])
            self.assertEqual("events/segment-0000.jsonl:3", marker["firstKnownFailure"]["eventRef"])
            self.assertEqual(0, result["integrity.json"]["emitters"][0]["missingWithinObservedRange"])
            self.assertNotIn("source.cvl", json.dumps(result))

    def test_sha256_mismatch_is_fatal(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = pathlib.Path(tmp) / "test.zip"
            make_zip(f, [event(1, "session.started")],
                     lambda m: m["segments"][0].update(sha256="0"*64))
            with self.assertRaisesRegex(analyzer.BundleError, "sha256_integrity_failure"):
                analyzer.analyze(f)

    def test_unsafe_member_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = pathlib.Path(tmp) / "test.zip"
            make_zip(f, [event(1, "session.started")], extra={"../source.cvl": "private"})
            with self.assertRaisesRegex(analyzer.BundleError, "unsafe_archive_member"):
                analyzer.analyze(f)

    def test_duplicate_sequence_and_unsupported_schema_are_reported(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = pathlib.Path(tmp) / "test.zip"
            make_zip(f, [event(1, "session.started"), event(1, "diagnostic.marker"),
                         event(2, "fault", schemaVersion=3)])
            result = analyzer.analyze(f)
            self.assertEqual(["duplicate_producer_sequence", "unsupported_schema"],
                             [x["reason"] for x in result["integrity.json"]["invalidEvents"]])
            self.assertEqual([], result["incident-index.json"]["markers"])

    def test_unknown_sequence_gap_not_called_root_cause(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = pathlib.Path(tmp) / "test.zip"
            make_zip(f, [event(2, "session.started"), event(4, "session.ended")])
            result = analyzer.analyze(f)
            self.assertEqual(1, result["integrity.json"]["emitters"][0]["missingWithinObservedRange"])
            self.assertIn("producer_sequence_gaps_unknown_cause", result["integrity.json"]["warnings"])

    def test_standalone_cli_creates_three_reports(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = pathlib.Path(tmp) / "test.zip"
            make_zip(f, [event(1, "diagnostic.marker", clientActionId="marker-"+"b"*32,
                               attributes=dict(category="performance"))])
            dest = pathlib.Path(tmp) / "results"
            run = subprocess.run([sys.executable, str(SCRIPT), str(f), str(dest)],
                                 capture_output=True, text=True)
            self.assertEqual(0, run.returncode, run.stderr)
            self.assertEqual({"summary.md", "integrity.json", "incident-index.json", "operations.json", "diagnostic-report.txt"},
                             {p.name for p in dest.iterdir()})
            self.assertEqual(1, len(json.loads((dest / "incident-index.json").read_text())["markers"]))

    def test_unsupported_manifest_fails_closed(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = pathlib.Path(tmp) / "test.zip"
            make_zip(f, [event(1, "session.started")],
                     lambda m: m.update(formatVersion=2))
            with self.assertRaisesRegex(analyzer.BundleError, "unsupported_bundle_format"):
                analyzer.analyze(f)


    def test_explicit_f12_failure_keeps_evidence_ref_and_producer_order(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = pathlib.Path(tmp) / "test.zip"
            uid = "a"*32 + "/rpc/42"
            make_zip(f, [
                event(4, "rpc.response.sent", requestUid=uid, outcome="success"),
                event(1, "rpc.request.received", requestUid=uid, outcome="received"),
                event(2, "request.started", requestUid=uid, operationId="op-1", stage="definition", outcome="started"),
                event(3, "request.completed", requestUid=uid, operationId="op-1", stage="definition",
                      outcome="not_found", reason="symbol_lookup_miss")
            ])
            data = analyzer.analyze(f)
            req = data["operations.json"]["requests"][0]
            self.assertEqual("events/segment-0000.jsonl:4", req["firstObservedFailure"]["eventRef"])
            self.assertTrue(req["terminalObserved"])
            self.assertEqual(4, len(req["eventRefs"]))
            self.assertIn("symbol_lookup_miss", data["diagnostic-report.txt"])
            self.assertIn("FULL EVENT TIMELINE: not embedded", data["diagnostic-report.txt"])

    def test_temporal_marker_does_not_prove_request_cause(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = pathlib.Path(tmp) / "test.zip"
            uid = "a"*32 + "/rpc/42"
            make_zip(f, [event(1, "request.completed", requestUid=uid,
                               outcome="not_found", reason="symbol_lookup_miss"),
                         event(2, "diagnostic.marker", clientActionId="marker-" + "b"*32,
                               attributes={"category": "definition"})])
            entry = analyzer.analyze(f)["incident-index.json"]["markers"][0]["requestCandidates"][0]
            self.assertEqual("temporal_only", entry["relation"])
            self.assertEqual("insufficient_evidence", entry["rootCause"])
            self.assertEqual("symbol_lookup_miss", entry["firstObservedFailure"]["reason"])

    def test_shared_snapshot_never_joins_independent_requests(self):
        with tempfile.TemporaryDirectory() as tmp:
            f = pathlib.Path(tmp) / "test.zip"
            a, b = "a"*32+"/rpc/1", "a"*32+"/rpc/2"
            make_zip(f, [event(1, "request.started", requestUid=a, snapshotId="snap-1"),
                         event(2, "request.started", requestUid=b, snapshotId="snap-1")])
            graph = analyzer.analyze(f)["operations.json"]
            self.assertEqual(2, graph["requestCount"])
            self.assertFalse(graph["requests"][0]["terminalObserved"])


if __name__ == "__main__":
    unittest.main()
