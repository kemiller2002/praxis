# Public site claim audit (PRAXIS-SITE-25)

Date: 2026-09-27. Every meaningful claim on `site/index.html` is classified,
checked against the repository, and corrected where it overstated.

Two passes:

1. **Build-time checks while writing each section.** For example, the
   runtime-detection list was checked against `Identity.fs`, the installers
   were run for real, and the reconciliation message was matched to
   `work-protocol.md`.
2. **An independent adversarial review.** A separate agent read only the page
   and the repository and was asked to find overstatement. It changed
   nothing. Its findings were then checked against the source before anything
   was changed.

Classes used:

- **Implemented**: true of the code today, with a citation.
- **Demonstrated**: shown on the page from real records, checked by
  `praxis-site evidence --check` (then `site-tools/evidence.mjs --check`).
- **Direction**: labelled on the page as architectural direction.
- **Planned**: none; nothing on the page is presented as scheduled.
- **Illustrative**: labelled "Illustrative execution".

## Corrected

| # | Original claim | Problem (evidence) | Now says |
| --- | --- | --- | --- |
| 1 | "When CI validates against a base commit, as this repository's does, it names the change and fails." | Unattributed-change findings are suppressed whenever any work item is active or blocked (`src/Ros.Domain/Work/Attribution.fs:29`), and this repository has open items. | Enforcement names such a change when no work is open, and the page says the check is lenient while work is active or blocked. |
| 2 | "Never quietly absorbed" / "not absorbed into the nearest one" | `work complete` attributes working-tree changes to the item being completed (`ContextPlan.fs:106-110`). | Changes are attributed while the work happens or reconciled later with a reason; reconciliation never invents a work item. |
| 3 | "When work changes hands, the next actor opens a new execution that points to its parent" | Block does not finalize, and resume rejoins every still-active execution, whoever the actor is (`TelemetryResolution.fs:73-77`). A new child is created only after finalization. | Blocking does not close an execution; resuming rejoins an open one; after finalization, resuming opens a child. |
| 4 | GH-84 handoff told as a change of hands | Both executions share one runtime session: nothing changed hands. | States that the same Claude Code session resumed, and that the session ID is withheld from the snapshot. Heading: "The identity was not assumed." |
| 5 | "The record keeps the mechanism, so a declaration is never mistaken for a detection." | The mechanism is per identity, not per field. `explicit-or-unmapped-environment` covers both a declaration and nothing detected (`Identity.fs:110-124`). | "A declaration takes precedence. The record keeps how provider and runtime were identified." |
| 6 | "Declared" listed as a value quality | Qualities in code are observed, derived and estimated (`TelemetryValidation.fs:74`). | "Estimated" replaces "Declared"; a test pins the three qualities to the code. |
| 7 | "Cost is taken from the runtime or calculated from a named price source; it is never guessed." | Praxis computes no cost. `telemetry record` defaults to observed with an agent-report source. | "Praxis does not compute cost; a cost calculated elsewhere must name its price table and version, and an estimate is labelled." |
| 8 | Tokens and cost "unavailable" on the real cards | The totals (`tokens.total`, `cost.execution_total`) are `unknown`; only components were `supported-unavailable`. | The generator reports the totals; the cards and GH-84 copy say "unknown". |
| 9 | "Test evidence is required to complete. A work item without it stays open." | This comes from configuration, and only file existence is checked (`Program.fs:1125-1128`). | "By default, completion requires naming an existing test file as evidence. … whether the tests pass is for CI to show." |
| 10 | "Each event carries the actor that caused it." | 204 of 281 events in this repository predate actor attribution. | "Events written by current versions carry the actor." |
| 11 | `init` adds `./ros` (next to "no Node.js required") | The repository-local launcher is a Node script. | Says the launcher runs on Node and the installed `praxis` command does not. |
| 12 | Execution "identity, start, end and baseline are recorded when it opens" | The end is written at finalization. | "…its end and change summary when it is finalized." |
| 13 | "measured against its clean baseline" | The summary includes uncommitted work and is unavailable when the baseline was dirty. | Says so. |
| 14 | Ledger counts | Stale as the build progressed. | Regenerated from the records at the final snapshot (PRAXIS-SITE-26); labelled "as of". |
| 15 | `registries/` "generated indexes of the above" | It indexes research records only. | "generated indexes of research records". |
| 16 | Site deployment "isolated from release workflows" | The tooling lived in `scripts/site/` and edited `package.json`, both of which `native-release.yml` watches on `main`. Merging would have re-uploaded v3.4.0 assets. | Tooling moved to `site-tools/` and `package.json` reverted; a mutation-checked test forbids overlap. |

Regression tests for 1-10: `tests/Site.Tests/ClaimsTests.fs` and
`tests/Site.Tests/ContentTests.fs` (ported from `tests/site/*.test.mjs` on
2026-09-28). For 16: `tests/Site.Tests/WorkflowTests.fs`.

Later change (2026-09-28): the repository became F#/.NET only. The page no
longer mentions an npm package or the repository-local launcher (row 11, and
the npm bullet under "From ROS to Praxis"); it says the native installers
install both `praxis` and `ros`, which `ContentTests.fs` pins to the
installers' source.

## Register after correction

| Claim area | Class | Backing |
| --- | --- | --- |
| Work lifecycle, illegal transitions refused | Implemented | work protocol; `allowedActions` in `work context` |
| Runtime detection (Codex, Claude Code, Gemini CLI, Copilot, GitHub Actions) | Implemented | `Identity.fs`; test pins each mechanism |
| Model never inferred; unknown recorded as unknown | Implemented, Demonstrated | `Identity.fs` (model only from input); every real card shows "unknown" |
| Missing values never zero | Implemented | `docs/development-telemetry.md:39` |
| Execution per attempt, parent link after finalize | Implemented, Demonstrated | resume logic; GH-84 cards |
| Completion requires named evidence (configurable) | Implemented | `FileWorkConfigRepository.fs:207-231` |
| Reconciliation, fail-closed rules | Implemented | `docs/work-protocol.md:113-133`; `work reconcile` |
| Attribution enforcement, lenient while work is open | Implemented | `Attribution.fs` |
| Records are files in the repository | Implemented | `.ros/`, `research/`, `registries/` exist (tested) |
| Ordo, work-adapter contract, `echelon` | Implemented | CLI commands (tested); installer requires `echelon` |
| Other Echelon integrations | Direction | labelled on page |
| Double-entry / fallback execution | Implemented | `docs/fallback-reconciliation.md`; `FileEnvelopeReconciliationDispatcher.fs`; crash/retry and checkpoint tests |
| Installers, checksums, `praxis`/`ros` names | Implemented | installers read; v3.4.0 run (`get-started-verification.md`) |
| "Done is a claim" answers | Demonstrated | `data-evidence`, checked against records |
| GH-84 handoff | Demonstrated | `data-evidence`, checked against records |
| Case-study ledger | Demonstrated | generated; checked |
| Execution-record anatomy | Illustrative | labelled in header and caption; test forbids real-looking IDs |
| Deployment | Not claimed | page says the snapshot predates deployment |

## Accepted nuance, not changed

- The checksum check in the installers proves integrity against the published
  checksum file, not publisher authenticity. The page says "checks it against
  the published checksums" and claims nothing more.
- The work-adapter contract exists and is exercised by a test double; no
  production tracker adapter ships. The page says "a transport-neutral adapter
  contract", not a named integration.
