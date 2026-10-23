# Inc 3.3 — executable acceptance map (interim)

This is a **repository-level acceptance index**, not the normative 87-scenario
Inc specification. Local IDs such as \`LSP33-ZIP-01\` are deliberately *not*
official scenario identifiers. The authoritative 87-case matrix has not yet
been reconciled against the Inc source document; **do not mark it complete**.

- \`implemented_test\`: exact xUnit method exists, and is part of the standard
  Windows/Ubuntu .NET test assembly (the CI job determines actual pass/fail).
- \`open\`: a requirement that cannot yet claim acceptance, with an explicit
  blocker. \`LSP33-GAP-07\` tracks the pending mapping to the 87 normative cases.
- \`normativeCoverage: unverified\`: the index is incomplete by design.
- Passing the static map gate **cannot prove** transport frame identity,
  producer closure, replay completeness, end-to-end latency, or correctness of
  the underlying test. It prevents silent loss and invented links.

\`python -m unittest discover -s tools/tests -v\` runs the RTM checks in the
existing CI workflow. The validation script can also be run directly:

\`\`\`bash
python tools/validate_inc33_rtm.py
\`\`\`

Before final acceptance, reconcile each requirement and negative scenario in
the authoritative Inc with the local IDs, implement missing tests, then replace
the interim map with a complete source-grounded normative RTM. Do not remove
the explicit gaps or change \`normativeCoverage\` merely to make a gate green.

Performance acceptance is separate: deterministic behavior and size budgets
should be hard CI gates; environment-sensitive wall-clock baselines need
dedicated, repeatable benchmarking on controlled runners.
