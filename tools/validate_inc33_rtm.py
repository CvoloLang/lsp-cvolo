#!/usr/bin/env python3
"""Static RTM gate: exact xUnit test references, no invented completeness.

Executed under tools/tests via Python unittest. Every implemented_test must
point to an existing [Fact]/[Theory] method. This gate does not claim tests
have passed without the actual .NET CI job and cannot certify normative Inc IDs.
"""
import json
import re
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parent
MATRIX = PROJECT_ROOT / "acceptance" / "inc33-rtm.json"
REPO_ROOT = PROJECT_ROOT.parent
VALID_STATES = {"implemented_test", "open"}
TEST_ATTRIBUTE = re.compile(r"\[(?:Fact|Theory)(?:\([^\]]*\))?\]")
METHOD_DECL = re.compile(
    r"\bpublic\s+(?:async\s+)?(?:void|Task(?:<[^>\n]+>)?|ValueTask(?:<[^>\n]+>)?)\s+"
    r"(?P<method>[A-Za-z_][A-Za-z_0-9]*)\s*\("
)


def _methods_with_xunit_attributes(text):
    """Bind the nearest prior xUnit attribute to the next method only."""
    methods = set()
    last_attribute = None
    for match in re.finditer(
        r"\[(?:Fact|Theory)(?:\([^\]]*\))?\]|\bpublic\s+(?:async\s+)?"
        r"(?:void|Task(?:<[^>\n]+>)?|ValueTask(?:<[^>\n]+>)?)\s+"
        r"[A-Za-z_][A-Za-z_0-9]*\s*\(",
        text,
    ):
        token = match.group()
        if TEST_ATTRIBUTE.fullmatch(token):
            last_attribute = match.end()
            continue
        method = METHOD_DECL.fullmatch(token)
        if method and last_attribute is not None:
            # A long unrelated region is not an xUnit method annotation.
            between = text[last_attribute:match.start()]
            if len(between) < 700 and "[Fact" not in between and "[Theory" not in between:
                methods.add(method.group("method"))
        last_attribute = None
    return methods


def validate_matrix(raw, root=REPO_ROOT):
    problems = []
    if raw.get("schemaVersion") != 1:
        problems.append("unsupported RTM schema")
    if raw.get("normativeCoverage") != "unverified":
        problems.append("normative 87-case coverage must not be claimed")
    records = raw.get("requirements")
    if not isinstance(records, list) or not records:
        return problems + ["RTM is empty"]
    ids, references = set(), set()
    cache = {}
    for index, record in enumerate(records):
        if not isinstance(record, dict):
            problems.append(f"{index}: invalid record")
            continue
        rid = record.get("id")
        state = record.get("state")
        if not isinstance(rid, str) or not re.fullmatch(r"LSP33-(?:[A-Z]{3}|GAP)-\d{2}", rid):
            problems.append(f"{index}: invalid local requirement ID")
            continue
        if rid in ids:
            problems.append(f"{rid}: duplicate requirement ID")
        ids.add(rid)
        if state not in VALID_STATES:
            problems.append(f"{rid}: unknown state")
            continue
        if state == "open":
            if record.get("test") is not None:
                problems.append(f"{rid}: open requirement must not claim implemented test")
            if not isinstance(record.get("blocking_reason"), str) or not record["blocking_reason"].strip():
                problems.append(f"{rid}: missing blocker")
            continue
        test = record.get("test")
        if not isinstance(test, dict) or set(test) != {"path", "method"}:
            problems.append(f"{rid}: missing explicit test reference")
            continue
        path_text, method = test["path"], test["method"]
        if not isinstance(path_text, str) or not re.fullmatch(
            r"tests/Cvolo\.LanguageServer\.Tests/(?:[A-Za-z0-9_-]+/)+[A-Za-z0-9_-]+\.cs", path_text
        ):
            problems.append(f"{rid}: unsafe/invalid test path")
            continue
        if not isinstance(method, str) or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", method):
            problems.append(f"{rid}: invalid method name")
            continue
        pair = (path_text, method)
        if pair in references:
            problems.append(f"{rid}: duplicate test reference")
        references.add(pair)
        file_path = root / path_text
        if path_text not in cache:
            cache[path_text] = (
                _methods_with_xunit_attributes(file_path.read_text(encoding="utf-8-sig"))
                if file_path.is_file() else set()
            )
        if method not in cache[path_text]:
            problems.append(f"{rid}: missing enabled xUnit [Fact]/[Theory]: {path_text}::{method}")
    return problems


def load_and_validate():
    return validate_matrix(json.loads(MATRIX.read_text(encoding="utf-8")))


if __name__ == "__main__":
    issues = load_and_validate()
    for issue in issues:
        print("RTM ERROR:", issue)
    if not issues:
        print("RTM references valid; normative 87-case acceptance remains unverified.")
    raise SystemExit(1 if issues else 0)
