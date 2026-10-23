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


def execute_mode(command, mode, project_uri, document_uri, source, timeout,
                 warmup, samples):
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
        # This one request shape is identical for all 3 supported modes.
        request = {
            "textDocument": {"uri": document_uri},
            "position": {"line": 0, "character": 4},
            "context": {"triggerKind": 1},
        }
        def completion():
            elapsed, result = process.call("textDocument/completion", request)
            if not isinstance(result, dict) or not isinstance(result.get("items"), list):
                raise BenchmarkFailure("completion_result_invalid_or_unavailable")
            return round(elapsed, 3)

        for _ in range(warmup):
            completion()
        return [completion() for _ in range(samples)]
    finally:
        process.close()


def collect(command, repetitions=8, samples=50, warmup=10, timeout=20):
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
            for mode in order:
                values[mode] = execute_mode(command, mode, project_uri, doc_uri,
                    source, timeout, warmup, samples)
            runs.append({"repetition": repetition,
                         "modeOrder": order, "samplesUs": values})
    sdk = subprocess.run(["dotnet", "--version"], timeout=15,
                         stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                         check=True).stdout.decode("ascii", errors="replace").strip()
    return {
        "schemaVersion": 1, "evidenceKind": "measured",
        "workload": "completion", "fixtureClass": "small",
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
    parser.add_argument("--rpc-timeout", type=float, default=20)
    args = parser.parse_args()
    try:
        command = command_for(args.server)
        data = collect(command, args.repetitions, args.samples,
                       args.warmup, args.rpc_timeout)
        # Import the same evaluator used by the test suite. The current server
        # rejects repro, so incomplete four-mode evidence MUST NOT become PASS.
        from evaluate_inc33_perf import evaluate
        report = evaluate(data)
        if report["status"] != "INCONCLUSIVE" or report["reason"] != "missing_or_duplicated_mode":
            raise BenchmarkFailure("unexpected_four_mode_acceptance")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print("Measured off/basic/detailed LSP completion; repro unavailable.")
        print("Normative 4-mode gate: INCONCLUSIVE (missing_or_duplicated_mode).")
        return 0
    except (BenchmarkFailure, OSError, subprocess.SubprocessError) as error:
        # Never dump RPC bodies/paths. A failed run must not publish results.
        print("Benchmark collection failed: " + type(error).__name__,
              file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
