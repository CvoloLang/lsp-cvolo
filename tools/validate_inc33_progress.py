#!/usr/bin/env python3
"""Fixed Inc 3.3 acceptance progress. No implementation subtasks as stages.

CI checks this generated Markdown against the immutable 7-family, 87-case
normative v4.1 RTM. A PASS is only the normative status, never a green build.
"""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "tools" / "acceptance" / "inc33-normative-rtm.json"
TARGET = ROOT / "docs" / "diagnostics" / "inc33-progress.md"
FAMILIES = (
    ("CORE", 10, "Telemetry kernel"),
    ("CON", 12, "Concurrency and integration"),
    ("COMP", 14, "Compiler / Tooling / CLI"),
    ("LSP", 11, "Language Server"),
    ("VSC", 6, "Installed VS Code extension"),
    ("IO", 15, "Recording, replay and analysis"),
    ("AUD", 19, "Release audit"),
)
STATES = ("PASS", "PARTIAL", "BLOCKED", "FAIL")


def render_progress(matrix):
    rows = matrix["requirements"]
    actual_ids = [r["requirementId"] for r in rows]
    expected_ids = [
        "OBS-{}-{:03d}".format(family, i)
        for family, size, _ in FAMILIES for i in range(1, size + 1)
    ]
    if len(actual_ids) != 87 or set(actual_ids) != set(expected_ids):
        raise ValueError("normative 87-case inventory drift")
    if len(actual_ids) != len(set(actual_ids)):
        raise ValueError("duplicate normative scenario")
    if any(r["status"] not in STATES for r in rows):
        raise ValueError("invalid normative status")
    counts = {state: sum(r["status"] == state for r in rows) for state in STATES}
    tick = chr(96)
    lines = [
        "# Inc 3.3 v4.1 — fixed acceptance plan",
        "",
        "Generated from " + tick + "tools/acceptance/inc33-normative-rtm.json" + tick + ".",
        "**Never edit progress totals or acceptance checkboxes by hand.**",
        "There are exactly **7 fixed families and 87 normative scenarios**.",
        "A technical fix, implemented test, green CI or exploratory benchmark",
        "does not count as normative PASS without a complete scenario oracle,",
        "platform evidence, a verified build SHA and an auditable evidence artifact.",
        "",
        ("**Current acceptance:** {}/87 PASS; {} PARTIAL; {} BLOCKED; {} FAIL."
         .format(counts["PASS"], counts["PARTIAL"],
                 counts["BLOCKED"], counts["FAIL"])),
        "",
        "| Fixed family | Normative cases | PASS | PARTIAL | BLOCKED | FAIL |",
        "|---|---:|---:|---:|---:|---:|",
    ]
    for family, expected, label in FAMILIES:
        subset = [r for r in rows if r["requirementId"].split("-")[1] == family]
        c = {state: sum(r["status"] == state for r in subset) for state in STATES}
        lines.append("| {} — {} | {} | {} | {} | {} | {} |".format(
            family, label, expected, c["PASS"], c["PARTIAL"],
            c["BLOCKED"], c["FAIL"]))
    lines.extend([
        "| **TOTAL** | **87** | **{}** | **{}** | **{}** | **{}** |".format(
            counts["PASS"], counts["PARTIAL"], counts["BLOCKED"], counts["FAIL"]),
        "",
        "## Permanent acceptance checklist",
        "",
        "A checked item means normative PASS only; PARTIAL remains unchecked.",
        "The ID and its fixed family never change. New engineering subtasks",
        "belong under these IDs rather than increasing the total number of stages.",
    ])
    for family, _, label in FAMILIES:
        lines.extend(["", "### {} — {}".format(family, label), ""])
        for r in rows:
            if r["requirementId"].split("-")[1] != family:
                continue
            description = r["description"].replace("\n", " ").strip()
            lines.append("- [{}] {}{}{} **{}** — {}".format(
                "x" if r["status"] == "PASS" else " ",
                tick, r["requirementId"], tick, r["status"], description))
    lines.extend([
        "",
        "## Release versus development",
        "",
        "- Development CI checks the integrity of the matrix, test references,",
        "  and this generated document; incomplete requirements are allowed.",
        "- Release-tag CI invokes --require-complete and rejects releases",
        "  unless all 87 normative scenarios have verified PASS evidence.",
        "- Local implementation tests and CI success are recorded separately",
        "  from normative acceptance. See the requirements-test-matrix.md",
        "  sibling document for test references and oracle details.",
        "",
    ])
    return "\n".join(lines)


def main():
    matrix = json.loads(SOURCE.read_text(encoding="utf-8"))
    expected = render_progress(matrix)
    if not TARGET.is_file() or TARGET.read_text(encoding="utf-8") != expected:
        print("INC33 PROGRESS ERROR: generated seven-family plan differs from normative RTM")
        return 1
    print("Inc 3.3 fixed acceptance progress verified: 7 families / 87 cases")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
