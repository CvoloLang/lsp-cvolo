#!/usr/bin/env python3
"""Explicit opt-in *real-process* LSP latency collector for Inc 3.3.

Runs a built cvolo-language-server binary, creates a temporary .cvlproj, and
measures client-observed completion RPC round trips for off/basic/detailed.
repro is deliberately NOT faked: the current server rejects that recording
mode. The output remains INCONCLUSIVE for the four-mode normative gate.

This collector is not run by public CI, never reads private user projects,
never enables source capture, never logs raw frames or source texts and makes
no claim that background server work has reached a steady state.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import queue
import re
import subprocess
import sys
import tempfile
import threading
import time
from urllib.parse import quote

MODES = ("off", "basic", "detailed")
WORKLOADS = ("completion", "document_symbol")
MAX_MESSAGE_BYTES = 8 * 1024 * 1024


class BenchmarkFailure(RuntimeError):
    pass


def alternating_modes(repetition):
    offset = repetition % len(MODES)
    return list(MODES[offset:] + MODES[:offset])


def command_for(server):
    path = Path(server).resolve(strict=True)
    if not path.is_file():
        raise BenchmarkFailure("server_is_not_a_file")
    return ["dotnet", str(path)] if path.suffix.lower() == ".dll" else [str(path)]


def frame(payload):
    data = json.dumps(payload, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
    if len(data) > MAX_MESSAGE_BYTES:
        raise BenchmarkFailure("request_too_large")
    return ("Content-Length: %d\r\n\r\n" % len(data)).encode("ascii") + data


def read_frame(stream):
    headers = {}
    total = 0
    while True:
        line = stream.readline(MAX_MESSAGE_BYTES + 1)
        if not line:
            raise EOFError("server_stdout_closed")
        total += len(line)
        if total > 16 * 1024 or not line.endswith(b"\n"):
            raise BenchmarkFailure("invalid_frame_header")
        if line in (b"\r\n", b"\n"):
            break
        if b":" not in line:
            raise BenchmarkFailure("invalid_frame_header")
        key, value = line.split(b":", 1)
        name = key.strip().lower()
        if name in headers:
            raise BenchmarkFailure("duplicate_frame_header")
        headers[name] = value.strip()
    try:
        length = int(headers[b"content-length"])
    except (KeyError, ValueError) as error:
        raise BenchmarkFailure("missing_content_length") from error
    if length < 0 or length > MAX_MESSAGE_BYTES:
        raise BenchmarkFailure("invalid_frame_length")
    # Unbuffered subprocess pipes may legitimately return fewer bytes than
    # requested. Collect exactly Content-Length, otherwise benchmark runs can
    # fail nondeterministically with a false short-frame classification.
    chunks = []
    remaining = length
    while remaining:
        chunk = stream.read(remaining)
        if not chunk:
            raise EOFError("short_server_frame")
        chunks.append(chunk)
        remaining -= len(chunk)
    result = json.loads(b"".join(chunks))
    if not isinstance(result, dict):
        raise BenchmarkFailure("non_object_rpc_message")
    return result


class RpcProcess:
    """Single outstanding request, continuously pumped transport, deadlines."""
    def __init__(self, command, timeout):
        self.timeout = timeout
        self.process = subprocess.Popen(
            command + ["--stdio"], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)
        self.messages = queue.Queue(maxsize=512)
        self.request_id = 0
        self._reader = threading.Thread(target=self._pump, daemon=True)
        self._reader.start()

    def _pump(self):
        try:
            while True:
                message = read_frame(self.process.stdout)
                try:
                    self.messages.put(message, timeout=1)
                except queue.Full:
                    raise BenchmarkFailure("response_queue_full")
        except Exception as error:
            # Classify only: the transport may carry private diagnostics and
            # must never be included in benchmark output or exception text.
            try:
                self.messages.put_nowait({"_transportFailure": type(error).__name__})
            except queue.Full:
                pass

    def send(self, body):
        assert self.process.stdin is not None
        self.process.stdin.write(frame(body))
        self.process.stdin.flush()

    def notify(self, method, params=None):
        self.send({"jsonrpc": "2.0", "method": method, "params": params or {}})

    def call(self, method, params=None):
        self.request_id += 1
        rid = self.request_id
        start = time.perf_counter_ns()
        self.send({"jsonrpc": "2.0", "id": rid, "method": method,
                   "params": params or {}})
        deadline = time.monotonic() + self.timeout
        while True:
            wait = deadline - time.monotonic()
            if wait <= 0:
                raise BenchmarkFailure("rpc_deadline_exceeded")
            try:
                response = self.messages.get(timeout=wait)
            except queue.Empty as error:
                raise BenchmarkFailure("rpc_deadline_exceeded") from error
            if "_transportFailure" in response:
                raise BenchmarkFailure("rpc_transport_failed")
            if "method" in response and "id" in response:
                # Respond to server-initiated requests so unrelated
                # workspace/configuration does not deadlock this collector.
                self.send({"jsonrpc": "2.0", "id": response["id"],
                           "result": [] if response["method"] == "workspace/configuration"
                                     else None})
                continue
            if response.get("id") != rid:
                # Server-to-client notifications can arrive between replies.
                if "id" not in response and "method" in response:
                    continue
                raise BenchmarkFailure("unexpected_rpc_response_identity")
            if "error" in response:
                raise BenchmarkFailure("server_rejected_" +
                    re.sub(r"[^a-z0-9_]", "_", method.lower())[:48])
            return (time.perf_counter_ns() - start) / 1000.0, response.get("result")

    def close(self):
        if self.process.poll() is None:
            try:
                self.process.terminate()
                self.process.wait(timeout=3)
            except (subprocess.TimeoutExpired, OSError):
                self.process.kill()
                self.process.wait(timeout=3)
        if self.process.stdin:
            self.process.stdin.close()
        if self.process.stdout:
            self.process.stdout.close()


def server_provenance(command):
    probe = subprocess.run(command + ["--version", "--json"], timeout=15,
                           stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                           check=True)
    try:
        data = json.loads(probe.stdout)
    except (ValueError, UnicodeError) as error:
        raise BenchmarkFailure("invalid_server_version_json") from error
    sha = data.get("languageServerCommit")
    if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise BenchmarkFailure("server_artifact_has_no_full_commit_sha")
    return sha


def wait_for_document_applied(process, timeout):
    """A sent didOpen is not proof that concurrent dispatch has settled."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        _, snapshot = process.call("cvolo/observability/flightRecorderSnapshot.v1")
        if not isinstance(snapshot, dict):
            raise BenchmarkFailure("invalid_flight_snapshot")
        for event in snapshot.get("events", []):
            if event.get("kind") == "did_open" and event.get("stage") == "handler_settled":
                if event.get("outcome") != "applied":
                    raise BenchmarkFailure("benchmark_document_open_rejected")
                return
        time.sleep(.02)
    raise BenchmarkFailure("document_open_not_confirmed")


def workload_request(workload, document_uri):
    if workload == "completion":
        return "textDocument/completion", {
            "textDocument": {"uri": document_uri},
            "position": {"line": 0, "character": 4},
            "context": {"triggerKind": 1},
        }
    if workload == "document_symbol":
        return "textDocument/documentSymbol", {
            "textDocument": {"uri": document_uri},
        }
    raise BenchmarkFailure("unsupported_benchmark_workload")


def valid_workload_result(workload, result):
    if workload == "completion":
        return isinstance(result, dict) and isinstance(result.get("items"), list)
    if workload == "document_symbol":
        # The fixture contains a top-level Main declaration. An empty outline
        # may be a successful RPC but is not evidence that semantic document
        # analysis actually ran; reject rather than benchmark a no-op.
        return (isinstance(result, list) and len(result) > 0
                and all(isinstance(item, dict) and isinstance(item.get("name"), str)
                        and bool(item["name"]) for item in result))
    return False


RESOURCE_METHOD = "cvolo/observability/flightResourceSnapshot.v1"
CUMULATIVE_RESOURCE_FIELDS = (
    "processCpuTimeTicks", "totalAllocatedBytes",
    "gen0Collections", "gen1Collections", "gen2Collections",
)
GAUGE_RESOURCE_FIELDS = ("managedHeapBytes", "workingSetBytes")


def resource_deltas(first, last, measured_count):
    """Process-wide counters around measured requests. Not attribution.

    Both snapshots are requested OUTSIDE the timed samples. Managed heap and
    working set are point samples rather than high-water marks, and CPU time
    includes background work by unrelated server threads.
    """
    if (not isinstance(first, dict) or not isinstance(last, dict)
        or not isinstance(measured_count, int) or measured_count <= 0):
        raise BenchmarkFailure("invalid_resource_snapshot")
    for snap in (first, last):
        if (snap.get("protocolVersion") != 1
            or snap.get("scope") != "same_process_cumulative_non_atomic"
            or any(snap.get(name) is not False for name in (
                "resourceAttributionToRequestProven",
                "isolatedCpuMeasurementProven", "peakMemoryObserved"))):
            raise BenchmarkFailure("invalid_resource_scope")
        required = ("monotonicTicks", "monotonicFrequency",
                    *CUMULATIVE_RESOURCE_FIELDS, *GAUGE_RESOURCE_FIELDS)
        for key in required:
            value = snap.get(key)
            if type(value) is not int or value < 0:
                raise BenchmarkFailure("invalid_resource_counter")
    if (first["monotonicFrequency"] <= 0
        or first["monotonicFrequency"] != last["monotonicFrequency"]
        or last["monotonicTicks"] <= first["monotonicTicks"]):
        raise BenchmarkFailure("invalid_resource_clock")

    deltas = {}
    for name in CUMULATIVE_RESOURCE_FIELDS:
        difference = last[name] - first[name]
        if difference < 0:
            raise BenchmarkFailure("resource_counter_regressed")
        deltas[name + "Delta"] = difference
    elapsed_us = ((last["monotonicTicks"] - first["monotonicTicks"])
                  * 1_000_000 / first["monotonicFrequency"])
    deltas.update({
        "sampleIntervalUs": round(elapsed_us, 3),
        "processCpuTimeUs": round(deltas["processCpuTimeTicksDelta"] / 10, 3),
        "allocatedBytesPerRequest": round(
            deltas["totalAllocatedBytesDelta"] / measured_count, 3),
        "processCpuUsPerRequest": round(
            deltas["processCpuTimeTicksDelta"] / 10 / measured_count, 3),
        "managedHeapBytesBefore": first["managedHeapBytes"],
        "managedHeapBytesAfter": last["managedHeapBytes"],
        "workingSetBytesBefore": first["workingSetBytes"],
        "workingSetBytesAfter": last["workingSetBytes"],
        "attribution": "process_wide_not_per_request",
        "sampling": "two_non_atomic_points_excluding_timed_samples",
        "peakMemoryObserved": False,
        "regressionBudgetPass": False,
    })
    return deltas


def execute_mode(command, mode, project_uri, document_uri, source, timeout,
                 warmup, samples, workload="completion", resource_sink=None):
    process = RpcProcess(command, timeout)
    try:
        _, init = process.call("initialize", {
            "processId": None, "rootUri": project_uri,
            "capabilities": {"workspace": {}, "textDocument": {}},
        })
        if not isinstance(init, dict) or "capabilities" not in init:
            raise BenchmarkFailure("invalid_initialize_response")
        process.notify("initialized")
        process.notify("textDocument/didOpen", {
            "textDocument": {"uri": document_uri, "languageId": "cvolo",
                             "version": 1, "text": source},
        })
        wait_for_document_applied(process, timeout)
        _, policy = process.call("cvolo/observability/setRecording.v1", {
            "protocolVersion": 1, "mode": mode, "captureSources": False,
            "capturePaths": False, "forensicScope": False,
        })
        if (not isinstance(policy, dict) or policy.get("status") != "applied"
                or policy.get("effectiveMode") != mode):
            raise BenchmarkFailure("recording_mode_not_fully_applied")
        # Identical request payload for all supported recording modes.
        # Completion has a deliberate 120-ms quiet period in production;
        # documentSymbol does not and is useful for measuring a lower-latency
        # path without modifying any LSP scheduling/debounce semantics.
        method, request = workload_request(workload, document_uri)

        def measured_request():
            elapsed, result = process.call(method, request)
            if not valid_workload_result(workload, result):
                raise BenchmarkFailure("benchmark_result_invalid_or_unavailable")
            return round(elapsed, 3)

        for _ in range(warmup):
            measured_request()
        _, before = process.call(RESOURCE_METHOD)
        latencies = [measured_request() for _ in range(samples)]
        _, after = process.call(RESOURCE_METHOD)
        resources = resource_deltas(before, after, samples)
        if resource_sink is not None:
            resource_sink.update(resources)
        return latencies
    finally:
        process.close()


def collect(command, repetitions=8, samples=50, warmup=10, timeout=20,
            workload="completion"):
    if workload not in WORKLOADS:
        raise BenchmarkFailure("unsupported_benchmark_workload")
    if repetitions < 1 or samples < 1 or warmup < 0:
        raise BenchmarkFailure("invalid_measurement_parameters")
    sha = server_provenance(command)
    source = "int Main() { return 0; }\n"
    with tempfile.TemporaryDirectory(prefix="cvolo-inc33-benchmark-") as temp:
        workspace = Path(temp)
        project = workspace / "App.cvlproj"
        document = workspace / "main.cvl"
        project.write_text('<Project Sdk="Cvolo.Sdk" />\n', encoding="utf-8")
        document.write_text(source, encoding="utf-8")
        project_uri = workspace.as_uri() + "/"
        doc_uri = document.as_uri()
        fixture_sha = hashlib.sha256(
            project.read_bytes() + b"\x00" + document.read_bytes()).hexdigest()
        runs = []
        for repetition in range(repetitions):
            order = alternating_modes(repetition)
            values = {}
            resources = {}
            for mode in order:
                mode_resources = {}
                values[mode] = execute_mode(command, mode, project_uri, doc_uri,
                    source, timeout, warmup, samples, workload, mode_resources)
                resources[mode] = mode_resources
            runs.append({"repetition": repetition,
                         "modeOrder": order, "samplesUs": values,
                         "processResourceDeltas": resources})
    sdk = subprocess.run(["dotnet", "--version"], timeout=15,
                         stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                         check=True).stdout.decode("ascii", errors="replace").strip()
    return {
        "schemaVersion": 1, "evidenceKind": "measured",
        "workload": workload, "fixtureClass": "small",
        "fixtureSha256": fixture_sha, "buildSha": sha,
        "host": {"os": platform.system(), "cpu": platform.processor() or
                 platform.machine(),
                 "runnerId": "runner-" + hashlib.sha256(
                     (platform.node() or "unknown").encode("utf-8")
                 ).hexdigest()[:16]},
        "sdk": sdk, "runs": runs,
        "unsupportedMode": "repro", "captureScope": "non_sensitive",
        "correlationScope": "client_observed_round_trip",
        "completeNormativeEvidence": False,
        "resourceSamplingScope": "process_wide_cpu_gc_allocation_and_two_memory_points",
        "resourceAttributionToRequestsProven": False,
    }


def exploratory_resource_summary(data):
    """Summarize process-wide resource measurements without per-RPC attribution."""
    from statistics import median

    runs = data["runs"]
    populated = [
        isinstance(run.get("processResourceDeltas"), dict)
        for run in runs
    ]
    if not any(populated):
        return {
            "status": "not_collected",
            "scope": "process_wide_not_per_request",
            "normativeBudgetPass": False,
        }
    if not all(populated):
        raise BenchmarkFailure("incomplete_resource_evidence")
    per_mode = {}
    for mode in MODES:
        samples = [run["processResourceDeltas"].get(mode) for run in runs]
        if any(not isinstance(sample, dict) for sample in samples):
            raise BenchmarkFailure("missing_resource_mode")
        for sample in samples:
            if (sample.get("attribution") != "process_wide_not_per_request"
                or sample.get("regressionBudgetPass") is not False
                or sample.get("peakMemoryObserved") is not False):
                raise BenchmarkFailure("invalid_resource_summary_scope")
            for field in ("allocatedBytesPerRequest", "processCpuUsPerRequest"):
                value = sample.get(field)
                if not isinstance(value, (int, float)) or isinstance(value, bool):
                    raise BenchmarkFailure("invalid_resource_summary_value")
                if not math.isfinite(value) or value < 0:
                    raise BenchmarkFailure("invalid_resource_summary_value")
        per_mode[mode] = {
            "runs": len(samples),
            "medianAllocatedBytesPerRequest": round(
                median(sample["allocatedBytesPerRequest"] for sample in samples), 2),
            "medianProcessCpuUsPerRequest": round(
                median(sample["processCpuUsPerRequest"] for sample in samples), 2),
            "observedGen0Collections": sum(sample["gen0CollectionsDelta"] for sample in samples),
            "observedGen1Collections": sum(sample["gen1CollectionsDelta"] for sample in samples),
            "observedGen2Collections": sum(sample["gen2CollectionsDelta"] for sample in samples),
        }
    baseline = per_mode["off"]["medianAllocatedBytesPerRequest"]
    if baseline <= 0:
        raise BenchmarkFailure("invalid_resource_baseline")
    for mode in MODES:
        per_mode[mode]["observedAllocationDeltaPercent"] = round(
            (per_mode[mode]["medianAllocatedBytesPerRequest"] / baseline - 1) * 100, 2)
    return {
        "status": "descriptive_only",
        "scope": "process_wide_not_per_request",
        "sampling": "two_non_atomic_points_excluding_timed_samples",
        "peakMemoryObserved": False,
        "normativeBudgetPass": False,
        "perMode": per_mode,
    }


def exploratory_summary(data):
    """Descriptive statistics only, NOT the normative four-mode verdict.

    This summary remains useful when repro cannot be enabled. No threshold,
    confidence claim or hidden PASS is inferred from incomplete modes.
    """
    import math

    def percentile(values, fraction):
        ordered = sorted(values)
        return ordered[max(0, math.ceil(len(ordered) * fraction) - 1)]

    all_samples = {}
    for mode in MODES:
        samples = []
        for run in data["runs"]:
            samples.extend(run["samplesUs"][mode])
        if not samples or any(not isinstance(x, (float, int))
                              or isinstance(x, bool) or not math.isfinite(x)
                              or x <= 0 for x in samples):
            raise BenchmarkFailure("invalid_exploratory_samples")
        all_samples[mode] = samples

    baseline = percentile(all_samples["off"], .95)
    # Use the same fixed, reviewed noise threshold as the evaluator. This is
    # descriptive only: shared GitHub hosts cannot establish a stable baseline.
    from evaluate_inc33_perf import SPEC_PATH
    spec = json.loads(SPEC_PATH.read_text(encoding="utf-8"))
    per_run_off = [
        percentile(run["samplesUs"]["off"], .95) for run in data["runs"]
    ]
    baseline_spread = (max(per_run_off) - min(per_run_off)) / baseline
    noisy_baseline = baseline_spread > spec["maxBaselineRelativeCiWidth"]
    summaries = {}
    for mode, samples in all_samples.items():
        p95 = percentile(samples, .95)
        summaries[mode] = {
            "sampleCount": len(samples),
            "p50Us": percentile(samples, .50),
            "p95Us": p95,
            "p99Us": percentile(samples, .99),
            "observedP95DeltaPercent": round((p95 / baseline - 1) * 100, 5),
        }
    return {
        "status": "DESCRIPTIVE_ONLY",
        "scope": "single_workload_uncontrolled_host",
        "completedModes": list(MODES),
        "missingModes": ["repro"],
        "latencyScope": "client_observed_lsp_round_trip",
        "perMode": summaries,
        "baselineRelativeP95RunSpread": round(baseline_spread, 6),
        "baselineNoiseThreshold": spec["maxBaselineRelativeCiWidth"],
        "noisyBaseline": noisy_baseline,
        "observedLatencyBudgetVerdict": "INCONCLUSIVE",
        "confidenceBoundedBudgetVerdict": False,
        "normativeRegressionPass": False,
        "processResources": exploratory_resource_summary(data),
    }


def incomplete_collection_evidence(error):
    """Safe classified failure, never source text, stderr or absolute paths."""
    raw = str(error) if isinstance(error, BenchmarkFailure) else ""
    code = raw if re.fullmatch(r"[a-z0-9_]{1,80}", raw) else "unclassified_error"
    return {
        "schemaVersion": 1,
        "status": "INCONCLUSIVE",
        "reason": "collection_failed",
        "failureCode": code,
        "sampleScope": "none",
        "normativeBudgetApproved": False,
        "allWorkloadsComplete": False,
        "endToEndRegressionGatePassed": False,
        "noMeasuredSamplesPublished": True,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", required=True,
                        help="Path to a built cvolo-language-server executable or .dll")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--repetitions", type=int, default=8)
    parser.add_argument("--samples", type=int, default=50)
    parser.add_argument("--warmup", type=int, default=10)
    parser.add_argument("--workload", choices=WORKLOADS, default="completion")
    parser.add_argument("--rpc-timeout", type=float, default=20)
    args = parser.parse_args()
    try:
        command = command_for(args.server)
        data = collect(command, args.repetitions, args.samples,
                       args.warmup, args.rpc_timeout, args.workload)
        # Import the same evaluator used by the test suite. The current server
        # rejects repro, so incomplete four-mode evidence MUST NOT become PASS.
        from evaluate_inc33_perf import evaluate
        report = evaluate(data)
        if report["status"] != "INCONCLUSIVE" or report["reason"] != "missing_or_duplicated_mode":
            raise BenchmarkFailure("unexpected_four_mode_acceptance")
        report["exploratoryMeasurements"] = exploratory_summary(data)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print("Measured off/basic/detailed LSP " + args.workload + "; repro unavailable.")
        print("Normative 4-mode gate: INCONCLUSIVE (missing_or_duplicated_mode).")
        return 0
    except (BenchmarkFailure, OSError, subprocess.SubprocessError) as error:
        # A failed run is NOT_RUN/INCONCLUSIVE, never a silent success or
        # stale output from a previous run. Export only whitelisted fields;
        # exception text and raw JSON-RPC frames can contain sensitive data.
        args.output.unlink(missing_ok=True)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(
            json.dumps(incomplete_collection_evidence(error), indent=2) + "\n",
            encoding="utf-8",
        )
        print("Benchmark collection failed: " + type(error).__name__,
              file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
