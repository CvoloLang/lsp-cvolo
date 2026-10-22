"""Privacy-filtered timeline, conservative invariants, observed duration percentiles.

Do not infer causality, missing history, clock frequency or CPU time.
"""
from collections import defaultdict
from datetime import datetime, timezone
from hashlib import sha256
from math import ceil, isfinite

MAX_EVIDENCE = 32
MAX_PERFORMANCE_GROUPS = 128


def _stamp(timestamp):
    if isinstance(timestamp, (int, float)) and isfinite(timestamp):
        try:
            return datetime.fromtimestamp(timestamp / 1000, timezone.utc).isoformat()
        except (ValueError, OverflowError, OSError):
            pass
    return None


def _safe_record(event):
    return {
        "eventRef": event["eventRef"],
        "emitterHash": sha256(event["_emitter"].encode("utf-8")).hexdigest()[:12],
        "producerSequence": event["_seq"],
        "recordingEpoch": event["recordingEpoch"],
        "timestampUtc": _stamp(event.get("_time")),
        "clockId": event.get("_clock"),
        "monotonicTicks": event.get("_ticks"),
        "event": event["event"],
        "stage": event.get("stage"),
        "outcome": event.get("outcome"),
        "reason": event.get("reason"),
        "requestUid": event.get("requestUid"),
        "operationId": event.get("operationId"),
        "parentOperationId": event.get("parentOperationId"),
        "dependencyId": event.get("dependencyId"),
        "analysisId": event.get("analysisId"),
        "snapshotId": event.get("snapshotId"),
        "documentVersion": event.get("documentVersion"),
        "markerId": event.get("markerId"),
        "category": event.get("category"),
        "elapsedUs": event.get("elapsedUs"),
    }


def _assertion(name, checked, findings):
    return {
        "invariant": name,
        "status": "fail" if findings else ("pass" if checked else "unknown"),
        "scope": "observed_events_only",
        "checkedSamples": checked,
        "findingCount": len(findings),
        "evidenceTruncated": len(findings) > MAX_EVIDENCE,
        "evidence": findings[:MAX_EVIDENCE],
    }


def _percentile(values, fraction):
    ordered = sorted(values)
    return ordered[max(0, ceil(fraction * len(ordered)) - 1)]


def audit_observed_events(records, graph):
    """Return one sanitized entry per validated record, assertions and durations.

    The caller validates and sorts records by emitter and producer sequence.
    Unclassified JSONL attributes and source contents are never propagated.
    """
    timeline = [_safe_record(e) for e in records]
    previous_ticks, previous_versions = {}, {}
    clock_failures, duration_failures, version_failures = [], [], []
    clock_checked = duration_checked = version_checked = 0
    durations = defaultdict(list)

    for event in records:
        ref = event["eventRef"]
        ticks, clock = event.get("_ticks"), event.get("_clock")
        if type(ticks) is int and isinstance(clock, str) and clock:
            key = event["_emitter"], clock
            prior = previous_ticks.get(key)
            if prior is not None:
                clock_checked += 1
                if ticks < prior[0]:
                    clock_failures.append({"eventRef": ref, "previousEventRef": prior[1],
                                           "finding": "monotonic_ticks_regressed"})
            previous_ticks[key] = ticks, ref

        elapsed = event.get("elapsedUs")
        if type(elapsed) is int:
            duration_checked += 1
            if elapsed < 0:
                duration_failures.append({"eventRef": ref, "finding": "negative_elapsed_us"})
            elif event["event"] in ("operation.completed", "operation.faulted", "operation.cancelled"):
                durations[event["_emitter"], event.get("stage") or "unknown"].append((elapsed, ref))

        kind = event["event"]
        if ((kind == "document.did_open" and event.get("outcome") == "applied")
                or (kind == "document.did_change.applied" and event.get("outcome") == "applied")):
            session, version = event.get("documentSessionId"), event.get("documentVersion")
            if isinstance(session, str) and session and type(version) is int:
                key = event["_emitter"], session
                prior = previous_versions.get(key)
                if prior is not None:
                    version_checked += 1
                    if version < prior[0]:
                        version_failures.append({"eventRef": ref, "previousEventRef": prior[1],
                                                 "finding": "applied_document_version_regressed"})
                previous_versions[key] = version, ref

    lifecycle_findings = graph.get("dependencyAudit", {}).get("lifecycleFindings", [])
    terminal_failures = [
        {"eventRef": finding["eventRef"], "finding": "multiple_terminal_events_observed"}
        for finding in lifecycle_findings
        if finding.get("finding") == "multiple_terminal_events_observed"
    ]
    checks = [
        _assertion("clock_ticks_nondecreasing_within_emitter_and_clock", clock_checked, clock_failures),
        _assertion("observed_elapsed_us_nonnegative", duration_checked, duration_failures),
        _assertion("applied_document_versions_nondecreasing_per_session", version_checked, version_failures),
        _assertion("at_most_one_terminal_per_observed_operation", len(graph.get("operations", [])),
                   terminal_failures),
    ]
    assertions = {
        "schemaVersion": 1, "scope": "observed_valid_events_only", "invariants": checks,
        "allObservedChecksPassed": all(check["status"] == "pass" for check in checks),
        "traceCompletenessProven": False,
        "limitations": ["unknown_is_not_pass", "missing_trace_is_not_proof_of_deadlock",
                        "same_clock_ticks_have_no_declared_frequency_here"],
    }

    groups, omitted = [], 0
    for i, ((emitter, stage), values) in enumerate(sorted(durations.items())):
        if i >= MAX_PERFORMANCE_GROUPS:
            omitted += 1
            continue
        samples = [duration for duration, _ in values]
        groups.append({
            "emitterHash": sha256(emitter.encode("utf-8")).hexdigest()[:12],
            "stage": stage, "sampleCount": len(samples),
            "minUs": min(samples), "maxUs": max(samples),
            "p50Us": _percentile(samples, 0.5),
            "p95Us": _percentile(samples, 0.95),
            "p99Us": _percentile(samples, 0.99),
            "sampleEvidence": values[0][1],
            "measurement": "emitted_elapsedUs_only",
        })
    performance = {
        "schemaVersion": 1, "durationPercentiles": "nearest_rank",
        "groups": groups, "omittedGroups": omitted,
        "criticalPath": "unknown", "cpuAttribution": "unknown",
        "limitations": ["utc_time_never_subtracted_for_durations",
                        "unobserved_or_parallel_intervals_not_inferred",
                        "groups_are_per_emitter_and_stage"],
    }
    return timeline, assertions, performance
