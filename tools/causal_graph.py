"""Evidence-only request/operation graph for the offline Cvolo analyzer.

Edges require explicit recorded identities from one emitter. Shared snapshots
and proximity in time are not evidence of one request causing another.
"""
from collections import defaultdict
import hashlib

FAILURE_OUTCOMES = frozenset({
    "not_found", "faulted", "rejected", "stale_discarded",
    "unavailable", "cancelled", "unsupported", "discarded", "invalid", "timeout"
})


def emitter_hash(value):
    return hashlib.sha256(value.encode()).hexdigest()[:12]


def observed_failure(event):
    if not event:
        return None
    return {
        "eventRef": event["eventRef"], "event": event["event"],
        "stage": event.get("stage"), "outcome": event["outcome"],
        "reason": event["reason"], "evidence": "observed_machine_outcome_not_root_cause"
    }


def is_failure(event):
    return (event["outcome"] in FAILURE_OUTCOMES
            or event["event"].endswith(".faulted"))


def safe_event(event):
    return {key: event.get(key) for key in (
        "eventRef", "event", "stage", "outcome", "reason", "requestUid",
        "operationId", "parentOperationId", "analysisId", "snapshotId",
        "documentVersion")}


def make_graph(records):
    """Build request/operation graph only when real identity fields link events.

    A snapshot shared between two requests is not a request dependency. Events
    without an observed terminal must remain open/unknown, never 'success'.
    """
    scopes = {}
    requests = defaultdict(list)
    owners = defaultdict(set)
    parents = defaultdict(set)
    analysis_snapshots = defaultdict(set)
    ambiguities = []
    for event in records:
        emitter = event["_emitter"]
        op, uid, parent = (event.get(name) for name in
                           ("operationId", "requestUid", "parentOperationId"))
        if op:
            key = emitter, op
            scopes.setdefault(key, []).append(event)
            if uid:
                owners[key].add(uid)
            if parent:
                parents[key].add(parent)
        if uid:
            requests[emitter, uid].append(event)
        if event.get("analysisId") and event.get("snapshotId"):
            analysis_snapshots[emitter, event["analysisId"]].add(event["snapshotId"])

    # Resolve nested child operation ownership to a fixed point. Parent
    # ambiguity is not broken by looking at timestamps or first-in-file order.
    for _ in range(min(len(scopes), 128)):
        changed = False
        for key, parent_ids in parents.items():
            if len(parent_ids) != 1:
                continue
            parent_key = key[0], next(iter(parent_ids))
            inherited = owners.get(parent_key, set())
            if len(inherited) == 1 and not owners[key]:
                owners[key] = set(inherited)
                changed = True
        if not changed:
            break

    for key, events in scopes.items():
        candidates = owners.get(key, set())
        if len(candidates) == 1:
            request_id = next(iter(candidates))
            already = {e["eventRef"] for e in requests[key[0], request_id]}
            for event in events:
                if event["eventRef"] not in already and not event.get("requestUid"):
                    requests[key[0], request_id].append(event)
        elif len(candidates) > 1:
            ambiguities.append({"reason": "ambiguous_operation_request",
                                "eventRef": events[0]["eventRef"]})
        if len(parents.get(key, set())) > 1:
            ambiguities.append({"reason": "ambiguous_parent_operation",
                                "eventRef": events[0]["eventRef"]})

    request_nodes = []
    internal_requests = {}
    for (emitter, uid), events in sorted(requests.items()):
        ordered = sorted({e["eventRef"]: e for e in events}.values(),
                         key=lambda e: e["_seq"])
        first_failure = next((e for e in ordered if is_failure(e)), None)
        terminal = next((e for e in reversed(ordered) if e["event"] in (
            "rpc.response.sent", "request.completed", "request.faulted",
            "request.cancelled", "feature.request.completed",
            "feature.request.faulted", "feature.request.cancelled")), None)
        request_nodes.append({
            "requestUid": uid, "emitterHash": emitter_hash(emitter),
            "eventRefs": [e["eventRef"] for e in ordered],
            "firstObservedFailure": observed_failure(first_failure),
            "terminal": {"eventRef": terminal["eventRef"],
                         "event": terminal["event"], "outcome": terminal["outcome"]}
                        if terminal else None,
            "terminalObserved": terminal is not None,
            "stageSequence": [safe_event(e) for e in ordered if e.get("stage")],
        })
        internal_requests[emitter, uid] = ordered

    operation_nodes = []
    for (emitter, op), events in sorted(scopes.items()):
        ordered = sorted(events, key=lambda e: e["_seq"])
        parent_ids = parents.get((emitter, op), set())
        candidates = owners.get((emitter, op), set())
        operation_nodes.append({
            "operationId": op, "emitterHash": emitter_hash(emitter),
            "parentOperationId": next(iter(parent_ids)) if len(parent_ids) == 1 else None,
            "requestUid": next(iter(candidates)) if len(candidates) == 1 else None,
            "eventRefs": [e["eventRef"] for e in ordered],
            "firstObservedFailure": observed_failure(
                next((e for e in ordered if is_failure(e)), None)),
            "startedObserved": any(e["event"].endswith(".started") for e in ordered),
            "terminalObserved": any(e["event"].endswith(
                (".completed", ".cancelled", ".faulted")) for e in ordered)
        })

    analysis_nodes = [
        {"analysisId": analysis, "emitterHash": emitter_hash(emitter),
         "snapshotIds": sorted(snapshots), "relation": "explicit_same_event"}
        for (emitter, analysis), snapshots in sorted(analysis_snapshots.items())
    ]
    report = {
        "graphVersion": 1, "relationshipPolicy": "explicit_ids_within_emitter_only",
        "requestCount": len(request_nodes), "operationCount": len(operation_nodes),
        "requests": request_nodes, "operations": operation_nodes,
        "analysisSnapshots": analysis_nodes, "ambiguities": ambiguities,
        "limitations": ["shared_snapshot_is_not_request_causation",
                        "missing_start_or_terminal_is_unknown_not_implied_success",
                        "cross_emitter_causality_not_inferred"]
    }
    return report, internal_requests


def marker_request_candidates(marker, records, requests, window_ms=10000):
    """Bounded temporal lookup plus explicit request chains, without causal claims."""
    nearby = []
    for e in records:
        if e is marker or e["_emitter"] != marker["_emitter"]:
            continue
        if e["_time"] is None or marker["_time"] is None:
            continue
        if abs(e["_time"]-marker["_time"]) <= window_ms:
            nearby.append(e)
            if len(nearby) == 500:
                break
    uids = sorted({e["requestUid"] for e in nearby if e.get("requestUid")})
    candidates = []
    for uid in uids[:50]:
        events = requests.get((marker["_emitter"], uid), [])
        failure = next((e for e in events if is_failure(e)), None)
        candidates.append({
            "requestUid": uid, "relation": "temporal_only",
            "eventRefs": [e["eventRef"] for e in events[:200]],
            "firstObservedFailure": observed_failure(failure),
            "rootCause": "insufficient_evidence"
        })
    return nearby, candidates
