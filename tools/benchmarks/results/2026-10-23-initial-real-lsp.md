# Inc 3.3 — first measured LSP performance evidence (2026-10-23 UTC)

**Status: EXPLORATORY / INCONCLUSIVE. Never treat as normative PASS/FAIL.**

The measurements below were collected by real `cvolo-language-server` processes on
GitHub-hosted runners. Each method and recording mode used a fresh server
process; there were 8 repetitions with 50 measured calls per mode and repetition
(400 samples/mode), after 10 warmups. The recording modes were
`off`, `basic`, `detailed`. `repro` remains unsupported.

## Provenance

- [Completion real-process run #40012817730](https://github.com/CvoloLang/lsp-cvolo/actions/runs/40012817730);
  binary provenance `bd6e6394ff557239a3c1c3251ec33937641e8d83`.
- [Validated documentSymbol run #40014573770](https://github.com/CvoloLang/lsp-cvolo/actions/runs/40014573770);
  binary provenance `35a45900102ffbbe16c826469a21764de167210a`.
- Ubuntu and Windows jobs: SUCCESS for both real-process runs. Raw measured
  samples are preserved as short-retention GitHub Actions artifacts; this
  summary does not claim the raw samples are permanently retained.
- .NET SDK: `10.0.402` (from first run's artifact). GitHub-hosted runners are
  **not controlled or fixed performance hosts**.
- Metrics are client-observed RPC wall-clock round trips, **not pure backend
  CPU time, GC time, diagnostic overhead or a causal critical path**.

## Completion (contains intentional 120 ms quiet period)

`CompletionHandler` intentionally runs `Task.Delay(CompletionQuietPeriodMs)`
where `CompletionQuietPeriodMs = 120`. These totals therefore mostly measure
the debounce/quiet period and are unsuitable on their own for detecting small
instrumentation overhead on fast paths.

| Host | Mode | p50 ms | p95 ms | p99 ms | Observed p95 Δ versus off |
|---|---|---:|---:|---:|---:|
| Ubuntu | off | 122.41 | 127.75 | 130.46 | baseline |
| Ubuntu | basic | 122.47 | 128.10 | 131.55 | +0.27% |
| Ubuntu | detailed | 122.46 | 128.48 | 131.43 | +0.57% |
| Windows | off | 123.88 | 134.10 | 136.13 | baseline |
| Windows | basic | 123.76 | 134.17 | 137.67 | +0.05% |
| Windows | detailed | 124.54 | 134.08 | 136.93 | -0.01% |

## Validated documentSymbol (no fixed quiet period)

The validated collector **rejects empty/invalid symbol arrays** for its
test fixture containing a top-level `Main`. This measures a shorter,
non-debounced request path. Latencies below are in microseconds.

| Host | Mode | p50 µs | p95 µs | p99 µs | Observed p95 Δ versus off |
|---|---|---:|---:|---:|---:|
| Ubuntu | off | 461.878 | 518.604 | 550.156 | baseline |
| Ubuntu | basic | 488.774 | 539.372 | 564.978 | +4.00% |
| Ubuntu | detailed | 499.378 | 561.854 | 602.839 | +8.34% |
| Windows | off | 469.400 | 520.900 | 548.300 | baseline |
| Windows | basic | 487.900 | 542.600 | 584.000 | +4.17% |
| Windows | detailed | 486.300 | 539.700 | 560.100 | +3.61% |

Per-run baseline p95 spread relative to pooled baseline p95:
- **Ubuntu 17.45%** — exceeds the evaluator's 8% noise threshold.
- **Windows 5.86%** — under 8%, but the 95% cluster-bootstrap intervals of
  observed p95 overhead overlap their respective tentative budget targets:
  basic approximately +0.65% to +6.20% (target +2%);
  detailed approximately -0.95% to +5.36% (target +5%).
- For context, Ubuntu bootstrap intervals were approximately +1.06% to
  +7.54% for basic and +5.45% to +10.51% for detailed, but baseline
  drift already makes its conclusions **INCONCLUSIVE**.

Bootstrap intervals above are exploratory diagnostics, not verified
certification results. No confidence-bound budget verdict is recorded as PASS.

## Acceptance impact

- Real-process collector and push-triggered opt-in benchmark workflow: **tested**.
- Two real LSP methods and three recording modes: **measured**.
- Overall normative regression gate (2/5/10% tentative thresholds):
  **INCONCLUSIVE**. Never silently reinterpret these results as PASS.
- Missing `repro`, controlled/fixed runners, broad fixture/workload matrix,
  reliable across-process/transport correlation, CPU/GC/memory measurements,
  compiler Tooling/CLI overhead, VSIX E2E, full official 87-case RTM.

The next work should specifically exercise and profile short requests on
controlled hardware, address the observed overhead where supported, and
collect evidence under the same immutable benchmark contract. Do not remove
the deliberate completion quiet period to make this benchmark look better.
