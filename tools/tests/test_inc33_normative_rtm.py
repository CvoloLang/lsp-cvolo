"""Static normative 87-case inventory gate; no fabricated coverage PASS."""
import copy
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from validate_inc33_normative_rtm import MATRIX, INTERIM, run, validate


class NormativeRtmTests(unittest.TestCase):
    def setUp(self):
        self.matrix = json.loads(MATRIX.read_text(encoding="utf-8"))
        self.interim = json.loads(INTERIM.read_text(encoding="utf-8"))

    def test_exact_68_plus_19_normative_scenarios_exist(self):
        self.assertEqual([], run())
        self.assertEqual(87, len(self.matrix["requirements"]))
        self.assertEqual("BLOCKED", self.matrix["overallStatus"])

    def test_release_gate_fail_closed_until_every_case_has_evidence(self):
        errors = run(require_complete=True)
        self.assertTrue(any("release acceptance blocked" in x for x in errors))

    def test_missing_and_duplicate_case_break_inventory(self):
        changed = copy.deepcopy(self.matrix)
        changed["requirements"][0]["requirementId"] = "OBS-AUD-001"
        errors = validate(changed, self.interim)
        self.assertTrue(any("duplicate normative ID" in x for x in errors))
        self.assertTrue(any("missing normative IDs" in x for x in errors))

    def test_fake_PASS_without_real_evidence_is_rejected(self):
        changed = copy.deepcopy(self.matrix)
        item = changed["requirements"][0]
        item["status"] = "PASS"
        changed["normativeCoverage"] = "complete"
        changed["overallStatus"] = "PASS"
        errors = validate(changed, self.interim)
        self.assertTrue(any("PASS lacks verified" in x for x in errors))

    def test_unknown_local_test_reference_is_rejected(self):
        changed = copy.deepcopy(self.matrix)
        item = next(x for x in changed["requirements"] if x["status"] == "PARTIAL")
        item["testIds"] = ["LSP33-FAKE-99"]
        self.assertTrue(any("unverified test link" in x
            for x in validate(changed, self.interim)))

    def test_no_normative_completion_claim_for_local_interim_RTMs(self):
        self.assertEqual("incomplete", self.matrix["normativeCoverage"])
        self.assertTrue(any(x["status"] == "BLOCKED" for x in self.matrix["requirements"]))


if __name__ == "__main__":
    unittest.main()
