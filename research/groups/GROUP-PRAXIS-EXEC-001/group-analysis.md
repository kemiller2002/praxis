# Group analysis: GROUP-PRAXIS-EXEC-001

- Group: `GROUP-PRAXIS-EXEC-001` (`./praxis work group show GROUP-PRAXIS-EXEC-001`)
- Members: `PRAXIS-EXEC-02`, `PRAXIS-EXEC-04`, `PRAXIS-EXEC-03`, `PRAXIS-EXEC-01`, `PRAXIS-EXEC-05`,
  `PRAXIS-EXEC-06`, `PRAXIS-EXEC-07` (`PRAXIS-EXEC-08` was abandoned as a duplicate of `PRAXIS-FND-01`/`PRAXIS-FND-02`, the shared-foundations track's items for consuming the Ordo execution contract; it needed an Ordo execution-contract
  package that does not exist yet)
- Execution repository: `kemiller2002/praxis`; every member executes here
- Base commit: `f242aae664b4` (main after PR #177)
- Source: requirements audit of 2026-10-06, gaps 4, 5 and 13 and two documentation discrepancies
  (`docs/execution-runtime.md`, `requirements/EXECUTION-ORCHESTRATION.md`).
- The machine-checkable `praxis.group-analysis/1` form is defined by `PRAXIS-GROUP-10`, which is not on
  main at the base commit; this Markdown form is the record until it is.

## 1. Members

Acceptance criteria are each member's own description (`./praxis work show ID`), enumerated there as
(1), (2), ... and verified one by one in section 5.

| Member | Obligation | Requirements | Depends on |
|---|---|---|---|
| PRAXIS-EXEC-02 | Governed evaluation runner; attributed ledger entries; every `execution` mutation legal-action gated | PRX-VER-001, 002; PRX-EXEC-024; PRX-REC-007 | - |
| PRAXIS-EXEC-04 | Containment profile and host enforcement evidence | PRX-SEC-001, 003, 010, 011, 013, 014 | PRAXIS-EXEC-02 (envelope shape) |
| PRAXIS-EXEC-03 | Role launchers and repository execution policy | PRX-EXEC-005, 010, 040, 041, 042; PRX-UI-025 | PRAXIS-EXEC-04 (launch checks required restrictions) |
| PRAXIS-EXEC-01 | Bind envelopes to work transitions, remote and fallback; divergence on resume; receipts at completion | PRX-EXEC-014, 026, 030, 041, 053, 055 | PRAXIS-EXEC-02 |
| PRAXIS-EXEC-05 | `/api/executions` control plane in `web serve` | PRX-CTL-001, 003, 005, 006, 011, 012; PRX-UI-008, 009 | PRAXIS-EXEC-01 (work-bound executions to list) |
| PRAXIS-EXEC-06 | Operator UI execution views, reasons and human-required marking | PRX-UI-001, 004, 007, 020, 021, 026, 027 | PRAXIS-EXEC-05 |
| PRAXIS-EXEC-07 | Accurate requirement status per row and runtime documentation; tests for untested rows | EXECUTION-ORCHESTRATION status; PRX-EXEC-033, 043, 044, 045, 054 | all |

## 2. Reuse inventory

Established by searching (`grep -rn "LegalActions\|ExecutionStore\|GitWorkspace\|runSelf\|readExecutor" src`),
not from memory.

| Existing element | Location | What it does | Disposition |
|---|---|---|---|
| `ExecutionEnvelope`, `ExecutionEnvelope.create` | `src/Praxis.Domain/Execution/Governance.fs:655` | the envelope and its admission rules | **extended** (origin, containment profile, evaluator command); one envelope type for explicit, work-bound, remote and fallback executions |
| `LegalActions.compute` | `src/Praxis.Domain/Execution/Governance.fs:863` | the single legal-action computation | **extended** with evaluate, launch, rebind and divergence; the old signature stays as a wrapper so no caller computes legality differently |
| `Containment` | `src/Praxis.Domain/Execution/Governance.fs:593` | unknown / semantic-only / host-enforced | **extended** by a per-restriction profile; `HostEnforced` is derived from it, never asserted |
| `StepLedger`, `Receipt.compare` | `src/Praxis.Domain/Execution/Governance.fs:756` | receipts and resume | **reused** unchanged; attribution is written beside entries, not into the union |
| `EvaluationOutcome.judge` | `src/Praxis.Domain/Execution/Governance.fs:275` | evaluator-changed / unavailable / pass / fail | **reused**; a typed verification record wraps it |
| `ExecutionStore`, `GitWorkspace` | `src/Praxis.Infrastructure/Execution/ExecutionStore.fs:39`, `:121` | `.ros/executions` persistence and Git observation | **extended** (branch, ancestry, commit range, attribution lines); becomes the port implementation |
| `ExecutionJson` | `src/Praxis.Contracts/Execution/ExecutionJson.fs:209` | `ordo.execution/1` wire shapes | **extended** additively; legacy envelopes still read |
| `ExecutionCommands` | `src/Praxis.Cli/ExecutionCommands.fs` | CLI and all execution behaviour | **split**: behaviour moves to a new Application service behind ports (the CLI ratchet forbids growth); the CLI keeps parsing and rendering |
| `FileProvenanceRepository.readExecutions` | `src/Praxis.Infrastructure/Provenance/FileProvenanceRepository.fs:175` | telemetry executions with actor and status | **reused** to find the execution `work begin` created, so the envelope shares its ID |
| `RemoteIdentity.readExecutor` | `src/Praxis.Domain/Remote/Identity.fs:152` | detects a remote child process | **reused** to record origin `remote` |
| `PathFilter.alwaysIgnoredPatterns` | `src/Praxis.Domain/Work/PathFilter.fs:67` | tool bookkeeping never needs attribution | **extended** with `.ros/executions/**` so binding never creates unattributed changes in repositories whose `ignoredPaths` predate it |
| `runWorkStart` and the work dispatch | `src/Praxis.Cli/Program.fs:1276`, `:2992` | `work begin` and the other transitions | **reused**: binding is one call at the success exit of `runWorkStart` (so `plan execute-group`, which calls it in-process, binds too) and a wrapper on the other dispatch lines |
| `WebInterface.route` / `execute` / `CliProcess.runSelf` | `src/Praxis.Cli/WebInterface.fs:169`, `:700`; `src/Praxis.Cli/CliProcess.fs:87` | stateless host that shells out to the CLI | **reused**: execution routes run `praxis execution ... --json`; no second rule set |
| Completion readiness gate | `src/Praxis.Application/Work/CompletionReadiness.fs` | evidence facets at `work complete` | **not reused**: it judges supplied quality evidence under opt-in policy; receipt state of a bound execution is governed state, always on. Kept separate and minimal to avoid colliding with PRAXIS-GROUP-10 |

New abstractions and why nothing existing fits:

- `ExecutionPorts` (Application): no Application module covers executions (`grep -rn Execution src/Praxis.Application` finds only telemetry). Needed so behaviour leaves the ratcheted CLI.
- `ContainmentProfile`: `Containment.HostEnforced` holds a flat restriction list with no per-restriction status; SEC-003/011 need enforced / unavailable / unrestricted / unknown per dimension.
- `ExecutionPolicy` (`ros.json` `execution`): no execution section exists in `ros.json` or `schemas/`; launchers, worktree requirement and required restrictions are new policy.
- `ExecutionOrigin`: envelopes had one origin (`execution start`); work-bound ones must not treat an undeclared boundary as "everything is outside".

## 3. Group-level design

- One Application service (`Praxis.Application.Execution`) owns every execution use case: start, show, list, actions, step, evaluate, expand-scope, resolve-effect, transition, rebind, launch, containment, cleanup, and work-transition binding. The CLI, the work transitions, remote execution (a child `praxis work ...` process) and the web host all reach it; the web host only through the CLI.
- Legality comes only from `LegalActions`. Work transitions keep their own kernel; the envelope mirrors their outcome, and the receipt pre-check at `work complete` reads the same blockers `execution.complete` uses (receipts, unknown effects, verification), not the uncommitted-workspace rule, which the durable-checkpoint protocol already owns.
- Work-bound envelopes bind the current checkout and never create a branch or worktree (PRX-GRP-117: `execute-group` never creates a branch). Their boundary is undeclared, so no scope effects are computed for them.
- Compatibility: envelope JSON gains fields additively; missing fields read as origin `explicit`, an all-unknown profile and no evaluator command (which makes `execution evaluate` unavailable until one is declared, by design of VER-001).
- Common tests: domain tests in `ExecutionGovernanceTests`, CLI tests over temporary Git repositories, web route tests in `WebInterfaceTests`.
- Risk of solving members independently: each would grow `ExecutionCommands.fs` past the ratchet and compute legality in a second place.
- Shared infrastructure (the Application service and ports) is attributed to `PRAXIS-EXEC-02`, the first member that needs it.

## 4. Order

1. `PRAXIS-EXEC-02` with the service extraction, then `PRAXIS-EXEC-04` and `PRAXIS-EXEC-03` (same PR: they extend the same envelope and legal actions). Group checkpoint after it merges.
2. `PRAXIS-EXEC-01` (binding). Group checkpoint.
3. `PRAXIS-EXEC-05` and `PRAXIS-EXEC-06` (web). Group checkpoint.
4. `PRAXIS-EXEC-07` (status table and docs, after the implementation is final).

## 5. Verification pass (before completing any member)

Recorded per member as `research/groups/GROUP-PRAXIS-EXEC-001/verification-<MEMBER>.md` before
that member's `work complete`.
