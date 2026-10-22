# Cvolo diagnostic bundle analyzer (Inc 3.3 preliminary)

Requires Python 3.11+ only; no VS Code, Compiler, .NET SDK or network access.

1. In VS Code: **Start Diagnostic Recording**, reproduce an issue, **Add Diagnostic Marker**, **Stop Diagnostic Recording**, **Export Diagnostic Bundle**.
2. Review the ZIP for private data before sharing.
3. Run:

    python tools/analyze-diagnostic-bundle.py path/to/cvolo-diagnostics.zip path/to/new-report-directory

Outputs: `integrity.json` (hash checks, gaps, bad records), `incident-index.json` (markers and temporal candidates), `summary.md` (privacy-filtered preliminary findings).

**Limitations:** The archive's manifest has no independently trusted signature. Hash checks cannot prove replay completeness or privacy. Within-emitter producerSequence, not physical file order, governs event ordering. Nearby events are `temporal_only`, never automatically causal; unresolved causes are `insufficient_evidence`. This first pass does not reconstruct the full dependency DAG, independently calibrate clocks, or replay source changes. Raw attributes and source text are not copied to the summary.
