#!/usr/bin/env python3
"""Conservative Inc 3.3 performance evidence evaluator (v4.1 §§17,47,61).

No benchmark is run here. It evaluates measured, host-paired repeated samples.
Synthetic fixtures are allowed ONLY in evaluator unit tests and cannot be
promoted to real measurements. Missing input is NOT_RUN, never PASS.
Do not edit 2/5/10% target budgets to suppress a real regression.
"""
import argparse
import json
import math
import random
import statistics
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SPEC_PATH = ROOT / "benchmarks" / "inc33-spec.json"


def quantile(values, q):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(q * len(ordered)) - 1)]


def _interval(pairs, mode, spec):
    """Cluster bootstrap entire paired repetitions, not independent requests."""
    rng = random.Random(spec["bootstrapSeed"])
    vals = []
    for _ in range(spec["bootstrapReplicates"]):
        selected = [pairs[rng.randrange(len(pairs))] for _ in pairs]
        baseline = [x for run in selected for x in run["samplesUs"]["off"]]
        compared = [x for run in selected for x in run["samplesUs"][mode]]
        vals.append(quantile(compared, .95) / quantile(baseline, .95) - 1)
    return quantile(vals, .025), quantile(vals, .975)


def evaluate(data, spec=None):
    spec = spec or json.loads(SPEC_PATH.read_text(encoding="utf-8"))
    empty = {
        "schemaVersion": 1, "status": "NOT_RUN", "reason": "no_measured_samples",
        "normativeBudgetApproved": bool(spec.get("finalBudgetApproved", False)),
        "modeResults": {}, "sampleScope": "none",
        "allWorkloadsComplete": False, "endToEndRegressionGatePassed": False,
    }
    if data is None:
        return empty

    # Metadata is required for fair comparisons. These strings are labels and
    # never used to open paths, load code or execute commands.
    required = ("schemaVersion", "evidenceKind", "workload", "fixtureClass",
                "fixtureSha256", "buildSha", "host", "sdk", "runs")
    if not isinstance(data, dict) or any(k not in data for k in required):
        return {**empty, "status": "INCONCLUSIVE", "reason": "missing_provenance"}
    if (data["schemaVersion"] != 1 or data["evidenceKind"] != "measured"
        or data["workload"] not in spec["workloads"]
        or data["fixtureClass"] not in spec["fixtureClasses"]
        or not isinstance(data["fixtureSha256"], str)
        or len(data["fixtureSha256"]) != 64
        or not all(c in "0123456789abcdef" for c in data["fixtureSha256"])
        or not isinstance(data["buildSha"], str)
        or len(data["buildSha"]) != 40
        or not all(c in "0123456789abcdef" for c in data["buildSha"])
        or not isinstance(data["host"], dict)
        or not all(isinstance(data["host"].get(k), str) and data["host"][k]
                   for k in ("os", "cpu", "runnerId"))
        or not isinstance(data["sdk"], str) or not data["sdk"]):
        return {**empty, "status": "INCONCLUSIVE", "reason": "invalid_provenance"}

    runs = data["runs"]
    if not isinstance(runs, list) or len(runs) < spec["minRepetitions"]:
        return {**empty, "status": "INCONCLUSIVE", "reason": "insufficient_repetitions"}
    totals = {name: 0 for name in spec["modeOrder"]}
    expected_ids = set(range(len(runs)))
    if {r.get("repetition") for r in runs if isinstance(r, dict)} != expected_ids:
        return {**empty, "status": "INCONCLUSIVE", "reason": "invalid_repetition_ids"}

    for r in runs:
        if not isinstance(r, dict):
            return {**empty, "status": "INCONCLUSIVE", "reason": "invalid_run"}
        order = r.get("modeOrder")
        samples = r.get("samplesUs")
        if (not isinstance(order, list) or sorted(order) != sorted(spec["modeOrder"])
            or not isinstance(samples, dict)
            or set(samples) != set(spec["modeOrder"])):
            return {**empty, "status": "INCONCLUSIVE", "reason": "missing_or_duplicated_mode"}
        # A fixed presentation order biases cache/thermal effects.
        if order == spec["modeOrder"] and all(
            other.get("modeOrder") == order for other in runs
        ):
            return {**empty, "status": "INCONCLUSIVE", "reason": "non_alternating_modes"}
        for mode, group in samples.items():
            if (not isinstance(group, list) or len(group) == 0
                or len(group) > 100000
                or any(not isinstance(x, (int, float)) or isinstance(x, bool)
                       or not math.isfinite(x) or x <= 0 for x in group)):
                return {**empty, "status": "INCONCLUSIVE", "reason": "invalid_sample_values"}
            totals[mode] += len(group)

    if min(totals.values()) < spec["minSamplesPerMode"]:
        return {**empty, "status": "INCONCLUSIVE", "reason": "insufficient_samples",
                "samplesPerMode": totals}

    off = [x for run in runs for x in run["samplesUs"]["off"]]
    base_p95 = quantile(off, .95)
    rng = random.Random(spec["bootstrapSeed"])
    baselines = []
    for _ in range(spec["bootstrapReplicates"]):
        chosen = [runs[rng.randrange(len(runs))] for _ in runs]
        baselines.append(quantile(
            [x for r in chosen for x in r["samplesUs"]["off"]], .95))
    lower_baseline, upper_baseline = quantile(baselines, .025), quantile(baselines, .975)
    width = (upper_baseline - lower_baseline) / base_p95

    details = {}
    for mode in ("basic", "detailed", "repro"):
        samples = [x for run in runs for x in run["samplesUs"][mode]]
        delta = quantile(samples, .95) / base_p95 - 1
        lower, upper = _interval(runs, mode, spec)
        target = spec["targetP95OverheadPercent"][mode] / 100
        if width > spec["maxBaselineRelativeCiWidth"]:
            verdict = "INCONCLUSIVE"
            cause = "noisy_baseline"
        elif upper <= target:
            verdict = "PASS"
            cause = "upper_confidence_bound_within_target"
        elif lower > target:
            verdict = "FAIL"
            cause = "lower_confidence_bound_exceeds_target"
        else:
            verdict = "INCONCLUSIVE"
            cause = "confidence_interval_overlaps_target"
        details[mode] = {
            "status": verdict, "reason": cause,
            "targetP95OverheadPercent": target * 100,
            "observedP95OverheadPercent": round(delta * 100, 5),
            "confidence95LowerPercent": round(lower * 100, 5),
            "confidence95UpperPercent": round(upper * 100, 5),
            "p50Us": quantile(samples, .5), "p95Us": quantile(samples, .95),
            "p99Us": quantile(samples, .99),
        }

    statuses = [v["status"] for v in details.values()]
    status = ("FAIL" if "FAIL" in statuses else
              "INCONCLUSIVE" if "INCONCLUSIVE" in statuses else "PASS")
    # This report is only ONE workload from ONE host. It cannot certify all
    # workloads or an approved final benchmark baseline.
    return {
        "schemaVersion": 1, "status": status,
        "reason": "single_host_workload_evaluation",
        "sampleScope": "single_host_single_workload",
        "workload": data["workload"],
        "fixtureClass": data["fixtureClass"],
        "buildSha": data["buildSha"],
        "host": data["host"], "sdk": data["sdk"],
        "samplesPerMode": totals, "repetitions": len(runs),
        "baselineP95Us": base_p95,
        "baselineRelativeCiWidth": round(width, 6),
        "modeResults": details,
        "normativeBudgetApproved": bool(spec["finalBudgetApproved"]),
        "allWorkloadsComplete": False,
        "endToEndRegressionGatePassed": False,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path)
    parser.add_argument("--report", required=True, type=Path)
    parser.add_argument("--fail-on-regression", action="store_true")
    args = parser.parse_args()
    raw = json.loads(args.input.read_text(encoding="utf-8")) if args.input else None
    report = evaluate(raw)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n",
                           encoding="utf-8")
    print(f"Inc 3.3 performance evidence: {report['status']} ({report['reason']})")
    # In public CI with no paired measurement input this tool reports NOT_RUN;
    # its unit tests are the stable smoke gate. Dedicated controlled CI should
    # use --fail-on-regression with actual evidence.
    return 1 if args.fail_on_regression and report["status"] != "PASS" else 0


if __name__ == "__main__":
    raise SystemExit(main())
