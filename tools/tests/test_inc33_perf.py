"""Deterministic perf-budget decision tests; NOT benchmark measurements."""
import copy
import importlib.util
import math
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "evaluate_inc33_perf.py"
module_spec = importlib.util.spec_from_file_location("inc33_perf_gate", SCRIPT)
perf = importlib.util.module_from_spec(module_spec)
module_spec.loader.exec_module(perf)


def case(multiplier=1.0, noisy=False, count=50):
    modes = ["off", "basic", "detailed", "repro"]
    runs = []
    for i in range(8):
        order = modes[i % 4:] + modes[:i % 4]
        base = 100 + (40 if noisy and i % 2 else 0)
        runs.append({
            "repetition": i, "modeOrder": order,
            "samplesUs": {m: [base * (1 if m == "off" else multiplier)] * count
                          for m in modes},
        })
    return {
        "schemaVersion": 1, "evidenceKind": "measured",
        "workload": "completion", "fixtureClass": "small",
        "fixtureSha256": "a" * 64, "buildSha": "b" * 40,
        "host": {"os": "linux", "cpu": "synthetic", "runnerId": "unit-test"},
        "sdk": "test-fixture", "runs": runs,
    }


class PerformanceDecisionTests(unittest.TestCase):
    def test_no_measurements_never_pass(self):
        report = perf.evaluate(None)
        self.assertEqual("NOT_RUN", report["status"])
        self.assertFalse(report["endToEndRegressionGatePassed"])

    def test_under_budget_and_constant_baseline(self):
        result = perf.evaluate(case(1.005))
        self.assertEqual("PASS", result["status"])
        self.assertEqual(400, result["samplesPerMode"]["off"])
        self.assertFalse(result["endToEndRegressionGatePassed"])
        self.assertFalse(result["normativeBudgetApproved"])
        self.assertEqual(100, result["baselineP95Us"])

    def test_budget_exceeded_reports_fail_not_silent_waiver(self):
        result = perf.evaluate(case(1.15))
        self.assertEqual("FAIL", result["status"])
        for mode in ("basic", "detailed", "repro"):
            self.assertEqual("FAIL", result["modeResults"][mode]["status"])
        self.assertEqual(2, result["modeResults"]["basic"]["targetP95OverheadPercent"])

    def test_insufficient_samples_is_inconclusive(self):
        result = perf.evaluate(case(1.0, count=20))
        self.assertEqual("INCONCLUSIVE", result["status"])
        self.assertEqual("insufficient_samples", result["reason"])

    def test_noisy_baseline_is_inconclusive(self):
        report = perf.evaluate(case(1.0, noisy=True))
        self.assertEqual("INCONCLUSIVE", report["status"])
        self.assertGreater(report["baselineRelativeCiWidth"], .08)

    def test_invalid_provenance_or_non_alternating_order_is_inconclusive(self):
        invalid = case()
        invalid["fixtureSha256"] = "not-hash"
        self.assertEqual("INCONCLUSIVE", perf.evaluate(invalid)["status"])
        same_order = case()
        for r in same_order["runs"]:
            r["modeOrder"] = ["off", "basic", "detailed", "repro"]
        self.assertEqual("non_alternating_modes", perf.evaluate(same_order)["reason"])

    def test_invalid_clock_samples_never_pass(self):
        corrupted = case()
        corrupted["runs"][0]["samplesUs"]["basic"][0] = math.nan
        self.assertEqual("invalid_sample_values", perf.evaluate(corrupted)["reason"])

    def test_one_missing_mode_is_inconclusive(self):
        missing = case()
        del missing["runs"][0]["samplesUs"]["repro"]
        self.assertEqual("missing_or_duplicated_mode", perf.evaluate(missing)["reason"])

    def test_no_improvised_budget_waivers(self):
        spec = copy.deepcopy(perf.json.loads(perf.SPEC_PATH.read_text(encoding="utf-8")))
        self.assertEqual({"basic": 2, "detailed": 5, "repro": 10},
                         spec["targetP95OverheadPercent"])
        self.assertFalse(spec["finalBudgetApproved"])


if __name__ == "__main__":
    unittest.main()
