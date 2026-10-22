"""Conservative diagnostics for explicit operation dependencies and lifecycle gaps.

Only same-emitter identities are joined. A missing target can be a rotated-away
record; an unobserved terminal is not proof that work is still running.
"""
from collections import defaultdict


TERMINALS = (".completed", ".cancelled", ".faulted", ".aborted")


def inspect_dependencies(records):
    operations = defaultdict(list)
    for event in records:
        if event.get("operationId"):
            operations[event["_emitter"], event["operationId"]].append(event)

    edges, unresolved, lifecycle = [], [], []
    unique_edges = set()
    for (emitter, operation), events in sorted(operations.items()):
        ordered = sorted(events, key=lambda event: event["_seq"])
        starts = [item for item in ordered if item["event"].endswith(".started")]
        terminals = [item for item in ordered if item["event"].endswith(TERMINALS)]
        if starts and not terminals:
            lifecycle.append({"operationId": operation, "eventRef": starts[0]["eventRef"],
                              "finding": "terminal_not_observed", "certainty": "unknown"})
        if len(terminals) > 1:
            lifecycle.append({"operationId": operation, "eventRef": terminals[1]["eventRef"],
                              "finding": "multiple_terminal_events_observed", "certainty": "observed"})

    for event in records:
        source = event.get("operationId")
        parent = event.get("parentOperationId")
        dependencies = []
        if parent and source:
            dependencies.append(("parent_operation", parent))
        if event["event"] == "operation.depends_on" and source:
            dependency = event.get("dependencyId")
            if dependency:
                dependencies.append(("explicit_dependency", dependency))
            else:
                unresolved.append({"eventRef": event["eventRef"], "reason": "dependency_target_unavailable",
                                   "certainty": "unknown"})
        for kind, target in dependencies:
            key = (event["_emitter"], source, kind, target)
            if key in unique_edges:
                continue
            unique_edges.add(key)
            if (event["_emitter"], target) not in operations:
                unresolved.append({"eventRef": event["eventRef"], "reason": "target_not_in_retained_trace",
                                   "relation": kind, "certainty": "unknown"})
                continue
            edges.append({"sourceOperationId": source, "targetOperationId": target,
                          "relation": kind, "eventRef": event["eventRef"],
                          "evidence": "explicit_same_emitter_identity"})
    return {
        "edgeCount": len(edges), "edges": edges, "unresolvedLinks": unresolved,
        "lifecycleFindings": lifecycle,
        "limitations": ["missing_target_may_be_rotated_or_dropped",
                        "terminal_not_observed_does_not_prove_hang",
                        "cross_emitter_links_not_inferred"]
    }
