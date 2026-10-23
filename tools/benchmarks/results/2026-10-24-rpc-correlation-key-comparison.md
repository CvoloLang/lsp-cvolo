# Inc 3.3 — RPC correlation typed-key optimization, exploratory before/after

**Status: INCONCLUSIVE / directional only. This is not a normative performance PASS.**

## Change under test

At `21cbae8`, `RpcObservability` replaced per-request formatted dictionary keys
(`n:<id>`, `s:<id>`) with a typed readonly value key. A later change
(`0f522e6`) retained fresh per-event attribute arrays to prevent mutation
of one event's attributes affecting another event. The public wire format,
numeric/string ID separation, request UID construction, emitted event families,
and scheduling contract remain unchanged.

## Input provenance

- Before: `68bbae68c95d04c1a88b62055bd74fe4afc7f654`,
  [real-process LSP workflow #40018446336](https://github.com/CvoloLang/lsp-cvolo/actions/runs/40018446336)
- After: `0f522e69ab6676e5970601a11dafc5723409b3f8`,
  [real-process LSP workflow #40026796487](https://github.com/CvoloLang/lsp-cvolo/actions/runs/40026796487)
- Measured `textDocument/documentSymbol`; same synthetic fixture *type* and
  8 repetitions × 50 timed requests per mode (`off/basic/detailed`).
- A new process is created for every mode/repetition. CPU/memory sampling is
  per-process, non-atomic, and outside the measured request loop.
- GitHub-hosted runner identities, background contention, and machine
  scheduling are NOT fixed between the two runs. Cross-run comparison is
  exploratory, not causal or controlled. `repro` remains unsupported.

## Raw descriptive medians (allocated bytes per measured request)

| OS | Revision | Off | Basic | Detailed | Extra basic vs off | Extra detailed vs off |
|---|---|---:|---:|---:|---:|---:|
| Ubuntu | Before | 29,383.76 | 33,302.80 | 34,284.00 | +3,919.04 | +4,900.24 |
| Ubuntu | After | 29,012.08 | 32,796.72 | 33,872.08 | +3,784.64 | +4,860.00 |
| Windows | Before | 29,528.88 | 33,727.04 | 34,503.44 | +4,198.16 | +4,974.56 |
| Windows | After | 29,870.08 | 33,490.56 | 34,384.16 | +3,620.48 | +4,514.08 |

The differences in extra mode allocations are about **−134 / −40 bytes** for
Ubuntu basic/detailed and **−578 / −461 bytes** for Windows basic/detailed.
These are descriptive differences in medians of process-wide resource totals;
they do **not** isolate RPC-key allocations, nor establish statistical
significance or an approved overhead budget.

## Client-observed p95 request duration (µs)

| OS | Revision | Off | Basic | Detailed |
|---|---|---:|---:|---:|
| Ubuntu | Before | 498.550 | 573.900 | 564.510 |
| Ubuntu | After | 528.511 | 562.721 | 558.553 |
| Windows | Before | 1,654.100 | 1,059.600 | 1,074.800 |
| Windows | After | 956.600 | 935.500 | 985.200 |

The large change in Windows baseline p95 illustrates why these runs cannot
certify performance improvements or compliance with the 2%/5% targets.
Do not turn the green benchmark workflow into a performance-budget PASS.

## Acceptance

- Numeric `731` vs string `"731"` transport correlation has a new real
  protocol regression test, and standard CI independently checks both OSes.
- Normative `OBS-LSP-008` and `OBS-LSP-009` remain PARTIAL.
- The `repro` performance mode, fixed hosts, request-level attribution and
  compiler/Tooling/CLI/installed VSIX acceptance remain open.
- Next diagnostic step: controlled repeated paired runs with profiling of the
  actual per-request allocation sites, while retaining the same mandatory
  event coverage and data privacy requirements.
