"""CI regression guards for the permanent seven-family Inc 3.3 progress."""
import copy
import json
import sys
import unittest
from pathlib import Path

TOOLS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(TOOLS))
from validate_inc33_progress import FAMILIES, SOURCE, TARGET, main, render_progress


class StableProgressTests(unittest.TestCase):
    def setUp(self):
        self.matrix = json.loads(SOURCE.read_text(encoding="utf-8"))

    def test_fixed_seven_families_and_eighty_seven_scenarios(self):
        self.assertEqual(7, len(FAMILIES))
        self.assertEqual(87, sum(size for _, size, _ in FAMILIES))
        output = render_progress(self.matrix)
        self.assertEqual(87, output.count("- [ ] " + chr(96) + "OBS-"))
        for family, _, _ in FAMILIES:
            self.assertEqual(1, output.count("### " + family + " — "))
        counts = {status: sum(r["status"] == status for r in self.matrix["requirements"])
                  for status in ("PASS", "PARTIAL", "BLOCKED", "FAIL")}
        self.assertIn(
            f'{counts["PASS"]}/87 PASS; {counts["PARTIAL"]} PARTIAL; '
            f'{counts["BLOCKED"]} BLOCKED; {counts["FAIL"]} FAIL',
            output)

    def test_progress_matches_normative_source_of_truth(self):
        self.assertEqual(0, main())
        self.assertEqual(render_progress(self.matrix),
                         TARGET.read_text(encoding="utf-8"))

    def test_partial_is_never_treated_as_completed(self):
        changed = copy.deepcopy(self.matrix)
        changed["requirements"][0]["status"] = "PARTIAL"
        revised = render_progress(changed)
        self.assertNotEqual(revised, TARGET.read_text(encoding="utf-8"))
        baseline_partial = sum(r["status"] == "PARTIAL" for r in self.matrix["requirements"])
        baseline_blocked = sum(r["status"] == "BLOCKED" for r in self.matrix["requirements"])
        self.assertIn(f"{baseline_partial + 1} PARTIAL; {baseline_blocked - 1} BLOCKED",
                      revised)
        self.assertNotIn("- [x] ", revised)

    def test_missing_id_or_extra_stage_is_rejected(self):
        changed = copy.deepcopy(self.matrix)
        changed["requirements"].append(copy.deepcopy(changed["requirements"][0]))
        with self.assertRaises(ValueError):
            render_progress(changed)

    def test_release_gate_is_enforced_only_for_tags(self):
        workflow = (TOOLS.parent / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8")
        self.assertIn("python tools/validate_inc33_normative_rtm.py --require-complete", workflow)
        self.assertIn("if: startsWith(github.ref, 'refs/tags/')", workflow)
        self.assertIn("python tools/validate_inc33_normative_rtm.py\n", workflow)


if __name__ == "__main__":
    unittest.main()
