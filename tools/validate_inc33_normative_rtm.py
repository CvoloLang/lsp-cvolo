#!/usr/bin/env python3
"""CI inventory and fail-closed acceptance validator for normative Inc 3.3.

Checks the actual 68+19 IDs from §§45/65, references to existing local xUnit
tests or real Python unittest methods, provenance requirements for PASS, and
complete release gating. A valid INCOMPLETE inventory is not acceptance.
"""
import argparse
import ast
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MATRIX = ROOT / "tools" / "acceptance" / "inc33-normative-rtm.json"
INTERIM = ROOT / "tools" / "acceptance" / "inc33-rtm.json"
FAMILIES = {"CORE": (10, "§45.1"), "CON": (12, "§45.2"),
            "COMP": (14, "§45.3"), "LSP": (11, "§45.4"),
            "VSC": (6, "§45.4"), "IO": (15, "§45.5"),
            "AUD": (19, "§65")}
FIELDS = {"requirementId", "sourceSection", "priority", "component",
          "description", "coveredMethods", "eventIds", "testIds",
          "testType", "oracle", "fixture", "platform", "status",
          "evidencePath", "buildSha", "knownLimitations"}
STATUSES = {"BLOCKED", "PARTIAL", "PASS", "FAIL"}


def _python_test_exists(root, reference):
    match = re.fullmatch(
        r"python:(tools/tests/test_[a-z0-9_]+\.py)::"
        r"([A-Za-z_][A-Za-z_0-9]*)\.([A-Za-z_][A-Za-z_0-9]*)", reference)
    if not match:
        return False
    file, cls, method = match.groups()
    path = root / file
    if not path.is_file():
        return False
    try:
        tree = ast.parse(path.read_text(encoding="utf-8-sig"))
    except (SyntaxError, UnicodeError, OSError):
        return False
    return any(isinstance(c, ast.ClassDef) and c.name == cls
        and any(isinstance(f, (ast.FunctionDef, ast.AsyncFunctionDef))
                and f.name == method for f in c.body)
        for c in tree.body)


def validate(matrix, interim, root=ROOT, require_complete=False):
    problems = []
    if matrix.get("schemaVersion") != 1:
        problems.append("invalid schema version")
    expected = {
        f"OBS-{family}-{i:03d}": source
        for family, (count, source) in FAMILIES.items()
        for i in range(1, count + 1)
    }
    records = matrix.get("requirements")
    if not isinstance(records, list):
        return ["missing normative case records"]
    if len(records) != 87:
        problems.append("expected exactly 87 normative records")
    if matrix.get("caseCounts") != {"base": 68, "additional": 19, "total": 87}:
        problems.append("incorrect normative counts")
    if matrix.get("sourceVersion") != "4.1":
        problems.append("unexpected specification version")
    local = {
        item.get("id") for item in interim.get("requirements", [])
        if isinstance(item, dict) and item.get("state") == "implemented_test"
    }
    seen = set()
    all_complete = True
    for item in records:
        if not isinstance(item, dict):
            problems.append("invalid requirement object")
            continue
        rid = item.get("requirementId")
        if rid not in expected:
            problems.append(f"unknown normative ID: {rid}")
            continue
        if rid in seen:
            problems.append(f"duplicate normative ID: {rid}")
        seen.add(rid)
        if set(item) != FIELDS:
            problems.append(f"{rid}: missing/unknown required field")
        if item.get("sourceSection") != expected[rid] or item.get("priority") != "MUST":
            problems.append(f"{rid}: incorrect source or priority")
        if not all(isinstance(item.get(x), str) and item[x].strip()
                   for x in ("description", "component", "oracle", "knownLimitations")):
            problems.append(f"{rid}: absent source text or limitations")
        if not all(isinstance(item.get(x), list)
                   for x in ("coveredMethods", "eventIds", "testIds", "platform")):
            problems.append(f"{rid}: malformed array")
            continue
        tests = item["testIds"]
        if len(tests) != len(set(tests)):
            problems.append(f"{rid}: duplicate test ID")
        for test in tests:
            if not isinstance(test, str) or not (
                test in local or _python_test_exists(root, test)
            ):
                problems.append(f"{rid}: unverified test link: {test}")
        status = item.get("status")
        if status not in STATUSES:
            problems.append(f"{rid}: invalid status")
        if status == "PARTIAL" and not tests:
            problems.append(f"{rid}: PARTIAL without actual test")
        if status == "PASS":
            sha = item.get("buildSha")
            evidence = item.get("evidencePath")
            if (not tests or not isinstance(sha, str)
                or not re.fullmatch(r"[0-9a-f]{40}", sha)
                or not isinstance(evidence, str)
                or not re.fullmatch(r"docs/diagnostics/test-evidence/[A-Za-z0-9._/-]+\.json", evidence)
                or not (root / evidence).is_file()
                or not item["platform"]):
                problems.append(f"{rid}: PASS lacks verified tests/evidence/build/platform")
        else:
            all_complete = False
            if item.get("evidencePath") is not None or item.get("buildSha") is not None:
                problems.append(f"{rid}: non-PASS must not claim completed evidence")
    missing = set(expected) - seen
    if missing:
        problems.append("missing normative IDs: " + ",".join(sorted(missing)))
    if matrix.get("normativeCoverage") != ("complete" if all_complete else "incomplete"):
        problems.append("incorrect coverage conclusion")
    if matrix.get("overallStatus") != ("PASS" if all_complete else "BLOCKED"):
        problems.append("incorrect overall status")
    if require_complete and not all_complete:
        problems.append("release acceptance blocked: normative RTM incomplete")
    return problems


def render_markdown(matrix):
    rows = matrix["requirements"]

    def escape(value):
        return str(value).replace("|", r"\|").replace("\n", " ").strip()

    def count(status):
        return sum(x.get("status") == status for x in rows)

    lines = [
        "# Inc 3.3 v4.1 — normative requirements/test matrix",
        "",
        "Generated from `tools/acceptance/inc33-normative-rtm.json` (the source of truth).",
        "The 68 §45 scenarios plus 19 §65 scenarios are a **complete ID inventory**, NOT completed acceptance.",
        "A linked test alone establishes only partial coverage. `PASS` requires test/evidence/build/platform provenance and the release gate.",
        "",
        (f"**Inventory:** {len(rows)} scenarios · {count('PASS')} PASS · "
         f"{count('PARTIAL')} PARTIAL · {count('BLOCKED')} BLOCKED · "
         f"{count('FAIL')} FAIL."),
        "",
        "| Requirement | Section | Component | Status | Existing test links | Scenario | Normative oracle |",
        "|---|---|---|---|---|---|---|",
    ]
    for x in rows:
        linked = ", ".join("`" + t + "`" for t in x["testIds"]) if x["testIds"] else "—"
        lines.append(
            f"| {escape(x['requirementId'])} | {escape(x['sourceSection'])} "
            f"| {escape(x['component'])} | {x['status']} | {linked} "
            f"| {escape(x['description'])} | {escape(x['oracle'])} |"
        )
    lines.extend([
        "",
        "**Release acceptance remains BLOCKED** until every normative scenario has complete evidence, the independent functional oracle, platform verification, and a verified build SHA.",
        "",
    ])
    return "\n".join(lines)


def run(require_complete=False):
    matrix = json.loads(MATRIX.read_text(encoding="utf-8"))
    interim = json.loads(INTERIM.read_text(encoding="utf-8"))
    problems = validate(matrix, interim, require_complete=require_complete)
    generated_path = ROOT / "docs" / "diagnostics" / "requirements-test-matrix.md"
    if not generated_path.is_file() or generated_path.read_text(encoding="utf-8") != render_markdown(matrix):
        problems.append("human-readable RTM is missing or differs from normative JSON")
    return problems


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--require-complete", action="store_true")
    options = parser.parse_args()
    errors = run(require_complete=options.require_complete)
    for err in errors:
        print("NORMATIVE RTM ERROR:", err)
    if not errors:
        print("87 normative scenario IDs and honest evidence states verified; "
              "release remains BLOCKED unless every case is PASS.")
    raise SystemExit(1 if errors else 0)
