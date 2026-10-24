# Inc 3.3 v4.1 — fixed acceptance plan

Generated from `tools/acceptance/inc33-normative-rtm.json`.
**Never edit progress totals or acceptance checkboxes by hand.**
There are exactly **7 fixed families and 87 normative scenarios**.
A technical fix, implemented test, green CI or exploratory benchmark
does not count as normative PASS without a complete scenario oracle,
platform evidence, a verified build SHA and an auditable evidence artifact.

**Current acceptance:** 0/87 PASS; 22 PARTIAL; 65 BLOCKED; 0 FAIL.

| Fixed family | Normative cases | PASS | PARTIAL | BLOCKED | FAIL |
|---|---:|---:|---:|---:|---:|
| CORE — Telemetry kernel | 10 | 0 | 2 | 8 | 0 |
| CON — Concurrency and integration | 12 | 0 | 0 | 12 | 0 |
| COMP — Compiler / Tooling / CLI | 14 | 0 | 0 | 14 | 0 |
| LSP — Language Server | 11 | 0 | 5 | 6 | 0 |
| VSC — Installed VS Code extension | 6 | 0 | 0 | 6 | 0 |
| IO — Recording, replay and analysis | 15 | 0 | 7 | 8 | 0 |
| AUD — Release audit | 19 | 0 | 8 | 11 | 0 |
| **TOTAL** | **87** | **0** | **22** | **65** | **0** |

## Permanent acceptance checklist

A checked item means normative PASS only; PARTIAL remains unchecked.
The ID and its fixed family never change. New engineering subtasks
belong under these IDs rather than increasing the total number of stages.

### CORE — Telemetry kernel

- [ ] `OBS-CORE-001` **BLOCKED** — Valid/invalid JSONL, Unicode, exception inner chains, newline, escaping
- [ ] `OBS-CORE-002` **BLOCKED** — Schema v2 compatibility, optional fields, wrong types, unknown required version
- [ ] `OBS-CORE-003` **BLOCKED** — Все известные events/reasons/outcomes и их допустимые поля
- [ ] `OBS-CORE-004` **BLOCKED** — Operation started/completed/cancelled/faulted/degraded
- [ ] `OBS-CORE-005` **BLOCKED** — Process instance, atomic sequence и конкурентная запись
- [ ] `OBS-CORE-006` **PARTIAL** — Fake monotonic clock + real Stopwatch
- [ ] `OBS-CORE-007` **PARTIAL** — Nested, overlapping and parallel spans
- [ ] `OBS-CORE-008` **BLOCKED** — Runtime mode transitions, existing Lazy before detailed
- [ ] `OBS-CORE-009` **BLOCKED** — No-op and detached collector
- [ ] `OBS-CORE-010` **BLOCKED** — Event-sink throws/reenters, dispose race

### CON — Concurrency and integration

- [ ] `OBS-CON-001` **BLOCKED** — Создание нескольких проектов и snapshots
- [ ] `OBS-CON-002` **BLOCKED** — Два документа/проекта меняются параллельно
- [ ] `OBS-CON-003` **BLOCKED** — Два конкурентных запроса используют один snapshot с нетронутой Lazy
- [ ] `OBS-CON-004` **BLOCKED** — Конкурентные запросы приходят до/во время/после Lazy factory
- [ ] `OBS-CON-005` **BLOCKED** — Lazy factory падает, повторное обращение
- [ ] `OBS-CON-006` **BLOCKED** — Cancel одного из двух ожидающих запросов
- [ ] `OBS-CON-007` **BLOCKED** — Snapshot updated while old feature running
- [ ] `OBS-CON-008` **BLOCKED** — Измерение lock wait и hold при контролируемой конкуренции
- [ ] `OBS-CON-009` **BLOCKED** — Reentrant & nested tracing under compiler locks
- [ ] `OBS-CON-010` **BLOCKED** — 2+ рабочие области + 2 server instances
- [ ] `OBS-CON-011` **BLOCKED** — Watchdog на stalled worker/lock, cancel deferred
- [ ] `OBS-CON-012` **BLOCKED** — Coalescing diagnostic worker с быстрыми didChange

### COMP — Compiler / Tooling / CLI

- [ ] `OBS-COMP-001` **BLOCKED** — Прямой `Cvolo.Analysis.Binder.Bind` через collector
- [ ] `OBS-COMP-002` **BLOCKED** — Parser: valid/invalid/incomplete input, recovery
- [ ] `OBS-COMP-003` **BLOCKED** — Binder: declaration/validation/flow/safety + disabled mode
- [ ] `OBS-COMP-004` **BLOCKED** — Error/degraded/fallback с fault injection
- [ ] `OBS-COMP-005` **BLOCKED** — Resolver: visibility, overload, ambiguity, generic, unresolved
- [ ] `OBS-COMP-006` **BLOCKED** — Navigation and index factories: first build/cache/wait/fault
- [ ] `OBS-COMP-007` **BLOCKED** — Package source / closed project file / missing source
- [ ] `OBS-COMP-008` **BLOCKED** — Completion: normal, member recovery/probe, malformed edits
- [ ] `OBS-COMP-009` **BLOCKED** — Semantic tokens: compiler classification + LSP encoding
- [ ] `OBS-COMP-010` **BLOCKED** — Остальные Tooling API — parametric inventory test
- [ ] `OBS-COMP-011` **BLOCKED** — CLI compile/emit/link/packages path
- [ ] `OBS-COMP-012` **BLOCKED** — `off`, `basic`, `detailed`, targeted forensic cross-mode
- [ ] `OBS-COMP-013` **BLOCKED** — Точечный L2 на одном symbol/operation
- [ ] `OBS-COMP-014` **BLOCKED** — Глубина инструментации coverage

### LSP — Language Server

- [ ] `OBS-LSP-001` **BLOCKED** — F12 found within project (integration subprocess)
- [ ] `OBS-LSP-002` **BLOCKED** — F12 found in package source
- [ ] `OBS-LSP-003` **BLOCKED** — F12 unknown symbol / invalid position / missing source / invalid mapped target
- [ ] `OBS-LSP-004` **BLOCKED** — Request cancelled before queue / during lazy wait / after cancellation deferred
- [ ] `OBS-LSP-005` **PARTIAL** — DidOpen/DidChange/DidClose/DidSave и смешанные edits
- [ ] `OBS-LSP-006` **BLOCKED** — Diagnostics publish/clear, generation changes, stale commit
- [ ] `OBS-LSP-007` **BLOCKED** — Completion/hover/signature/rename/references/tokens/inlay/other handlers
- [ ] `OBS-LSP-008` **PARTIAL** — Concurrent JSON-RPC requests с числовым и строковым id
- [ ] `OBS-LSP-009` **PARTIAL** — JSON-RPC stdout hygiene при всех режимах, включая writer failure
- [ ] `OBS-LSP-010` **PARTIAL** — Headless LSP, startup/shutdown/restart/parent process loss
- [ ] `OBS-LSP-011` **PARTIAL** — Динамическое переключение logging без запуска нового server

### VSC — Installed VS Code extension

- [ ] `OBS-VSC-001` **BLOCKED** — Запуск установленного VSIX + bundled Tooling
- [ ] `OBS-VSC-002` **BLOCKED** — Start/Stop/Marker/Export + status bar
- [ ] `OBS-VSC-003` **BLOCKED** — Explain Highlighting at Cursor
- [ ] `OBS-VSC-004` **BLOCKED** — Theme/customizations + client middleware observable boundaries
- [ ] `OBS-VSC-005` **BLOCKED** — Отключение/отзыв source consent, timeout, повторный запуск
- [ ] `OBS-VSC-006` **BLOCKED** — Несколько окон VS Code и несколько процессов LSP

### IO — Recording, replay and analysis

- [ ] `OBS-IO-001` **BLOCKED** — Parallel event producers + bounded queue + batching
- [ ] `OBS-IO-002` **BLOCKED** — Overflow всех приоритетов, IO exception, disk full, permissions
- [ ] `OBS-IO-003` **BLOCKED** — Rotation and retention, crash last-line corruption
- [ ] `OBS-IO-004` **BLOCKED** — Shutdown flush deadline + abrupt kill
- [ ] `OBS-IO-005` **PARTIAL** — Adversarial privacy inputs в source/path/symbol/exception/stack
- [ ] `OBS-IO-006` **PARTIAL** — Source consent: allow-list, TTL, revoke, no initial snapshot
- [ ] `OBS-IO-007` **PARTIAL** — Repro unicode, CRLF, UTF-16 edits, multi-edit notifications
- [ ] `OBS-IO-008` **PARTIAL** — Missing/edit-dropped/reordered/duplicate didChange, corrupted source archive
- [ ] `OBS-IO-009` **PARTIAL** — Bundle schema, manifest, operations graph, critical path, coverage, integrity
- [ ] `OBS-IO-010` **BLOCKED** — One-file report at size limit, partial/full bundle
- [ ] `OBS-IO-011` **BLOCKED** — Compare before/after with equal inputs and changed source/config/build
- [ ] `OBS-IO-012` **BLOCKED** — Prompt-injection-like strings in source/comments/paths
- [ ] `OBS-IO-013` **BLOCKED** — Collector absent, observer fails while Binder locked
- [ ] `OBS-IO-014` **PARTIAL** — Flight recorder: pretrigger buffer, TTL, overflow, no-consent
- [ ] `OBS-IO-015` **PARTIAL** — Metric percentiles, lost-event counters, orphan/dependency links

### AUD — Release audit

- [ ] `OBS-AUD-001` **BLOCKED** — Start/Stop/Export получает server ACK, effective epoch/mode, не выводит ложного `recording_active` при timeout или failed RPC
- [ ] `OBS-AUD-002` **BLOCKED** — Snapshot/Lazy создан при `off`, затем activate detailed без restart; новая операция записывается, ранее выполненная не фабрикуется
- [ ] `OBS-AUD-003` **BLOCKED** — Writer/parser/schema/events/reasons едины по generated catalog; legacy alias не создаёт ложных причин
- [ ] `OBS-AUD-004` **BLOCKED** — При P0/P1 обгоне физический JSONL не принимается за producer order; восстановление local history корректно
- [ ] `OBS-AUD-005` **BLOCKED** — Disk-full/crash/drop до summary дают `loss_unknown`/`unknown_after_last_checkpoint`, не `complete`
- [ ] `OBS-AUD-006` **BLOCKED** — Операция, начатая до mode cutoff, не становится ложным зависанием; operation opened mid-recording не получает fabricated start
- [ ] `OBS-AUD-007` **BLOCKED** — Adversarial exception/source URI/symbol strings не попадают в raw JSONL/ring/basic/detailed без consent
- [ ] `OBS-AUD-008` **PARTIAL** — Revoke во время streaming writer/source capture предотвращает новые данные после barrier; revoke/purge не заявлены успешными без проверки
- [ ] `OBS-AUD-009` **PARTIAL** — Flight Recorder живой pretrigger vs kill-before-checkpoint различается; нет fake crash durability
- [ ] `OBS-AUD-010` **BLOCKED** — Static generated public-operation inventory / exclusions; новые entry points без L0 теста ломают gate
- [ ] `OBS-AUD-011` **BLOCKED** — Candidate Tooling bundle проходит isolated LSP+packed VSIX до release, затем опубликованный bundle снова проверяется по SHA
- [ ] `OBS-AUD-012` **BLOCKED** — Collector throws/reenters while Binder lock held → no callback under lock, deadlock, reordered compiler results
- [ ] `OBS-AUD-013` **PARTIAL** — Синтетические overlapping time spans не двойным счётом показываются; cross-process timing unknown при skew
- [ ] `OBS-AUD-014` **PARTIAL** — Document replay complete отдельно от semantic partial; corruption на version boundary ломает нужный статус
- [ ] `OBS-AUD-015` **PARTIAL** — Typed numeric/string StreamJsonRpc IDs связаны с requestUid при конкуренции или документирован безопасный observable fallback
- [ ] `OBS-AUD-016` **BLOCKED** — Нельзя пометить новый observability-тест baseline-known-failure; waiver должен иметь owner+evidence
- [ ] `OBS-AUD-017` **PARTIAL** — Performance gate с pre-registered sample count/noise/retry policy выдаёт PASS/FAIL/INCONCLUSIVE, без самовольного изменения бюджета
- [ ] `OBS-AUD-018` **PARTIAL** — Marker incident index даёт temporal vs causal различие и ссылки на наблюдаемые события
- [ ] `OBS-AUD-019` **PARTIAL** — Mixed major/minor schema, unknown event/reason и старый reader не дают ложного root cause

## Release versus development

- Development CI checks the integrity of the matrix, test references,
  and this generated document; incomplete requirements are allowed.
- Release-tag CI invokes --require-complete and rejects releases
  unless all 87 normative scenarios have verified PASS evidence.
- Local implementation tests and CI success are recorded separately
  from normative acceptance. See the requirements-test-matrix.md
  sibling document for test references and oracle details.
