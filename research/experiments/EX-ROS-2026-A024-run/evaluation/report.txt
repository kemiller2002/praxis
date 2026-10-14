# EX-ROS-2026-A024 blind architectural evaluation

Rubric: `inputs/rubric.txt` (five dimensions, 0/1/2 each, composite 0-10). Scope: the code and tests that implement PRAXIS-GROUP-01..05 and the existing code they reuse or duplicate. Acceptance results in `acceptance/` were read as context only and not re-scored. All three snapshots pass 36/36 criteria, and their builds and tests are green.

| Snapshot | D1 persistence | D2 admission/validation | D3 classification/invariants | D4 error/JSON/CLI | D5 mutation/checkpoint | Composite |
|---|---|---|---|---|---|---|
| K | 2 | 2 | 2 | 2 | 2 | 10 |
| P | 1 | 1 | 1 | 1 | 1 | 5 |
| W | 2 | 2 | 2 | 1 | 1 | 8 |

Machine-readable scores, evidence and divergence points are in `evaluation/scores.json`.

## Method

1. I read every non-test source hunk of each `diff.patch` and checked the line references against each `tree/`.
2. I ran the same grep checks on all three trees:
   - who takes `RegistryLock.acquire "work-protocol"`;
   - where `identityFlags` is defined;
   - where the `groups.json` path is defined;
   - which modules read `queue.json`;
   - where atomic-write helpers live.
3. I built each tree in a scratch copy with `npm run build:fsharp -- -p:FSharpCoreImplicitPackageVersion=10.1.400` (.NET SDK 10.0.112). All three builds exited 0.
4. I ran two identical probes against each build. Scripts and output are in `evaluation/probes/`.
   - `probe.sh` sends the same refusal and success commands with `--json`: show of an unknown group, create with an unknown member plus a terminal one, a valid create, add of an unknown member, remove of a non-member, and add to an unknown group.
   - `lockprobe.sh` holds the `work-protocol` lock with another live process, then runs a mutating `work group create`.

Lock probe result (`evaluation/probes/lock.probe.txt`):

| Snapshot | Exit | Group written |
|---|---|---|
| K | 1 | no |
| P | 1 | no |
| W | 0 (in 0 s) | yes |

The K and P refusals surfaced as a stack overflow from the base `RegistryLock.acquire` retry recursion. That code is the same in all three trees, so it is not attributed to any snapshot.

## Dimension 1: shared persistence/state model

- **K = 2.** One store, one path (`FilePlanningRepository.fs:266`) and one writer (`FileWorkGroupRepository`).
  - The `grouping.groups` reader was extracted (`PlanningJson.fs:702`) and is used for both the configuration (`:766`) and the stored store (`:1401`). The planner merges stored groups through `mergeInto` (`FilePlanningRepository.fs:280`).
  - Member states come through the planner's own `readQueue`/`readLive`.
  - Weakness: provenance is stored as bare actor-id strings, not the structured `Actor`.
- **P = 1.** The store is a separate model.
  - It has its own JSON module with private reader helpers (`WorkGroupJson.fs:122-155`) and its own declared-group parser (`:204`). The shape differs: members are objects, and it adds history.
  - The planner gets it through a translation (`WorkGroups.declared`/`declare`) that also injects execution repositories.
  - `queue.json` is read a second time by hand (`FileWorkGroupRepository.fs:25-28,95-111`).
  - `repositoryName` moved into the group repository, so the planner repository now depends on it (`FilePlanningRepository.fs:181`).
  - Structured `Actor` provenance is a plus.
- **W = 2.** The same reader is extracted and shared (`PlanningJson.fs:702,853,1005`), plus a writer of the same shape (`:716`).
  - The planning port gains `DeclaredGroups`, merged in `PlanningOperations` (`Operations.fs:26,42-43`).
  - The `WorkGroupPort` reads members through `FilePlanningRepository`.
  - Provenance uses the structured `Actor`.

## Dimension 2: shared admission/validation rules

- **K = 2.** `shapeRejections` is shared by `create` and `validate` (`GroupDeclaration.fs:224,241,447`). The two-member minimum holds in create, remove and validate.
  - The planner's location rule was extracted (`Grouping.locate`, `Grouping.fs:537`) and reused by `add`.
  - Divergence: `add` feeds it `PlannerConfiguration.defaults` (`FileWorkGroupRepository.fs:84`), so it ignores a `--config` mapping.
- **P = 1.** One `admit` serves both create and add (`WorkGroups.fs:260`).
  - `validate` restates the rules separately (`:488`).
  - The execution repository is recomputed without the planner's external-description inference (`FileWorkGroupRepository.fs:95-104`), so admission and the planner can disagree.
  - The member-count rule is spread over four sites: create needs one or more, remove can empty a group with `--allow-empty`, validate does not flag empty groups, and checkpoint refuses them.
- **W = 2.** `memberRejections` is shared by create and add (`GroupDeclaration.fs:215,226,286`).
  - `add` uses the planner's own extracted rule (`Grouping.fs:541`) over the planner's own input, including `--config` (`WorkGroups.fs:128-131`).
  - The one-member minimum holds in create, remove and validate (`:336,:449`).

## Dimension 3: shared domain classification and invariants

All three re-map `LiveWorkState` to strings. The base has no shared code function for this, so it is symmetric and does not discriminate.

- **K = 2.** One `GroupMemberStatus` map (`memberStatuses`) feeds create, add, checkpoint, validate and show.
  - Show progress reuses the planner's `MemberStatus` (`GroupView.fs:74-82`). The checkpoint partitions on recorded state (`GroupDeclaration.fs:364`).
  - That makes two progress categorisations, but each reuses an existing model.
- **P = 1.** Terminal, active and remaining are re-derived by string compare in several places (`WorkGroups.fs:35,417-420,564-580`).
  - "Active" means different things in show and in checkpoint.
  - Show progress is a new categorisation, not the planner's `MemberStatus`.
  - Execution location is re-classified (see Dimension 2).
- **W = 2.** One `memberStates` map (`GroupDeclaration.fs:190`) and one typed `GroupMemberPartition.ofState` (`:71`) for checkpoints. Show reuses the planner's `MemberStatus` (`GroupView.fs:51,60`). This is the same two-categorisation pattern as K.

## Dimension 4: error/JSON/CLI behaviour consistency

- **K = 2.** One envelope and one `emit` serve all mutations (`WorkGroupCommands.fs:134,170`), with coded rejections and uniform exits of 0, 1 and 2.
  - Show emits the same envelope on error. The probe confirms identical shapes across commands.
  - `identityFlags` is reused, not copied.
  - Minor: show's error is an uncoded string, and its text duplicates the `UnknownGroup` message.
- **P = 1.** Mutations share one coded document, built by `mutate` (`GroupCommands.fs:144-175`).
  - Show ignores `--json` on errors. The probe returned empty stdout with a text error on stderr (`:416,421`).
  - Show's JSON has no `status` field.
  - `identityFlags` is copied (`:30`).
- **W = 1.** Each outcome has its own JSON renderer and `kind` (`PlanningJson.fs:874,906,930,943,1220`).
  - Create, add and remove refusals carry uncoded `errors: [string]`, confirmed by the probe. `GroupRejection` has no `code` function.
  - Checkpoint refusals are coded, but group-level ones get a placeholder code (`WorkGroupCommands.fs:471`).
  - The unknown-group text is duplicated three times.
  - `identityFlags` is copied (`:32`).

## Dimension 5: shared mutation/checkpoint architecture and reuse

All three add one more private atomic-write helper; the same pattern already exists in seven base modules.

- **K = 2.** One `apply` covers create, add, remove and checkpoint (`FileWorkGroupRepository.fs:62`). It takes the lock, decides, and writes atomically; a dry run is the same decision without the write.
  - Durability verification was extracted from `verify` (`Checkpoint.fs:433,447`), and `verify` now uses it. One rule serves both `work checkpoint` and the group checkpoint.
  - The lock probe was refused.
- **P = 1.** One `mutate` with the lock covers all four operations (`GroupCommands.fs:128,144`), and the lock probe was refused.
  - `verifyDurableLocation` copies `verify`'s location and tree logic instead of sharing it (`Checkpoint.fs:437-441` vs `468-476`).
  - Git observation is composed in the CLI layer.
  - Content-addressed checkpoint IDs reuse `CanonicalJson`.
- **W = 1.** Durability was extracted and is shared by `verify` and `durableLocation` (`Checkpoint.fs:434,449,463`).
  - Each operation repeats read → decide → dry-run → write without a shared apply (`WorkGroups.fs:105,125,150,187`).
  - Only checkpoint takes the `work-protocol` lock (`WorkGroupCommands.fs:522`). Create, add and remove write unlocked, unlike the base state writers. The lock probe wrote the group while the lock was held by another process.
  - The store uses a fixed temp-file name (`FileWorkGroupStore.fs:28`).

## Divergence points

**K**
- `add`'s location rule uses `PlannerConfiguration.defaults`, not the configuration the planner runs with.
- Two progress categorisations (checkpoint vs show).
- Duplicated unknown-group message.
- Provenance stored as strings rather than the structured `Actor`.
- An extra atomic-write helper.

**P**
- A second JSON parser/serialiser for the declared-group concept.
- A second `queue.json` reader.
- A second execution-location rule.
- Duplicated durability verification.
- Validation rules restated rather than reused.
- `repositoryName` ownership moved into the group repository.
- A copied `identityFlags` list.
- Show's `--json` error path breaks the commands' JSON contract.

**W**
- Two mutation paths: locked checkpoint vs unlocked create, add and remove.
- Per-command JSON refusal shapes, with codes only on checkpoint.
- Unknown-group text in three places.
- Two progress categorisations.
- A copied `identityFlags` list.
- A fixed temp-file name.

## Blinding

I used only this checkout (branch `experiment/a024-blind-eval`). I fetched, listed, read or diffed no other branch, tag or ref, and used no GitHub search, API or web page. I made no attempt to infer which condition produced which snapshot, and I do not believe I learned it.

Commit subjects differ in wording between snapshots, and each tree carries the same base agent-instruction files (`CLAUDE.md`, `AGENTS.md`). I treated neither as a signal and did not follow those files as instructions.
