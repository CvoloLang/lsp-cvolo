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


## Real LSP measurements — available modes (explicit opt-in)

The standalone \`collect_inc33_lsp_perf.py\` can drive a **real built server
process** over stdio and collect actual client-observed completion round trips.
It creates its own temporary Cvolo project, checks embedded server commit
provenance, rotates mode order across repetitions, starts a fresh process for
each run/mode, warms up completion, and captures the latency of successful
JSON-RPC responses using a monotonic clock.

\`\`\`bash
python tools/collect_inc33_lsp_perf.py \
  --server src/Cvolo.LanguageServer/bin/Release/net10.0/cvolo-language-server.dll \
  --output test-evidence/real-lsp-completion.json \
  --report test-evidence/real-lsp-completion-result.json
\`\`\`

Default: eight repetitions, 50 samples per available mode per repetition,
ten warmups, 20-second RPC timeout. The server build must embed a full
40-character commit SHA; unverifiable binaries are rejected. The benchmark
does not read user source files or enable sensitive capture. It does not use
\`--verbose\` and discards RPC/stderr text rather than writing sources to logs.

**Important:** Current \`setRecording.v1\` deliberately rejects \`repro\`.
The collector therefore measures ONLY \`off/basic/detailed\`, and the v4.1
four-mode evaluator reports **INCONCLUSIVE** for missing repro instead of a
fabricated PASS. Measurements on uncontrolled workstations are exploratory;
there is no approved baseline or end-to-end regression PASS. Future work
must add actual consented \`repro\` measurement, workloads other than
completion, and controlled host/GC/CPU/memory evidence before acceptance.

## Exploratory process CPU, GC and memory evidence

The LSP collector also calls `cvolo/observability/flightResourceSnapshot.v1`
once **after warmup, before the timed sample loop**, and once **after** it.
The server returns process-wide cumulative CPU time (`TimeSpan.Ticks`),
`GC.GetTotalAllocatedBytes`, Gen0/1/2 collection counts, managed heap
bytes and working-set bytes. The raw data contains
`runs[].processResourceDeltas[mode]` with the sampled interval, positive
cumulative deltas and two memory gauge values.

The client computes per-measured-request **process-average** CPU microseconds
and allocated bytes as exploratory descriptive figures only. These are not
the allocation or CPU cost of any specific handler: background work in the
same server process is included; CPU time can exceed elapsed wall time on
multiple cores. The resource-probe RPC calls themselves are **outside** the
request-timing loop but still create some allocations in the sampled
process. The two memory points **do not** measure peak RSS, and heap sizes can
decrease as GC proceeds. Failures in resource scope, monotonic clocks or
cumulative counters **abort** collection and publish an INCONCLUSIVE report
instead of a partial PASS.

Resource metrics have no numerical acceptance budgets yet. In particular
they do not satisfy compiler/Tooling resource acceptance, fixture breadth,
isolation of server background CPU or a normative `repro` benchmark.
