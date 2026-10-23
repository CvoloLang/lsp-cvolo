"""Executable static acceptance mapping tests for Inc 3.3."""
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from validate_inc33_rtm import load_and_validate, validate_matrix

MATRIX = ROOT / "acceptance" / "inc33-rtm.json"


class AcceptanceMatrixTests(unittest.TestCase):
    def setUp(self):
        self.document = json.loads(MATRIX.read_text(encoding="utf-8"))

    def test_registered_methods_exist_with_xunit_attributes(self):
        self.assertEqual([], load_and_validate())

    def test_no_claim_of_normative_87_case_completion(self):
        self.assertEqual("unverified", self.document["normativeCoverage"])
        self.assertIn("LSP33-GAP-07",
            {x["id"] for x in self.document["requirements"] if x["state"] == "open"})

    def test_detects_missing_test_reference(self):
        broken = json.loads(json.dumps(self.document))
        row = next(x for x in broken["requirements"] if x["state"] == "implemented_test")
        row["test"]["method"] = "ThisMethodDoesNotExist"
        self.assertTrue(any("missing enabled xUnit" in p for p in validate_matrix(broken)))

    def test_detects_incorrectly_closed_requirement(self):
        broken = json.loads(json.dumps(self.document))
        gap = next(x for x in broken["requirements"] if x["state"] == "open")
        gap["state"] = "implemented_test"
        self.assertTrue(any("missing explicit test reference" in p for p in validate_matrix(broken)))

    def test_detects_duplicate_case_and_path_traversal(self):
        broken = json.loads(json.dumps(self.document))
        broken["requirements"][1]["id"] = broken["requirements"][0]["id"]
        row = next(x for x in broken["requirements"] if x["state"] == "implemented_test")
        row["test"]["path"] = "tests/../secret.cs"
        errors = validate_matrix(broken)
        self.assertTrue(any("duplicate requirement ID" in p for p in errors))
        self.assertTrue(any("unsafe/invalid test path" in p for p in errors))


if __name__ == "__main__":
    unittest.main()
