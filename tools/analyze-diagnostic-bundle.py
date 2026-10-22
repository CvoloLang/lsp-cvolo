#!/usr/bin/env python3
"""Standalone Cvolo diagnostic-bundle v1 analyzer, Python 3.11+ stdlib only.

Usage: python tools/analyze-diagnostic-bundle.py <archive.zip> <report-directory>
The archive is read without extracting files or executing content.
"""
import argparse
import hashlib
import json
import re
import sys
import zipfile
from causal_graph import make_graph, marker_request_candidates
from dependency_audit import inspect_dependencies
from pathlib import Path
from datetime import datetime

MAX_BYTES = 128 * 1024 * 1024
MAX_EVENTS = 250000
SEGMENT = re.compile(r"events/segment-[0-9]{4}\.jsonl\Z")
WORD = re.compile(r"[a-z][a-z0-9_.-]{0,90}\Z")
MARKER = re.compile(r"marker-[a-f0-9]{32}\Z")
REQUEST = re.compile(r"[a-f0-9]{32}/rpc/[0-9]{1,16}\Z")
OP = re.compile(r"(?:op|snap|analysis|idx|index|gen)-[a-zA-Z0-9._-]{1,100}\Z")
ANALYSIS = re.compile(r"[a-f0-9]{32}/analysis/[0-9]{1,16}\Z")


class BundleError(ValueError):
    pass


def word(value):
    return value if isinstance(value, str) and WORD.fullmatch(value) else "unknown"


def safe_id(value, pattern):
    return value if isinstance(value, str) and pattern.fullmatch(value) else None


def event_time(value):
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp() * 1000 if isinstance(value, str) else None
    except (ValueError, OverflowError):
        return None


def read_bundle(archive_path):
    if Path(archive_path).stat().st_size > MAX_BYTES:
        raise BundleError("archive_too_large")
    try:
        with zipfile.ZipFile(archive_path) as archive:
            files = archive.infolist()
            names = [x.filename for x in files]
            if not 2 <= len(files) <= 257 or len(set(names)) != len(names) or names.count("manifest.json") != 1:
                raise BundleError("invalid_archive_members")
            if any(name != "manifest.json" and not SEGMENT.fullmatch(name) for name in names):
                raise BundleError("unsafe_archive_member")
            if any(x.is_dir() or x.flag_bits & 1 or x.file_size > MAX_BYTES
                   or x.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED) for x in files):
                raise BundleError("unsupported_archive_member")
            if sum(x.file_size for x in files) > MAX_BYTES:
                raise BundleError("expanded_size_limit")
            members = {}
            for info in files:
                chunks, size = [], 0
                with archive.open(info) as source:
                    while chunk := source.read(65536):
                        size += len(chunk)
                        if size > info.file_size or size > MAX_BYTES:
                            raise BundleError("decompression_size_limit")
                        chunks.append(chunk)
                if size != info.file_size:
                    raise BundleError("segment_size_mismatch")
                members[info.filename] = b"".join(chunks)
    except (zipfile.BadZipFile, RuntimeError, EOFError) as exc:
        raise BundleError("corrupt_zip") from exc
    try:
        manifest = json.loads(members["manifest.json"].decode("utf-8"))
    except (UnicodeError, ValueError) as exc:
        raise BundleError("invalid_manifest_json") from exc
    if not isinstance(manifest, dict) or (manifest.get("format"), manifest.get("formatVersion")) != ("cvolo.diagnostic-bundle", 1):
        raise BundleError("unsupported_bundle_format")
    if manifest.get("hashAlgorithm") != "sha256" or manifest.get("integrityScope") != "uncompressed_archive_members":
        raise BundleError("unsupported_integrity_contract")
    segments = manifest.get("segments")
    if not isinstance(segments, list) or not 1 <= len(segments) <= 256:
        raise BundleError("invalid_segments")
    expected = {"manifest.json"}
    for item in segments:
        if not isinstance(item, dict):
            raise BundleError("invalid_segment")
        name, digest, size = item.get("entry"), item.get("sha256"), item.get("sizeBytes")
        if not isinstance(name, str) or not SEGMENT.fullmatch(name) or name in expected:
            raise BundleError("invalid_segment_name")
        if not isinstance(digest, str) or not re.fullmatch("[a-f0-9]{64}", digest):
            raise BundleError("invalid_sha256")
        if type(size) is not int or size < 0 or name not in members:
            raise BundleError("missing_segment")
        data = members[name]
        if len(data) != size or hashlib.sha256(data).hexdigest() != digest:
            raise BundleError("sha256_integrity_failure")
        expected.add(name)
    if expected != set(members):
        raise BundleError("unexpected_archive_members")
    if not isinstance(manifest.get("checkpoint"), dict) or type(manifest["checkpoint"].get("recordingEpoch")) is not int:
        raise BundleError("invalid_checkpoint")
    return manifest, members


def analyze(path):
    manifest, members = read_bundle(path)
    records, invalid, seen, majors = [], [], set(), set()
    for segment in manifest["segments"]:
        name = segment["entry"]
        try:
            content = members[name].decode("utf-8")
        except UnicodeError as exc:
            raise BundleError("invalid_jsonl_utf8") from exc
        for line_number, line in enumerate(content.splitlines(), 1):
            if not line.strip():
                continue
            if len(records) >= MAX_EVENTS:
                raise BundleError("event_count_limit")
            ref = f"{name}:{line_number}"
            try:
                row = json.loads(line)
            except ValueError:
                invalid.append({"eventRef": ref, "reason": "invalid_json"})
                continue
            if not isinstance(row, dict):
                invalid.append({"eventRef": ref, "reason": "invalid_envelope"})
                continue
            major = row.get("schemaVersion")
            majors.add(major if isinstance(major, int) else "unknown")
            if major != 2 or type(row.get("schemaMinor")) is not int or row["schemaMinor"] < 0:
                invalid.append({"eventRef": ref, "reason": "unsupported_schema"})
                continue
            emitter, sequence, epoch = row.get("emitterInstanceId"), row.get("producerSequence"), row.get("recordingEpoch")
            if not isinstance(emitter, str) or not emitter or type(sequence) is not int or sequence < 1 or type(epoch) is not int or epoch < 0 or not isinstance(row.get("event"), str):
                invalid.append({"eventRef": ref, "reason": "invalid_event_identity"})
                continue
            key = emitter, sequence
            if key in seen:
                invalid.append({"eventRef": ref, "reason": "duplicate_producer_sequence"})
                continue
            seen.add(key)
            attrs = row.get("attributes")
            records.append({
                "eventRef": ref, "_emitter": emitter, "_seq": sequence,
                "_time": event_time(row.get("timestampUtc")), "recordingEpoch": epoch,
                "event": word(row["event"]), "outcome": word(row.get("outcome")),
                "reason": word(row.get("reason")),
                "markerId": safe_id(row.get("clientActionId"), MARKER),
                "requestUid": safe_id(row.get("requestUid"), REQUEST),
                "operationId": safe_id(row.get("operationId"), OP),
                "parentOperationId": safe_id(row.get("parentOperationId"), OP),
                "dependencyId": safe_id(attrs.get("dependencyId"), OP) if isinstance(attrs, dict) else None,
                "analysisId": safe_id(row.get("analysisId"), ANALYSIS),
                "snapshotId": safe_id(row.get("snapshotId"), OP),
                "stage": word(row.get("stage")),
                "documentVersion": row.get("documentVersion") if type(row.get("documentVersion")) is int and row["documentVersion"] >= 0 else None,
                "category": word(attrs.get("category") if isinstance(attrs, dict) else None)
            })
    # Physical JSONL order is not producer order: P0 can overtake P1/P2.
    records.sort(key=lambda e: (e["_emitter"], e["_seq"]))
    graph, request_events = make_graph(records)
    graph["dependencyAudit"] = inspect_dependencies(records)
    markers = []
    for marker in (x for x in records if x["event"] == "diagnostic.marker" and x["markerId"]):
        nearby_events, request_candidates = marker_request_candidates(marker, records, request_events)
        nearby = [{k: e[k] for k in ("eventRef", "event", "outcome", "reason", "requestUid", "operationId", "snapshotId")} | {"relation": "temporal_only"} for e in nearby_events]
        fault = next((e for e in nearby if e["outcome"] in ("faulted", "rejected", "not_found") or e["event"].endswith(".faulted")), None)
        markers.append({
            "markerId": marker["markerId"], "category": marker["category"],
            "eventRef": marker["eventRef"], "recordingEpoch": marker["recordingEpoch"],
            "incidentWindowMs": 10000, "association": "temporal_only", "candidates": nearby, "requestCandidates": request_candidates,
            "firstKnownFailure": {"eventRef": fault["eventRef"], "reason": fault["reason"],
                                  "outcome": fault["outcome"], "evidence": "observed_outcome_not_root_cause"} if fault else None,
            "rootCause": "insufficient_evidence"
        })
    streams = {}
    for item in records:
        streams.setdefault(item["_emitter"], []).append(item["_seq"])
    emitters = [{"emitterHash": hashlib.sha256(emitter.encode()).hexdigest()[:12],
                 "first": min(seq), "last": max(seq), "count": len(seq),
                 "missingWithinObservedRange": max(seq) - min(seq) + 1 - len(seq)}
                for emitter, seq in sorted(streams.items())]
    warnings = []
    if manifest.get("completenessVerified") is not True:
        warnings.append("completeness_not_verified")
    if manifest.get("privacyAuditVerified") is not True:
        warnings.append("privacy_audit_not_verified")
    checkpoint = manifest["checkpoint"]
    if checkpoint.get("replayIncomplete") or any(checkpoint.get(k, 0) for k in ("droppedP0", "droppedP1", "droppedP2", "writerLost")):
        warnings.append("checkpoint_reports_incomplete_trace")
    if invalid:
        warnings.append("invalid_or_duplicate_event_records")
    if any(x["missingWithinObservedRange"] for x in emitters):
        warnings.append("producer_sequence_gaps_unknown_cause")
    if any(x["_time"] is None for x in records):
        warnings.append("invalid_or_missing_timestamps")
    if any(s != 2 for s in majors):
        warnings.append("unsupported_schema_present")
    if graph["dependencyAudit"]["unresolvedLinks"]:
        warnings.append("unresolved_operation_links")
    if graph["dependencyAudit"]["lifecycleFindings"]:
        warnings.append("operation_lifecycle_uncertainty")
    integrity = {
        "analyzerVersion": 1, "archiveHashesVerified": True,
        "payloadOrder": "per_emitter_producer_sequence", "validEventCount": len(records),
        "invalidEvents": invalid, "emitters": emitters, "warnings": warnings,
        "replayCompleteness": "unverified", "causalAttribution": "insufficient_evidence"
    }
    index = {
        "analyzerVersion": 1, "incidentAssociationRule": "same_emitter_timestamp_window_only",
        "clockSkewCalibrated": False, "markers": markers,
        "limitations": ["temporal_proximity_is_not_causation", "rotated_away_history_unknown", "unknown_after_last_checkpoint"]
    }
    summary = [
        "# Cvolo diagnostic bundle - preliminary analysis", "",
        "**Integrity:** segment SHA-256 checks passed; the manifest has no trusted external signature.",
        "**Replay completeness:** unverified. **Privacy:** not independently audited.",
        "**Root cause:** insufficient evidence (timestamps are not causal proof).", "",
        f"Valid events: {len(records)}; invalid events: {len(invalid)}; markers: {len(markers)}.",
        "Warnings: " + (", ".join(warnings) if warnings else "none beyond completeness uncertainty") + ".", "",
        "## Diagnostic markers", ""
    ]
    for marker in markers:
        fault = marker["firstKnownFailure"]
        summary.append(f"- {marker['markerId']} ({marker['category']}), epoch {marker['recordingEpoch']}: "
                       + f"{len(marker['candidates'])} nearby events; "
                       + (f"first observed failure at {fault['eventRef']}" if fault else "no explicit failure observed")
                       + "; root cause unproven.")
    if not markers:
        summary.append("No valid diagnostic.marker found in retained segments.")
    summary.append("")
    summary += ["", "## Operation dependency audit", ""]
    audit = graph["dependencyAudit"]
    summary.append("Explicit links: " + str(audit["edgeCount"]) + "; unresolved: " + str(len(audit["unresolvedLinks"])) + "; lifecycle findings: " + str(len(audit["lifecycleFindings"])) + ".")
    summary += ["", "## Observed request chains", ""]
    for req in graph["requests"][:200]:
        failure = req["firstObservedFailure"]
        finding = ("first observed failure " + failure["eventRef"] + " outcome=" + failure["outcome"] + " reason=" + failure["reason"]) if failure else ("no explicit failure; terminal " + ("observed" if req["terminalObserved"] else "not observed"))
        summary.append("- " + req["requestUid"] + ": " + finding + "; root cause unproven.")
    summary_text = "\n".join(summary) + "\n"
    report = ("CVOLO DIAGNOSTIC REPORT v1\n"
              "CAUTION: marker proximity is temporal, not causal; privacy and replay completeness are unverified.\n\n"
              + summary_text + "\nINTEGRITY\n"
              + json.dumps(integrity, sort_keys=True, indent=2) + "\n\nINCIDENT INDEX\n"
              + json.dumps(index, sort_keys=True, indent=2) + "\n\nOPERATION GRAPH\n"
              + json.dumps(graph, sort_keys=True, indent=2)
              + "\n\nFULL EVENT TIMELINE: not embedded; original verified ZIP required. "
                "This standalone report is incomplete for replay.\n")
    return {"integrity.json": integrity, "incident-index.json": index,
            "operations.json": graph, "summary.md": summary_text,
            "diagnostic-report.txt": report}


def main(argv=None):
    parser = argparse.ArgumentParser(description="Offline Cvolo diagnostic analyzer")
    parser.add_argument("archive")
    parser.add_argument("output")
    args = parser.parse_args(argv)
    try:
        results = analyze(args.archive)
        destination = Path(args.output)
        destination.mkdir(parents=True, exist_ok=True)
        for filename, value in results.items():
            content = value if isinstance(value, str) else json.dumps(value, indent=2, ensure_ascii=True) + "\n"
            with (destination / filename).open("x", encoding="utf-8") as handle:
                handle.write(content)
        print("Cvolo diagnostic analysis written to the requested directory")
        return 0
    except BundleError as exc:
        print("Diagnostic bundle rejected: " + str(exc), file=sys.stderr)
        return 1
    except OSError:
        print("Diagnostic analyzer failed: io_error", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
