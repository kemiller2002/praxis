# Group analysis: GROUP-PRAXIS-HYG

- Group: `GROUP-PRAXIS-HYG` (`./praxis work group show GROUP-PRAXIS-HYG`)
- Members: `PRAXIS-HYG-01`, `PRAXIS-HYG-02`, `PRAXIS-HYG-03`, `PRAXIS-HYG-04`
- Execution repository: repository-operating-system
- Base commit: `f242aae6` (origin/main, PR #177 merged)
- Source: the state and documentation hygiene track of the requirements gap
  audit (canonical state contamination, validate warnings, stale status rows).

## 1. Members

| Member | Obligation | Acceptance criteria | Depends on |
|---|---|---|---|
| PRAXIS-HYG-01 | Retire the leaked fixture items `WI-READY` and `WI-ACTIVE`; keep tests out of canonical state | 1. Both leave live work through `praxis work abandon` with a reason; the planner no longer reports them as active or blocked. 2. CI fails when the F# test suite changes a tracked file or leaves an untracked file. 3. `docs/work-protocol.md` says smoke tests of mutating commands run against a scratch root. | - |
| PRAXIS-HYG-02 | `validate` fails on backlog/context status disagreement; repair `WI-0061` | 1. A pure domain rule reports a backlog row whose terminal status (`complete`, `abandoned`) contradicts the live item with the same ID, in `validate` and `work backlog-validate`; tests cover agreement, both directions and non-terminal rows. 2. `praxis work reidentify` renames only a backlog row that collides with a live item, refuses invalid or used IDs and a missing reason, records `reidentifiedFrom`/`reidentifiedReason`, under the work-protocol lock and the backlog journal; tests cover each rejection and the effect. 3. `WI-0061`'s row is repaired with it and `validate` passes. | - |
| PRAXIS-HYG-03 | Record the missing `created` provenance of `EV-ROS-2026-A063` and `EV-ROS-2026-A067` truthfully | The true originator records the creation, or an accepted decision defines an explicit origin-unrecorded acknowledgement; `validate` then has no originator warning for either. | owner action |
| PRAXIS-HYG-04 | Correct the GRP-080..088 replication status; guard status tables | 1. The row cites `EV-ROS-2026-A070` and no longer says replication is pending. 2. A repository test fails when a status-table row cites a missing record, a missing `tests/` or `src/` path, or an unknown work item. | - |

## 2. Reuse inventory

Searches: `grep -rn "BacklogQueueValidation" src tests`,
`grep -rn "effectiveStatus" src`, `grep -rn "DuplicateInContext\|nextId" src/Praxis.Domain/Work`,
`grep -rn "commitQueue\|applyStateChange" src/Praxis.Infrastructure/Work`,
`grep -rn "repositoryRoot" tests/Praxis.Tests`, `grep -rn "work abandon\|runWorkAbandon" src/Praxis.Cli`.

| Existing element | Location | What it does | Disposition |
|---|---|---|---|
| `BacklogQueueValidation.findings` | `src/Praxis.Domain/Work/QueueValidation.fs:22` | Pure findings over raw queue rows (duplicates, invalid id/status/priority) | Extended: a sibling `contextDisagreements` in the same module, same finding type |
| `QueuePresentation.effectiveStatus` | `src/Praxis.Domain/Work/QueuePresentation.fs:25` | Merged status: a non-ready live item wins | Reused as the semantics the rule protects (a terminal backlog status that the merged view would hide is the disagreement) |
| `WorkCapture.plan` / `nextId` | `src/Praxis.Domain/Work/Capture.fs:61-115` | Allocates IDs avoiding queue, context and history; rejects explicit IDs used anywhere | Reused: the reidentify rule applies the same three ID sets and `WorkItemId.isValid` |
| `WorkItemId.isValid` | `src/Praxis.Domain/Work/Identity.fs:7` | ID grammar | Reused |
| `FileBacklogQueueRepository.commitQueue` | `src/Praxis.Infrastructure/Work/FileBacklogQueueRepository.fs:272` | Regenerates `queue.md` and commits both files through the backlog journal | Reused by the new `reidentify` effect |
| `RegistryLock` "work-protocol" + `WorkStateTransaction.recover`/`BacklogStateTransaction.recover` | `src/Praxis.Cli/Program.fs:613-628` | Lock and crash recovery around every backlog effect | Reused, same order |
| `FileEventLogRepository.readWorkItemIds` | `src/Praxis.Infrastructure/Work/FileEventLogRepository.fs:42` | IDs seen in event history | Reused for the "new ID already used" check |
| `computeUnifiedFindings` / `runBacklogQueueValidate` | `src/Praxis.Cli/Program.fs:2007-2080` | Composes validate contributors | Extended with the new contributor |
| `work abandon` | `src/Praxis.Cli/Program.fs` (`runWorkAbandon`) | Abandons live work and backlog rows, finalizes telemetry | Reused to retire the fixtures (no new command) |
| `repositoryRoot` test helper | `tests/Praxis.Tests/ArchitectureTests.fs:33` | Locates the checkout from the test binary | Reused pattern for the status-table test |
| Praxis validation workflow | `.github/workflows/praxis-validation.yml` | Builds, runs F# tests, validates | Extended with a clean-checkout step after the tests |

New abstractions:

| Name | Considered existing | Why not reused |
|---|---|---|
| `BacklogReidentification.plan` (domain) | `WorkUpdate.plan` (`src/Praxis.Domain/Work/Update.fs`), `BacklogTransition.decide` | Update changes title/description/tags/priority and never identity; transitions change status only. Renaming a row is a distinct, narrowly allowed repair. |
| `work reidentify` (CLI) | `work update`, `work backlog-transition` | Same reason; the effect reuses their lock, recovery and journaled commit. |

## 3. Group-level design

- Shared invariant: a backlog row and a live work item with the same ID are one
  obligation, so a terminal backlog status must agree with the live state.
  Rows still `captured`/`ready`/`blocked` are the normal pre-promotion
  record (63 completed items keep `ready`), so they are not disagreements.
- One rule serves PRAXIS-HYG-02 and makes PRAXIS-HYG-01's clean-up checkable:
  abandoning a fixture through `work abandon` moves both records to
  `abandoned`, which the rule accepts.
- Rejected rules: a validate rule for "fixture-shaped" IDs is unsound
  (`WorkItemId.isValid` admits any shape, and test sources legitimately cite
  real IDs such as `GH-84`, `WI-0003`); a semantic status-table rule ("not
  implemented" while a cited item is complete) is unsound because rows mix
  complete and open items (GRP-073).
- Common tests: domain tests in `QueueValidationTests`, effect tests in a new
  reidentify test file, repository tests for status tables.
- Risk of solving independently: fixing `WI-0061` by hand would bypass the
  journal and hide the class; a validate rule without a repair command
  would leave the repository permanently failing.

## 4. Order

1. PRAXIS-HYG-01 (state clean-up via existing commands, CI guard, guidance).
2. PRAXIS-HYG-02 (rule, command, repair) — the rule lands with the repair so
   `validate` never fails on main.
3. PRAXIS-HYG-04 (doc row, status-table test).
4. PRAXIS-HYG-03 stays blocked on the true originators.

Group checkpoint after step 2.

## 5. Verification pass

Completed before each member's completion; see the table below.

| Member | Criterion | Status | Evidence |
|---|---|---|---|
