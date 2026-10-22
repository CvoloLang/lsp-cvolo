"""Conservative privacy risk scan and observed-event coverage for bundle v1.

Inputs are untrusted JSON, including attributes that the main analyzer omits.
Reports contain only bounded machine-coded findings and evidence references:
never copy suspicious keys, strings, source, absolute paths, or secret values.
This is a risk detector, NOT a privacy certification.
"""
from collections import Counter
import re

MAX_FINDINGS = 96
MAX_VALUE_SCAN = 8192
MAX_SCAN_DEPTH = 8

# Exact normalized key names, not substring matches: a documentVersion field
# must never be mistaken for a document payload.
SENSITIVE_KEYS = frozenset({
    "source", "sourcetext", "sourcecode", "rawsource", "text", "content",
    "filecontent", "documenttext", "documentcontent", "filepath", "fileuri",
    "filename", "documenturi", "absolutepath", "workspacepath",
    "projectpath", "homepath", "commandline", "environmentvariables",
    "password", "passwd", "secret", "apikey", "accesstoken",
    "refreshtoken", "authorization", "cookie", "privatekey",
})
PATH_WINDOWS = re.compile(r"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\[A-Za-z0-9_.-]+\\[A-Za-z0-9_.-]+\\)")
PATH_UNIX = re.compile(r"(?<![A-Za-z0-9])/(?:home|Users|mnt|private|tmp|var|etc|workspace|root)(?:/|\\b)")
FILE_URI = re.compile(r"file://", re.IGNORECASE)
EMAIL = re.compile(r"\b[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,128}\.[A-Za-z]{2,24}\b")
BEARER = re.compile(r"\bBearer\s+[A-Za-z0-9._~+/-]{8,}", re.IGNORECASE)
CREDENTIAL = re.compile(r"(?:sk-[A-Za-z0-9_-]{12,}|ghp_[A-Za-z0-9]{12,}|github_pat_[A-Za-z0-9_]{12,}|AKIA[A-Z0-9]{16})")
KEY_BLOCK = re.compile(r"-----BEGIN\s+(?:[A-Z ]+\s+)?PRIVATE KEY-----")


def _normalized_key(key):
    return "".join(ch.lower() for ch in key if ch.isascii() and ch.isalnum())


class PrivacyRiskScan:
    def __init__(self):
        self._counts = Counter()
        self._findings = []
        self.scanned_records = 0
        self.truncated_values = 0

    def _mark(self, ref, kind, rule):
        self._counts[rule] += 1
        if len(self._findings) < MAX_FINDINGS:
            self._findings.append({"eventRef": ref, "fieldKind": kind, "rule": rule,
                                   "severity": "high" if rule in (
                                       "secret_like_value", "sensitive_metadata_key",
                                       "private_key_material") else "review"})

    def inspect(self, raw, event_ref, is_manifest=False):
        self.scanned_records += 1
        base_kind = "manifest" if is_manifest else "event"
        # Iterative traversal avoids recursion overflow for hostile JSON input.
        pending = [(raw, base_kind, 0)]
        while pending:
            value, kind, depth = pending.pop()
            if depth > MAX_SCAN_DEPTH:
                self._mark(event_ref, kind, "uninspectable_nested_payload")
                continue
            if isinstance(value, dict):
                for key, item in value.items():
                    next_kind = ("attributes" if key == "attributes" else kind)
                    if isinstance(key, str) and _normalized_key(key) in SENSITIVE_KEYS:
                        self._mark(event_ref, next_kind, "sensitive_metadata_key")
                    pending.append((item, next_kind, depth + 1))
            elif isinstance(value, list):
                # No large unbounded list retained in the output.
                for item in value:
                    pending.append((item, kind, depth + 1))
            elif isinstance(value, str):
                if len(value) > MAX_VALUE_SCAN:
                    self.truncated_values += 1
                    self._mark(event_ref, kind, "value_too_large_to_inspect")
                    continue
                if KEY_BLOCK.search(value):
                    self._mark(event_ref, kind, "private_key_material")
                elif BEARER.search(value) or CREDENTIAL.search(value):
                    self._mark(event_ref, kind, "secret_like_value")
                elif PATH_WINDOWS.search(value) or PATH_UNIX.search(value) or FILE_URI.search(value):
                    self._mark(event_ref, kind, "absolute_path_or_file_uri")
                elif EMAIL.search(value):
                    self._mark(event_ref, kind, "email_like_value")

    def report(self, invalid_records=0):
        risks = sum(self._counts.values())
        return {
            "schemaVersion": 1,
            "auditVerified": False,
            "assessment": "risk_detected" if risks else "inconclusive",
            "scannedObjects": self.scanned_records,
            "invalidOrUnparsedEvents": invalid_records,
            "riskCount": risks,
            "findingCount": len(self._findings),
            "findingsTruncated": risks > len(self._findings),
            "valueScanLimitCharacters": MAX_VALUE_SCAN,
            "findings": self._findings,
            "ruleCounts": dict(sorted(self._counts.items())),
            "limitations": [
                "no_finding_does_not_certify_privacy",
                "encrypted_encoded_and_semantic_secrets_may_be_missed",
                "no_raw_suspicious_values_or_keys_exported",
                "manifest_not_independently_authenticated",
                "invalid_events_may_contain_uninspectable_content",
            ],
        }


# A presence matrix, not a percentage of runtime code coverage. The trace may
# start in the middle of a session, be sampled, or lose entire spans to rotation.
EVENT_GROUPS = {
    "recording_control": (
        "recording.mode_changed", "recording.triggered", "recording.cutoff"),
    "rpc_boundary": (
        "rpc.request.received", "rpc.response.sent", "rpc.notification.received"),
    "document_updates": (
        "document.did_open", "document.did_change.applied",
        "document.did_change.rejected", "document.update.applied"),
    "snapshot_lifecycle": (
        "snapshot.created", "snapshot.capture.completed",
        "snapshot.advance.completed", "snapshot.fallback"),
    "feature_requests": (
        "feature.request.started", "feature.request.completed",
        "feature.request.faulted", "feature.request.cancelled"),
    "diagnostic_publication": (
        "diagnostics.scheduled", "diagnostics.publish_sent",
        "diagnostics.publish_faulted", "diagnostics.discarded"),
    "user_incident_markers": ("diagnostic.marker",),
    "analysis_lifecycle": ("analysis.started", "analysis.completed", "analysis.faulted"),
}


def observed_coverage(records, checkpoint):
    counts = Counter(e["event"] for e in records)
    groups = []
    for name, codes in sorted(EVENT_GROUPS.items()):
        events = [{"event": code, "observedCount": counts.get(code, 0)}
                  for code in codes]
        count = sum(v["observedCount"] for v in events)
        groups.append({
            "group": name, "status": "observed" if count else "not_observed",
            "observedEventCount": count, "events": events,
        })
    dropped = {key: checkpoint.get(key) for key in (
        "droppedP0", "droppedP1", "droppedP2", "writerLost")}
    return {
        "schemaVersion": 1,
        "interpretation": "observed_trace_event_presence_only",
        "notObservedDoesNotMeanUninstrumented": True,
        "completenessProven": False,
        "replayIncompleteReported": bool(checkpoint.get("replayIncomplete")),
        "reportedLossCounters": dropped, "groups": groups,
        "limitations": ["rotated_or_dropped_events_can_hide_coverage",
                        "disabled_event_detail_levels_can_hide_groups",
                        "not_a_percentage_of_source_code_coverage",
                        "unknown_areas_are_not_automatically_pass"],
    }
