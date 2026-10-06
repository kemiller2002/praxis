# Group analysis: GROUP-PRAXIS-GROUPING-001

- Group: `GROUP-PRAXIS-GROUPING-001` (`./praxis work group show GROUP-PRAXIS-GROUPING-001`;
  planner view at start: [`explain-group-at-start.json`](explain-group-at-start.json))
- Members: `PRAXIS-GROUP-08`, `PRAXIS-GROUP-07`, `PRAXIS-GROUP-10`, `PRAXIS-GROUP-09`, `PRAXIS-PLAN-10`
  (`PRAXIS-PLAN-11` stays captured: it is a review at a later trigger, not implementation)
- Execution repository: `kemiller2002/praxis` (Praxis id `repository-operating-system`); every member executes here
- Base commit: `f242aae664b4` (main after PR #177, `DF-ROS-2026-A053`)
- Machine-checkable form: [`group-analysis.json`](group-analysis.json) (`praxis.group-analysis/1`, defined by
  `PRAXIS-GROUP-10`). This analysis was written and committed before any code of any member.
- Planner: affinity **high**, confidence high; 5/5 declared together, 5/5 same area `grouping`,
  5/5 cite `DF-ROS-2026-A053` and `PRX-GRP-190`; collision risk elevated, not parallel-safe;
  recommended one sequential agent.

## 1. Members

Acceptance criteria are taken from each member's description and the requirement IDs it names in
`requirements/PLANNING-WORK-GROUPS.md`; they are enumerated identically in the JSON form and verified
per member in `group-verification-<MEMBER>.json`.

| Member | Obligation | Acceptance criteria | Depends on |
|---|---|---|---|
| PRAXIS-GROUP-08 | Group command surface (PRX-GRP-110..116) | see JSON `members[0]` (9 criteria: list; envelopes and round-trips; store v2 with v1 read; idempotency; history with execution ID; append-only validation; group store only; derived status, removed-open, no completion transition; existing validation kept) | - |
| PRAXIS-GROUP-07 | Cross-repository groups (PRX-GRP-100..109) | JSON `members[1]` (11 criteria) | PRAXIS-GROUP-08 |
| PRAXIS-GROUP-10 | Completion gates (PRX-GRP-133..135) | JSON `members[2]` (6 criteria) | PRAXIS-GROUP-08 |
| PRAXIS-GROUP-09 | `plan execute-group`, grouped by default (PRX-GRP-117, 130..132, 136..137) | JSON `members[3]` (9 criteria) | PRAXIS-GROUP-08, PRAXIS-GROUP-10 |
| PRAXIS-PLAN-10 | Context and cost measurement (PRX-GRP-150..158) | JSON `members[4]` (10 criteria) | PRAXIS-GROUP-09 (GEX parts) |

Open-question defaults recorded in PR #177 and used here: `execute-group` uses only existing
transitions; the reference verb is `work group link`; idempotent repeats return `changed: false`;
`deferredTo` is allowed; the grouped-mode size ceiling is 6; shared cost is allocated
`equal-share` and labelled `allocated`; observations are stale after 1440 minutes; an unreachable
member is recorded `verified: false` with a warning.

## 2. Reuse inventory

Established by search (section "Searches" in the JSON), not memory.

| Existing element | Location | What it does | Disposition |
|---|---|---|---|
| `WorkGroups` decisions (`create`, `add`, `remove`, `eligibility`, `findings`, `progress`, `checkpoint`) | `src/Praxis.Domain/Work/WorkGroups.fs:367` | the one pure group decision module | **extended** (idempotency, derived status, removed-open, references, observations, executions) |
| `WorkGroupJson` store reader/writer | `src/Praxis.Contracts/Work/WorkGroupJson.fs:327` | the one parser of `.ros/work/groups.json` | **extended** to schemaVersion 2 (additive); v1 still read |
| `PlanningJson.parseDeclaredGroup` | `src/Praxis.Contracts/Planning/PlanningJson.fs:723` | declaration parser shared with `grouping.groups` | **reused** unchanged for declarations |
| `FileWorkGroupRepository.transact` | `src/Praxis.Infrastructure/Work/FileWorkGroupRepository.fs:61` | lock, read, decide, atomic write | **reused** by every new mutation (`link`, `execute-group`) |
| `WorkGroupCommands.parse`, `mutate`, `envelope` | `src/Praxis.Cli/WorkGroupCommands.fs:59` | argument parsing and the JSON envelope | **reused**; new verbs live in new CLI files within the ratchet |
| `Grouping.executionLocation` | `src/Praxis.Domain/Planning/Grouping.fs:551` | where an item executes (PRX-GRP-051) | **reused** for locality and for "runnable in this checkout" |
| `Grouping.stronglyConnected` / `Graph.cycles` | `src/Praxis.Domain/Planning/Grouping.fs:688`, `src/Praxis.Domain/Planning/Graph.fs:41` | cycle detection | **extended**: `stronglyConnected` exposed so cross-repository edges use the same algorithm |
| `MemberStanding.lookup` / `QueuePresentation.effectiveStatus` | `src/Praxis.Domain/Work/WorkGroups.fs:96` | a member's recorded lifecycle state | **reused** for local members and, over observed files, for remote members |
| `FilePlanningRepository.readQueue` / `readLive` | `src/Praxis.Infrastructure/Planning/FilePlanningRepository.fs:62` | read the queue and live context | **extended** with content-based variants so an observed Git ref is parsed by the same code |
| `FilePlanningRepository.readRepositoryObservations` | `src/Praxis.Infrastructure/Planning/FilePlanningRepository.fs:208` | merged PRs, tags, merged checkpoint commits | **reused** pattern (`ReleaseExists`, `CommitMerged`) for `released`/`merged` milestones |
| `CheckpointVerification.verifyLocation` | `src/Praxis.Domain/Work/Checkpoint.fs:435` | durable Git checkpoint rule | **reused** for group checkpoints (unchanged) |
| `FileCheckpointRepository.resolveExecution` | `src/Praxis.Infrastructure/Work/FileCheckpointRepository.fs:227` | the caller's own execution | **extended** with an any-item variant for history `executionId` and GEX ownership |
| Completion readiness facets and gate (`DF-ROS-2026-A052`) | `src/Praxis.Domain/Work/CompletionReadiness.fs:29`, `src/Praxis.Application/Work/CompletionReadiness.fs:112`, `src/Praxis.Infrastructure/Work/FileCompletionReadiness.fs:58` | evidence facets, refusal exit 3, refusal document | **extended** with a `group-verified` facet; no second gate mechanism |
| `QualityEvidenceJson` decoders and readiness record | `src/Praxis.Contracts/Work/QualityEvidenceJson.fs:429` | decode evidence documents, render readiness | **extended** with the two group documents |
| `runWorkStart` (`work begin`) | `src/Praxis.Cli/Program.fs:1276` | the existing begin transition | **reused** by `plan execute-group`, called in-process, never reimplemented |
| Session transcript adapter metrics | `src/Praxis.Domain/Telemetry/SessionTranscript.fs:281` | repeated reads, compactions, first code change | **extended** with distinct re-read files, peak tokens, first productive change |
| `History` cost and context-reuse model | `src/Praxis.Domain/Planning/History.fs:285` | sample thresholds (3) and cost basis | **extended** for grouped-versus-independent pricing |
| Metric registry | `telemetry/metrics.json` | provider-neutral metric IDs | **extended** with the new IDs |
| CLI ratchet | `quality/cli-boundary-baseline.json` | CLI may not grow | **respected**: behaviour goes to Domain/Application/Infrastructure |

## 3. Group-level design

- **One store, one parser, one decision module.** Every new datum (home repository, member
  references, observations, group executions, opt-outs) is an additive field of the existing
  group record in `.ros/work/groups.json`, parsed by `WorkGroupJson` and decided by `WorkGroups`.
  Store `schemaVersion` becomes 2; a version-1 store is read as-is and is rewritten only by a
  mutation, which keeps its history entries byte-for-byte equivalent.
- **Shared invariants.** Membership never alters a member (PRX-GRP-002/115); group status is
  derived, never stored (PRX-GRP-103/116); history is append-only (PRX-GRP-113); every decision is
  pure and deterministic (PRX-GRP-075/137); unknown stays unknown, never zero (PRX-GRP-156).
- **Conflicts resolved.** PRX-GRP-113 says each mutation SHOULD also append a `work.group.*` event;
  PRX-GRP-115 says group commands write only the group store, and the existing tests assert the
  event log is untouched. The MUST wins: no event is appended; the group history is the audit trail
  (recorded as a deliberate deviation from a SHOULD).
- **Observation, not copying.** Cross-repository member state is read from a configured local clone's
  fetched ref (`git show REF:.ros/...`), parsed by the same queue/context readers; nothing is
  fetched, written or committed in another repository. Remote-protocol reads are reported
  `unsupported` (unknown with a reason) until a later item adds them.
- **One gate mechanism.** The group gates are a fifth completion-readiness facet (`group-verified`)
  evaluated whenever a completing member belongs to a grouped-mode group execution, independently of
  the opt-in `qualityEvidence` policy, and fail closed through the existing refusal path.
- **Group execution record.** The GEX record (ID, mode, basis, order, members begun, opt-outs,
  fallback, prediction, telemetry) is part of the group record. Its data model lands with
  `PRAXIS-GROUP-10` (the gate needs it) as shared group infrastructure; the command that creates it
  lands with `PRAXIS-GROUP-09`.
- **Compatibility.** No existing JSON field changes meaning. `AlreadyMember`/`NotMember`/
  `DuplicateGroup` repeats of an identical intent now exit 0 with `changed: false` (required change,
  PRX-GRP-114); conflicting requests keep exit 1. Cross-repository groups now require the
  `GROUP-ECHELON-` prefix (PRX-GRP-100); no stored group on main uses `crossRepository: true`.
- **Common tests.** `tests/Praxis.Tests/WorkGroupTests.fs` (domain + real binary), extended per
  member; the lifecycle-hash no-mutation test covers every group command.
- **Risk of solving each member independently.** Five store-format bumps instead of one, a second
  gate mechanism beside A052, a re-implemented member-state reader for remote repositories, and a
  second cycle detector: exactly the duplication `EV-ROS-2026-A064` found.
- **Shared group infrastructure (PRX-GRP-043).** The store schemaVersion 2 envelope is attributed to
  `PRAXIS-GROUP-08`; the GEX data model to `PRAXIS-GROUP-10`; this analysis to `PRAXIS-GROUP-08`
  as shared group infrastructure.

## 4. Order

`PRAXIS-GROUP-08` (store v2, idempotency, list, derived status) -> `PRAXIS-GROUP-07` (cross-repository,
builds on derived status) -> `PRAXIS-GROUP-10` (gates, GEX model) -> `PRAXIS-GROUP-09`
(`execute-group`, default; effective only because the gates exist) -> `PRAXIS-PLAN-10` (GEX telemetry,
apportionment, pricing). One pull request per member, each completing its own member. A group
checkpoint is recorded after each member's pull request is pushed.

## 5. Verification pass

Recorded per member, per criterion, in `group-verification-<MEMBER>.json`
(`praxis.group-verification/1`) before that member completes.
