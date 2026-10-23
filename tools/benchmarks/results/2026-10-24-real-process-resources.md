# Inc 3.3 — real LSP process-resource measurements (2026-10-24)

**EXPLORATORY / INCONCLUSIVE. This is not a normative performance PASS or a proven per-request CPU cost.**

## Provenance and sampling

- [GitHub Actions real-process run #40016490239](https://github.com/CvoloLang/lsp-cvolo/actions/runs/40016490239); both Windows and Ubuntu completed successfully
- Measured `document_symbol` in real LSP processes against a synthetic Cvolo fixture; 8 repetitions, 50 requests per mode per repetition (400 timed responses per mode)
- Embedded LSP build identity: `ffc790da9eacb3520ffdfe0816d15030993ac11b`
- Separate process per mode; warmups preceding sample capture; process resource counters captured once before and once after the timed request loop
- Modes: `off/basic/detailed`; `repro` is not supported through the current recording-control contract
- Artifacts (retention-limited): `inc33-real-lsp-windows-latest-document_symbol` and `inc33-real-lsp-ubuntu-latest-document_symbol`

## Observed figures

All figures below describe **these two particular GitHub-hosted CI runs**. Latency p95 is client-observed, CPU and allocation columns are medians of *whole-process interval totals divided by 50 measured calls*; background server activity and resource-probe overhead remain included.

| Host | Mode | RPC p95 (µs) | Observed p95 Δ | Median process CPU µs/request | Median process allocated bytes/request | Observed allocated Δ |
|---|---|---:|---:|---:|---:|---:|
| Windows | off | 518.000 | baseline | 625.00 | 29,657.36 | baseline |
| Windows | basic | 552.000 | +6.56% | 625.00 | 33,811.52 | +14.01% |
| Windows | detailed | 550.700 | +6.31% | 937.50 | 34,569.04 | +16.56% |
| Ubuntu | off | 269.002 | baseline | 411.83 | 29,379.20 | baseline |
| Ubuntu | basic | 296.628 | +10.27% | 441.20 | 33,290.08 | +13.31% |
| Ubuntu | detailed | 320.393 | +19.10% | 444.96 | 34,150.24 | +16.24% |

- Baseline p95 spread across runs: **Windows 7.471%**, **Ubuntu 45.1272%**. Ubuntu exceeds the preregistered 8% noise threshold; it must not be marked PASS.
- None of the eight measured intervals per mode generated Gen0 collections; this does **not** prove no GC or allocation overhead outside those windows.
- Windows process CPU samples have visibly coarse resolution; do not interpret the per-request CPU figures as fine-grained handler work or infer parallelism from one run.
- Two working-set/managed-heap gauges per interval cannot establish steady-state/peak process memory. No per-request allocation attribution is claimed.
- GitHub-hosted machines are **not controlled performance runners**; startup/JIT, contention and other host effects are not eliminated.
- Strict normative verdict remains `INCONCLUSIVE` with reason `missing_or_duplicated_mode`. `repro`, broad workloads, controlled host, full LSP/Tooling/CLI/VSIX coverage and accepted budgets remain open.

## Evidence integrity notes

SHA-256 digests of the downloaded GitHub artifact ZIP bytes at inspection time:

- Windows: `a0fc1b35d46639d58708ec33fda04991251c08819d9221c6892562f383a6224d`
- Ubuntu: `b6bcbb1365524125e0a8466322c042b02304c92a47803bca30f44c9409357873`

Artifact retention is seven days, so these hash values prove only equality with *those downloaded ZIPs* if the files are still available; they are not signature, trusted provenance, or permanent storage.

## Next engineering checks

Reduce and explain observability-path allocations with functionality-preserving profiling rather than disabling required L0 evidence to meet a number. Compare CPU/GC/memory on fixed runners across independent repetitions, implement the full four-mode consented synthetic benchmark, and keep `INCONCLUSIVE` until the official acceptance contract has complete evidence.
