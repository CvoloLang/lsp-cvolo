# Cvolo diagnostic bundle analyzer (Inc 3.3 preliminary)

Requires Python 3.11+ only; no VS Code, Compiler, .NET SDK or network access.

1. In VS Code: **Start Diagnostic Recording**, reproduce an issue, **Add Diagnostic Marker**, **Stop Diagnostic Recording**, **Export Diagnostic Bundle**.
2. Review the ZIP for private data before sharing.
3. Run:

    python tools/analyze-diagnostic-bundle.py path/to/cvolo-diagnostics.zip path/to/new-report-directory

Outputs: `integrity.json` (hash checks, gaps, bad records), `incident-index.json` (markers and temporal candidates), `summary.md` (privacy-filtered preliminary findings).

**Limitations:** The archive's manifest has no independently trusted signature. Hash checks cannot prove replay completeness or privacy. Within-emitter producerSequence, not physical file order, governs event ordering. Nearby events are `temporal_only`, never automatically causal; unresolved causes are `insufficient_evidence`. This first pass does not reconstruct the full dependency DAG, independently calibrate clocks, or replay source changes. Raw attributes and source text are not copied to the summary.

The analyzer also writes `operations.json` (explicit request/operation ownership and observed first failures) and `diagnostic-report.txt` (portable evidence summary). The text report explicitly **omits the complete raw timeline**, so it cannot be used alone for replay; retain the original verified ZIP. Snapshot sharing does not imply request causation, and markers have `temporal_only` associations.

Operation dependency audit: `operations.json` now contains `dependencyAudit`, including explicit parent/dependency edges, unresolved targets and missing/multiple terminal observations. A missing dependency target may have been rotated away; a missing terminal does not prove an active hang. Dependency links are joined only within the same emitter. In v1, `dependencyId` is read from the event `attributes` map when present.

Timeline and invariants (preliminary): `timeline.jsonl` preserves emitter-local producer order using allowlisted fields only; `assertions.json` distinguishes observed pass/fail from unknown and points to evidence; `performance.json` reports nearest-rank p50/p95/p99 from emitted `elapsedUs` values only. Unknown clock frequency, cross-process clock skew, CPU attribution and critical path are never guessed. No raw JSONL attributes are copied into these reports.

Privacy and observation coverage: `privacy.json` reports heuristic risks (sensitive keys, absolute paths, apparent credentials) with event references but never copies raw matching values. `coverage.json` reports which diagnostic event families are observed in retained JSONL, not which code paths are instrumented. Both reports remain explicitly inconclusive about complete privacy or trace coverage.
