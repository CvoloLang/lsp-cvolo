# Inc 3.3 v4.1 — performance gate contract

Source: §§17, 47, 61 and **OBS-AUD-017** of the 87-case spec.

This evaluator is **not** a latency benchmark runner. It consumes measured
same-host, repeated and alternating \`off/basic/detailed/repro\` samples
from a pre-registered workload and fixture. It reports **PASS / FAIL /
INCONCLUSIVE / NOT_RUN** without making fake performance guarantees.

- **Targets**, not owner-approved final limits: p95 increase 2% basic, 5%
  detailed, 10% repro. Do not raise these automatically.
- 8 repeated runs per mode and >=400 samples per mode, alternate execution
  order, cluster bootstrap on whole repetitions (seed 33017), uncertainty
  threshold 8% of baseline p95.
- Noise exceeding the threshold or confidence intervals intersecting a
  target gives **INCONCLUSIVE** rather than PASS.
- Metadata must contain OS, CPU, runner, SDK, build SHA and fixture SHA.
- Even a PASS covers only **one workload on one host**. It is not global
  acceptance. Exact cross-mode workload and cache conditions must be
  controlled by the measurement harness, separately audited.
- **No production baseline has been measured or approved by these unit
  tests**. They use artificial samples to test the verdict algorithm.
- Benchmark runner and coverage of Tooling, compiler CLI, LSP and VSIX
  still need implementation. CPU/GC/memory/log bytes and end-to-end
  editor fixtures remain open. Do not claim overall Inc 3.3 complete.

Public CI generates a \`NOT_RUN\` status when no measured evidence is
present and exercises the evaluator with synthetic fixtures as a stable
smoke check. Dedicated controlled-host CI supplies measured JSON and
uses \`--fail-on-regression\` (which rejects INCONCLUSIVE/NOT_RUN too).

Example invocation:

\`\`\`bash
python tools/evaluate_inc33_perf.py --report test-evidence/performance-smoke.json
python tools/evaluate_inc33_perf.py --input measured.json --report measured-result.json --fail-on-regression
\`\`\`

The \`measured.json\` file has a \`schemaVersion=1\`, \`evidenceKind=measured\`,
\`workload\`, \`fixtureClass\`, SHA-256 \`fixtureSha256\`, 40-character
\`buildSha\`, \`sdk\`, \`host {os,cpu,runnerId}\`, and \`runs\`: each
contains \`repetition\`, \`modeOrder\` and \`samplesUs\` arrays by mode.
Synthetic records must not be submitted as benchmark evidence.
