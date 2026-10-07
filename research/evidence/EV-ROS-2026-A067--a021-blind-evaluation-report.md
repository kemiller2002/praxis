---
id: EV-ROS-2026-A067
title: "EX-ROS-2026-A021 blind evaluation report (arm-X / arm-Y)"
status: draft
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-30
updated: 2026-09-30
research_area: repository-operating-system
evidence_type: primary
supports: [EX-ROS-2026-A021]
related_documents:
  - EX-ROS-2026-A021
  - EV-ROS-2026-A065
tags: [planning, grouping, experiment, evaluation]
confidence: medium
provenance:
  contributions:
    EXE-20260930T230548914Z-bb6a60fd:
      operations: [migrated]
      at: 2026-09-30T23:06:18.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-08: renumbered from EV-ROS-2026-A060..A062 on experiment/a021-evaluation-kit, which collided with main's EV-ROS-2026-A060; content unchanged apart from the IDs"
    EXE-20261007T185521101Z-d60238bc:
      operations: [origin-unrecorded]
      at: 2026-10-07T19:02:05.523Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Written by a blinded evaluator session that had no Praxis execution (originally EV-ROS-2026-A062, commit f3bad01c); its creation was never recorded (DF-ROS-2026-A055)"
---

# EX-ROS-2026-A021 blind evaluation report (arm-X / arm-Y)

## 1. Summary

- Both arms build cleanly (0 warnings, 0 errors) and their full test suites pass. arm-X: 811/811, with 19 `work group` tests. arm-Y: 829/829, with 29 `work group` tests and 8 `group checkpoint` tests.
- I ran the same CLI fixture against both arms. Almost every acceptance criterion of PRAXIS-GROUP-01..05 is met in both, and I observed the behaviour directly. The exceptions are the "blocked member / who it gates" rows of 02, which I checked only through each arm's own tests.
- **arm-Y has a data-loss defect.** create, add and remove read the store, change it and write it back with no lock. I ran 8 concurrent creates: all 8 exited 0 and only 2 groups were stored. I ran 4 concurrent adds: all 4 exited 0 and only 1 member and 1 provenance entry were stored. arm-X serialises the same writes under a lock and never lost a write that reported success.
- **Store model differs.** arm-X keeps declarations, history and group checkpoints in one versioned file with one rule set. arm-Y uses three stores: `groups.json`, `group-membership.json` and group checkpoint events in `events.jsonl`. It also has four rejection types, two JSON envelope conventions, two group-ID rules and exit codes that differ between commands.
- **Checkpoints differ.** arm-Y's group checkpoint is the more rigorous: it needs an active member running under the caller's own execution, writes a content-addressed event, warns when a member's own checkpoint is behind, and has a history view. arm-X records a checkpoint with no active member and no execution, but it supports `--dry-run`, which arm-Y does not.
- **Reuse differs.** arm-Y reuses the planner's JSON parser and its execution-location rule. arm-X re-implements both, and its version omits the planner's description-based inference.
- **Size.** arm-X added 2,279 lines in 5 patches with little rework. arm-Y added 4,048 lines in 6 patches, with visible rework in items 04 and 05. arm-Y's `docs/planning.md` is stale: it calls PRAXIS-GROUP-05 deferred.
- Blinding was compromised: text in the bundle names the arms (section 3).

## 2. Method

- Kit: `research/experiments/EX-ROS-2026-A021-evaluation-kit/prepare-blind-bundle.sh "$HOME/a021-eval"`, run from branch `experiment/a021-evaluation-kit`. After that I used only `$EVAL/bundle/` and `$EVAL/output/`. I did not open `$EVAL/sealed/`.
- Toolchain: `apt-get install dotnet-sdk-10.0` (SDK 10.0.112). Build and test commands exactly as the brief gives them (`-p:FSharpCoreImplicitPackageVersion=10.1.400`, then `dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll`) in each `tree/`. Logs: `output/build-arm-{X,Y}.log`, `output/test-arm-{X,Y}.log`.
- Fixture (`output/fixture/run-fixture.sh ARM`, the same script for both arms):
  - Setup:
    - a fresh `git init` with a bare `origin`, `ros init`, and `requireDurableCheckpoint: true`;
    - branch `feature/x`, pushed with an upstream;
    - items ITEM-1..5, DONE-1 (completed), GONE-1 (abandoned) and OTHER-1;
    - ITEM-3's description says "Depends on: ITEM-2"; ITEM-4 stays captured;
    - everything committed and pushed.
  - Identity is set through the `ROS_ACTOR*` / `ROS_TELEMETRY_*` environment variables.
  - Before and after every mutating step I hash `.ros/work/queue.json`, `.ros/context/current.json`, `.ros/events/events.jsonl`, `.ros/work/groups.json` and `.ros/work/group-membership.json`, and run `git status --porcelain`.
  - The only difference between arms is the group-ID flag each arm's usage text documents: `--group` for arm-X, `--id` for arm-Y.
  - Steps S0–S34 are labelled in `output/fixture/fixture-arm-{X,Y}.log`.
- Further scripts, applied the same way to both arms:
  - `run-extra.sh`: the correct `executionRepositories` object form, cross-repository add, concurrent create and add, `--json` on argument errors, and `checkpoint show`;
  - `run-extra2.sh`: text-mode group checkpoint, `validate` with an unknown member injected by hand, and a malformed group ID given to checkpoint;
  - `run-extra3.sh`: the planner's member list before and after add and remove.
- I read every arm source file named in `diffstat.txt` and read the patches in order to judge churn.
- Could not do: diagnose why arm-X's lock contention surfaces as an "unexpected operational failure" (see 7.X-3); test Windows or macOS; test the CLI with a blocked member (covered only by the arms' own tests).

## 3. Blinding

- Mapping commitment: `6ca091e3c3e18432296a7700cbf3a6e0ab3f224acbc3d5afccf070c71988aaa6`
- Last line of `arm-X/diffstat.txt`: ` 16 files changed, 2279 insertions(+), 7 deletions(-)`
- Last line of `arm-Y/diffstat.txt`: ` 29 files changed, 4048 insertions(+), 34 deletions(-)`
- **Blinding was compromised, and I believe I know the mapping.** I did not look for it; the scrubber missed two strings:
  - `arm-X/commit-subjects.txt` line 17 reads "EX-ROS-2026-A021 grouped-arm session metrics". The scrub rule `\b(control|grouped) arm\b` needs a space, so "grouped-arm" passes through.
  - `arm-Y/tree/docs/planning.md:356` reads "on the unmerged `EX-ROS-2026-A021` control branch". "control branch" matches none of the scrub patterns.
  - arm-Y's history also contains a merge commit, and patch 0006 says items 01..04 "landed on this branch while this item was in progress". That is consistent with items developed separately.
- From these I infer that arm-X is the grouped arm and arm-Y is the control arm. I saw these strings early (Step 1–2), before reading any code.
- I applied every check to both arms through the same scripts. Readers should still treat the qualitative judgements in section 6 as possibly influenced.
- Kit fix: widen the scrub to `(control|grouped)[- ](arm|branch)`, and scrub commit bodies and docs for "control"/"grouped" near "arm" or "branch".

## 4. Build and tests

| | arm-X | arm-Y |
|---|---|---|
| Build (Release, sdk 10.0.112) | success, 0 warnings, 0 errors | success, 0 warnings, 0 errors |
| Test total / passed / failed | 811 / 811 / 0 | 829 / 829 / 0 |
| Failing tests | none | none |
| Group-command tests added | 19 (`work group:` prefix, `tests/Ros.Tests/WorkGroupTests.fs`, 515 lines) | 29 `work group:` + 8 `group checkpoint:` = 37 (`WorkGroupTests.fs` 666 lines, `GroupCheckpointTests.fs` 260 lines) |
| Fixture runs of the arm's own CLI | all steps ran | all steps ran |

## 5. Acceptance criteria

Evidence `S#`, `E#`, `F#` refers to steps in `output/fixture/*-arm-{X,Y}.log`.

### PRAXIS-GROUP-01 `work group create`

| Criterion | arm-X | arm-Y |
|---|---|---|
| Records ID, members, kind, execution repository, cross-repository flag, shared context | met: S7 `groups.json` has all fields (`WorkGroups.fs:354-383`) | met: S7 (`GroupDeclaration.fs:44-47`, stored with `PlanningJson.declaredGroupNode`) |
| Refuses unknown or terminal members | met: S2 exit 1, codes `terminal-member` ×2 and `unknown-member` | met: S2 exit 1, all three reasons (JSON rejections are plain strings with no codes) |
| Refuses duplicate IDs | met: S8 exit 1 `duplicate-group` | met: S8 exit 1 |
| Never changes a member's lifecycle state | met: S7 hashes of queue, current and events unchanged; `git status` shows only `groups.json` | met: same observation |
| Planner reads the stored declaration exactly as `grouping.groups` | met: S9, extra3 (`plan groups` members identical); test "cli the planner reads a stored group exactly as the same declaration in grouping.groups" passed. A configured ID shadows the stored one (`WorkGroups.fs:433-435`, S33 exit 0) | met: S9, extra3; parsed by the planner's own parser (`WorkGroupJson.fs:58` calls `PlanningJson.parseDeclaredGroup`). An ID in both places makes the planner fail (S33 exit 1, `GroupDeclaration.fs:162-174`) |
| `--dry-run` | met: S1 exit 0, `status: dry-run`, nothing written | met: S1 exit 0, `status: planned`, nothing written |
| `--json` | met: S1, S2, S8 | met: S1, S2, S8 |
| `validate` checks stored groups | met: F2, an injected unknown member makes `validate` exit 1 | met: F2, same result |

### PRAXIS-GROUP-02 `work group show`

| Criterion | arm-X | arm-Y |
|---|---|---|
| Members with their own recorded and planning states | met: S11 | met: S11 |
| Partial-completion progress | met: S24 "1 of 3 complete", `complete: false` | met: S24 "1 of 3 complete", `allComplete: false` |
| Blocked members and who they gate | met (tests only): "cli show reports … blocked members and who they gate" passed; code at `WorkGroups.fs:478-509` | met (tests only): "show: a blocked member names its reasons and the members and other items it gates" passed; also lists non-members it gates (`WorkGroupCommands.fs:405-414`) |
| Execution repository, architecture notes | met: S11 | met: S11 |
| Text and `--json` | met: S11, S12 | met: S11, S12 |
| Unknown group exits 1 | met: S13 exit 1 in text and JSON; no ID gives exit 2 | met: S13, same |
| Never writes | met: S11–S13 hashes equal | met: S11–S13 hashes equal |

### PRAXIS-GROUP-03 `work group add`

| Criterion | arm-X | arm-Y |
|---|---|---|
| Refuses unknown or terminal items | met: S14 exit 1 | met: S14 exit 1 |
| Refuses items already present | met: S14 `already-member` | met: S14 |
| Refuses an item whose execution repository differs, unless the group is cross-repository | met: E1 exit 1 `repository-mismatch`; E2 accepted into a `--cross-repository` group | met: E1, E2 |
| Records who added it | met: S16, a `history` entry `member-added` with actor and reason in `groups.json` | met: S16, a ledger entry `addedBy` in `group-membership.json` |
| Member lifecycle untouched | met: S16 hashes unchanged | met: S16 hashes unchanged |
| (brief) Planner reads the new member | met: extra3 | met: extra3 |

### PRAXIS-GROUP-04 `work group remove`

| Criterion | arm-X | arm-Y |
|---|---|---|
| Refuses non-members | met: S18 exit 1 `not-member` | met: S18 exit 1 |
| Never changes the item's lifecycle, evidence or attribution | met: S20 queue, current and events unchanged | met: S20 unchanged |
| Removing the last member is refused or explicit | met: S22 refused (`last-member`) unless `--allow-empty`, which is recorded in history (`WorkGroups.fs:414-428`) | met: S22 always refused (`GroupMembership.fs:194-198`) |
| Provenance recorded | met: history entry `member-removed` | met: ledger entry `removedBy` |
| (brief) Planner after remove | met: extra3 | met: extra3 |
| (brief) A completed member may leave | met: S25 dry-run accepted | met: S25 dry-run accepted |

### PRAXIS-GROUP-05 `work group checkpoint`

| Criterion | arm-X | arm-Y |
|---|---|---|
| Records group ID, active, completed and remaining members, shared decisions, branch and commit, next action | met: S31 and F1 (also blocked and abandoned) | met: S31 and F1 (blocked members are counted as remaining, `GroupCheckpoint.fs:34-41`) |
| Same durable-checkpoint verification as `work checkpoint` | met for the Git rule: S28 refused `uncommitted-changes`, S29 refused `local-ahead` (`Checkpoint.fs` `verifyLocation`). It does **not** require an active member or execution: S26 recorded with none active | met: S28 and S29 refused the same way, and it also needs an active member under the caller's own execution (S26 refused `no-active-member`; `GroupCheckpoint.fs:285-292`) |
| References members' own checkpoints and never replaces them | met: F1 "member checkpoint: ITEM-2 …"; `current.json` byte-identical (S31) | met: S31 `members[].checkpoint {id, commit, executionId}`; `current.json` byte-identical; warns when the member's own checkpoint is behind (F1) |
| No member claims another's changes (PRX-GRP-043) | met: records no paths and no execution (`WorkGroups.fs:52-69`) | met: `paths: []` enforced on re-validation (`GroupCheckpoint.fs:433-434`); recorded under the caller's own executions only |
| (brief) `--dry-run` | met: S30, nothing written | **not supported**: S30 exit 2 "unexpected argument '--dry-run'" (not part of the 05 acceptance text) |

## 6. Cross-item architecture

**Store model.**
- arm-X: one file, `.ros/work/groups.json`, with `schemaVersion: 1`, holding each group's declaration, append-only `history` and `checkpoints` (`WorkGroupJson.fs:95-104`, `:343-356`). Files written before item 05 still load, because a missing `checkpoints` field reads as empty (I removed the field and `show` still exited 0).
- arm-Y: three stores:
  - `groups.json` (`praxis.work-groups/1.0.0`);
  - `group-membership.json` (ledger, `schemaVersion 1.0.0`, `WorkGroupMembershipJson.fs:56`), written first, then `groups.json`, with a manual restore if the second write fails (`FileWorkGroupRepository.fs:96-106`);
  - group checkpoints as `work.group.checkpointed` events in `.ros/events/events.jsonl`, written through the work-state journal (`FileGroupCheckpointRepository.fs:109-140`).
- Difference: arm-X is simpler, and one lock covers everything. arm-Y reuses the event log for checkpoints, which fits the existing checkpoint model, but it spreads one group's state across three files.

**Validation.**
- arm-X: one join rule, `WorkGroups.eligibility` (`WorkGroups.fs:327-343`), used by both create and add. One rejection type, `GroupRejection`, with codes (`:114-168`), which the checkpoint type wraps.
- arm-Y: near-copies of the same rule.
  - Create uses `GroupDeclaration.decide` (`GroupDeclaration.fs:101-124`) and add uses `GroupMembership.itemChecks` (`GroupMembership.fs:140-149`), with different rejection types and different wording ("cannot join a new group" versus "cannot join a group").
  - Only add checks the execution repository; create has no such check and does not accept `--config` (S6 exit 2).
  - There are four rejection types: declaration, addition, removal and checkpoint.
  - Checkpoint uses a different group-ID pattern: `^[A-Za-z0-9][A-Za-z0-9._-]*$` (`GroupCheckpoint.fs:239-242`) versus `GROUP-…` (`GroupDeclaration.fs:51-53`).
- arm-X is clearly more uniform on this dimension.

**Output contracts.**
- arm-X: every command emits `{command, schemaVersion: 1, status, …}` (`WorkGroupCommands.fs:141-142`), and rejections are `{code, message}`. Exit codes follow one rule: 2 for argument-shaped refusals, 1 for state refusals (`:168`, `:405`).
- arm-Y:
  - create, add, remove and show emit `{schema: "praxis.work-group/1.0.0", kind: "work-group-*", status, lifecycleChanged}`, while checkpoint emits `{command, schemaVersion: 1, …}` (`GroupCheckpointCommands.fs:123-129`).
  - Create's JSON rejections are plain strings; add, remove and checkpoint use `{code, message}`.
  - Create exits 1 for argument-shaped refusals (invalid ID, no members, repeated member: S3–S5), while checkpoint exits 2 for an invalid or undeclared group (F3).
  - add and remove exit 1 for an undeclared group, but checkpoint exits 2 (F3).
  - Status words differ by command: `created`, `planned`, `added`, `removed`, `recorded`.
- arm-X is more consistent here.

**Checkpoint shape (PRAXIS-GROUP-05 versus PRX-GRP-044).**
- Both record the group ID, member standings, decisions, summary, next action, the branch and commit with remote verification, and references to members' own checkpoints. Neither touches a member's checkpoint history (S31 `current.json` byte-identical).
- arm-X references only members that have a live checkpoint (`WorkGroupCommands.fs:323-337`). It has no execution or identity requirement, so any actor can record a group checkpoint for work that is not running (S26).
- arm-Y:
  - records per-member standing, state, execution and checkpoint reference, plus the declaration source;
  - records the membership as it was at the time, so a later remove does not rewrite history;
  - content-addresses the event;
  - requires the caller's own execution;
  - warns when a member's own checkpoint is behind;
  - has `work group checkpoint show` and validates stored events.
- No attribution laundering in either: neither records paths.
- arm-Y is more rigorous on attribution and durability. arm-X is lighter and supports `--dry-run`.

**Reuse versus duplication.**
- arm-X:
  - re-implements the `DeclaredGroup` JSON reader (`WorkGroupJson.fs:204-225`) instead of reusing `PlanningJson`;
  - re-implements execution location from `grouping.executionRepositories` only (`WorkGroupCommands.fs:110-122`). The planner's `evidenceFor` also infers `UnknownExternal` from the description (`Grouping.fs:540-544`), so add can accept an item the planner would place in an external repository;
  - shares one argument parser and one mutate/render pipeline across the four mutating verbs (`WorkGroupCommands.fs:59-73`, `:174-209`).
- arm-Y:
  - reuses `PlanningJson.parseDeclaredGroup` and `declaredGroupNode`, having exposed them in patch 0001;
  - reuses `Grouping.executionLocation`, extracted from the planner in patch 0003;
  - has three near-identical request builders and three renderers in `WorkGroupCommands.fs:122-319`;
  - uses a third, independent argument-parsing style in `GroupCheckpointCommands.fs:31-85`, built on `List.pairwise`.
- Both define a `MemberStanding` type of their own.
- arm-Y reuses more of the planner. arm-X reuses more within the feature.

**Compatibility.**
- Both change the planner's read path so stored groups join the configured ones (arm-X `FilePlanningRepository.fs:272-277`, arm-Y `:277`). In both, a malformed `groups.json` therefore makes planner reads fail.
- The behaviours differ when an ID is both configured and stored: arm-X lets the configured declaration shadow the stored one silently, while arm-Y refuses and the planner command exits 1 (S33).
- arm-X changes one private function signature in `Checkpoint.fs` (`gitLocation`) and adds `verifyLocation`. arm-Y changes `Checkpoint.fs` by 30 lines and `PlanningJson.fs` by 48.
- No migration is needed for existing repositories in either arm.

**Design revisions and churn (patches in order).**
- arm-X: 1186 / 338 / 161 / 111 / 500 lines added and 3 / 5 / 1 / 2 / 13 removed across its 5 patches. The history schema already included `member-added` and `member-removed` in patch 0001, so later items only added code; there is almost no rework.
- arm-Y: 858 / 454 / 796 / 587 / 1416 / 184 lines added and 18 / 12 / 34 / 83 / 11 / 123 removed across its 6 patches.
  - Patch 0004 reworked patch 0003's additions-only ledger into a `MembershipChange` union.
  - Patch 0006 reworked item 05 after merging items 01–04: `--member` and ad-hoc membership were removed, and `--group` became `--id`.
- Totals: arm-X +2279 / −7; arm-Y +4048 / −34.

**Maintainability and technical debt.**
- Both are functional F# in the style of the surrounding code: pure domain decisions and `Result` pipelines.
- arm-X: `WorkGroupCommands.checkpoint` is long (`:347-437`), and `WorkGroups` sits in `Ros.Domain.Work` while its types come from Planning. Docs are updated (`docs/cli.md` +72 lines, `docs/planning.md`).
- arm-Y: GroupCheckpoint is well documented, but the commands are spread over two CLI modules. The docs have a stale statement: `docs/planning.md:356` still says PRAXIS-GROUP-05 is "deferred". There is a text-output bug (7.Y-2).

**Cross-item consequences.**
- arm-X: choosing one store and one lock in item 01 let items 03–05 each add a small function (`WorkGroups.fs:393-428`), with the same concurrency safety and the same output envelope.
- arm-Y:
  - Item 01's lock-free store pattern was copied into items 03 and 04, so the lost-update defect affects all three mutating commands.
  - Item 03 added a second store, which item 04 had to generalise.
  - Item 05 was built alongside items 01–04 and then reconciled (patch 0006). The different envelope, group-ID rule and parser remained.

## 7. Defects and risks

**arm-X**
- X-1 (inconsistency): the execution-repository rule ignores the planner's description-based external inference (`WorkGroupCommands.fs:110-122` versus `Grouping.fs:540-544`). An item whose description names an external repository can join a local group.
- X-2 (risk): a group checkpoint is accepted with no active member and without any execution check, so any actor can record one (S26 exit 0).
- X-3 (observation): under contention the lock sometimes surfaces as "ERROR Praxis encountered an unexpected operational failure" (E4: 3 of 8 concurrent creates exited 1; the first run had 1 of 8). No state was lost: 5 stored matched 5 exit-0 runs, and 7 matched 7. The lock module predates the arms (it is not in `diffstat.txt`). The root cause is **unknown**.
- X-4 (debt): the `DeclaredGroup` JSON reader duplicates `PlanningJson` (`WorkGroupJson.fs:204-225`). A configured group ID silently shadows a stored one (S33).

**arm-Y**
- Y-1 (**defect, data loss**): create, add and remove use read-modify-write with no lock (`FileWorkGroupRepository.fs:57-70`, `:111-130`, `:137-154`). Reproduction: run `run-extra.sh`.
  - E4: 8 concurrent `work group create` runs all exit 0, but only 2 groups are stored.
  - E5: 4 concurrent `work group add` runs to one group all exit 0, but the group lists only ITEM-5 and ITEM-2, and the ledger has one addition.
  - `validate` then passes (E6), so the loss is not detected.
- Y-2 (defect, output): text-mode `work group checkpoint` prints the summary under the label "completed:" (`GroupCheckpointCommands.fs:156`). F1 shows `completed:     SUMMARY-TEXT` directly after `completed:     ITEM-1`.
- Y-3 (inconsistency): create has no execution-repository check and no `--config` option, while add has both (`GroupDeclaration.fs:101-124`, `GroupMembership.fs:132-149`, S6).
- Y-4 (inconsistency): the envelope (`schema`/`kind` versus `command`/`schemaVersion`), the rejection shape (strings versus `{code, message}`), exit codes (create: argument errors exit 1; checkpoint: undeclared group exits 2, while add/remove exit 1) and the group-ID pattern (`GroupCheckpoint.fs:239-242` versus `GroupDeclaration.fs:51-53`) all differ between commands.
- Y-5 (risk): an ID declared both in configuration and in the store makes planner reads fail (S33 exit 1), and create does not check configured IDs.
- Y-6 (debt): `docs/planning.md:356` is stale ("the remaining command is PRAXIS-GROUP-05, deferred"). `work group checkpoint` has no `--dry-run`.

## 8. Unknowns and limitations

- The cause of arm-X's lock-contention error (X-3) is unknown.
- I exercised blocked-member rendering only through each arm's own tests.
- I did not check arm-Y's behaviour with a planner configuration file stored in the repository (create cannot see configured IDs; see Y-5).
- Performance with large stores and cross-platform file-locking behaviour are unknown.
- I did not assess `.ros/` contents, as the brief instructs.
- Blinding was compromised (section 3), which may bias the qualitative judgements.
- Persisting the result: `./ros validate` on the evaluation-kit branch reports one error for this file. It was created after the provenance policy date but records no provenance. `./ros provenance record` refuses unless the caller has its own active work execution. Starting one would change `.ros/` state, which the brief forbids committing, and using an existing execution would misattribute authorship. I left the error unresolved rather than fabricate provenance. The owner can record provenance inside a real execution.
