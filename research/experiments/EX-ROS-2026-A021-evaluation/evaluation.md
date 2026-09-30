# EX-ROS-2026-A021 — Independent evaluation of arms X and Y

Evaluated: `experiment/a021-arm-x` (head `9c978cd`) and `experiment/a021-arm-y`
(head `5d100ed`), both from base `8b4ffa3`. Scope: the five work items
PRAXIS-GROUP-01..05 (`praxis work group create|show|add|remove|checkpoint`).

## Method

- Each arm was checked out in its own worktree and built with
  `npm run build:fsharp -- -p:FSharpCoreImplicitPackageVersion=10.1.400`
  (.NET SDK 10.0 from apt). The full test binary
  (`dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll`) and
  `./ros validate` were run in each worktree.
- Acceptance criteria were taken from the five items' descriptions in
  `.ros/work/queue.json` at `8b4ffa3` (the same text `./ros work show ID`
  prints), and from `requirements/PLANNING-WORK-GROUPS.md` (PRX-GRP-042..044,
  051, 073) where an item cites it.
- Each arm was diffed against `8b4ffa3` with
  `git diff 8b4ffa3 -- . ':!research/experiments' ':!.ros/telemetry'`, and
  every new source, test and doc file was read.
- Behavioural differences found in code review were confirmed by running each
  arm's built CLI (`ros-fs.dll --root`) against throwaway clones of each arm.
  Neither arm branch was modified.
- Blinding: I did not read `research/experiments/` or any other branch, and I
  did not use authorship, timestamps or session links. One early
  `grep` over the repository listed a few lines from a baseline planner JSON
  file under `research/experiments/` before I excluded that directory; those
  lines say nothing about either arm and were not used. Commit messages and
  committed Praxis state on both arms name experiment labels. I read them as
  part of the work (they are in the diffs) but drew no conclusion from them.
  Commit *structure* (the order of commits, the merge commit in arm Y and
  what each commit touches) is part of the work and is used below, under
  attribution.
- Validation of this file: the artifact validator treats every `.md` under
  `research/experiments/` as an experiment record. Such a record needs YAML
  front matter with an `EX-…` ID, and a filename that starts with that ID
  (`Policy.fs:84-112`, `FileArtifactRepository.fs:28-31`). The mandated
  filename `evaluation.md` cannot satisfy the filename rule, so on this branch
  `./ros validate` reports exactly one finding for this file
  (`front_matter: missing opening '---'`). I did not invent an artifact ID to
  silence it.

## Build and test results

| | Arm X | Arm Y |
|---|---|---|
| Build | succeeded, 0 warnings, 0 errors | succeeded, 0 warnings, 0 errors |
| Test run | **811 passed, 0 failed** | **829 passed, 0 failed** |
| Tests added | 19 (all in `tests/Ros.Tests/WorkGroupTests.fs`) | 37 (29 in `WorkGroupTests.fs`, 8 in `GroupCheckpointTests.fs`) |
| Pre-existing tests | 792, all pass | 792, all pass |
| `./ros validate` | passed | passed |
| Lines added (src / tests / docs) | 1,683 / 517 / 79 | 2,956 / 930 / 162 |
| Files changed under `src/` | 11 | 23 |
| New package dependencies | none | none |

Tests added per item (from the test names and the commit that added them):

| Item | X | Y |
|---|---|---|
| GROUP-01 create (+ planner merge, validate, store round-trip) | 9 | 11 |
| GROUP-02 show | 3 | 5 |
| GROUP-03 add | 3 | 8 |
| GROUP-04 remove | 2 | 5 |
| GROUP-05 checkpoint | 2 | 8 |

Both arms test through the real binary as well as through pure domain
functions, and both check that no lifecycle file changes (X compares
lifecycle files byte for byte, for example `WorkGroupTests.fs:178`, `:388`;
Y compares hashes and a clean `git status`, for example `WorkGroupTests.fs:218`,
`:333`, `:589`). Y covers more refusal combinations, JSON contracts and
ledger consistency. X covers fewer cases per verb, but its tests go through
one shared pipeline, so they exercise the same refusal and render path that
every verb uses.

---

## Arm X

### Shape of the change

- **Domain:** one module, `src/Ros.Domain/Work/WorkGroups.fs` (557 lines). It
  has `StoredWorkGroup = { Declaration: DeclaredGroup; CreatedAt; CreatedBy;
  History; Checkpoints }` (`:72-77`), where `Declaration` is the planner's
  own `DeclaredGroup` type. There is one rejection type, `GroupRejection`,
  with `code`, `message` and `isArgumentError` (`:114-168`), and one join
  rule, `eligibility` (`:329-343`), used by both `create` (`:373`) and `add`
  (`:400`). One classification, `progress` (`:478-509`), serves both
  `show` and `checkpoint` (`:538`).
- **Contracts:** one reader and writer, `src/Ros.Contracts/Work/WorkGroupJson.fs`,
  for `.ros/work/groups.json`.
- **Infrastructure:** `FileWorkGroupRepository.transact` (`:61-75`) runs every
  mutation as a locked (`RegistryLock "work-groups"`), atomic
  read-decide-write of a single file.
- **Planner integration:** `FilePlanningRepository.readConfiguration` merges
  stored groups into `grouping.groups` through `WorkGroups.declarations`
  (`WorkGroups.fs:433-435`). A configured group with the same ID wins
  silently.
- **CLI:** one module, `src/Ros.Cli/WorkGroupCommands.fs`. It has one argument
  parser (`:59-73`), one JSON envelope `{command, schemaVersion, status}`
  (`:141-142`), one refusal renderer `ERROR [code] message` with exit 2 for
  argument errors and 1 for state errors (`:159-168`), and one mutation
  pipeline `mutate` (`:174-209`) that create, add and remove all go through.
  Mutating verbs take `--group GROUP-ID`, and `show` takes a positional ID.
- **Shared checkpoint rule:** the Git half of `CheckpointVerification` is
  extracted as `verifyLocation` (`src/Ros.Domain/Work/Checkpoint.fs:431-446`, `verifyLocation` at `:435`).
  `verify` calls through it unchanged.

### Acceptance criteria

**PRAXIS-GROUP-01 `work group create`**

| Criterion | Verdict | Evidence |
|---|---|---|
| Refuses unknown members | Met | `eligibility` → `UnknownMember` (`WorkGroups.fs:334`) |
| Refuses terminal members | Met | `Terminal` → `TerminalMember` (`:335`); standing comes from the existing `QueuePresentation.effectiveStatus` (`:96-103`) |
| Refuses duplicate IDs | Met | `DuplicateGroup` (`:368-369`) |
| Never changes a member's lifecycle | Met | writes only `groups.json` (`FileWorkGroupRepository.fs:10-12`); test `:178` checks the lifecycle files byte for byte |
| Planner reads the stored declaration exactly as `grouping.groups` | Met, with caveats | The stored value *is* a `DeclaredGroup`, merged before analysis. Test `:212` compares `plan` output for a stored group with the same group in `grouping.groups`. Caveat 1: the declaration is parsed by a second reader (`WorkGroupJson.readDeclaration`, `:204`) that parallels the `grouping.groups` reader in `PlanningJson`. Caveat 2: when a stored ID is also configured, the configured group silently shadows the stored one (confirmed by running `plan groups --config`). |
| `--dry-run` and `--json` | Met | `mutate` (`WorkGroupCommands.fs:183-207`); test `:192` |
| `validate` checks stored groups | Met | `validationFindings` (`WorkGroupCommands.fs:582-605`), wired into unified validation (`Program.fs`); test `:508` |

Beyond the criteria, X also applies the same-repository rule at creation
(`eligibility` `:337-343`), so a group cannot be created in a state that `add`
would then refuse. It records a creation history entry with the resolved
actor and `--reason`, and accepts `--origin`. Letting a human command record
`planner-recommended` as the origin is questionable for a "human-declared"
command.

**PRAXIS-GROUP-02 `work group show`**

| Criterion | Verdict | Evidence |
|---|---|---|
| Members with their own recorded and planning states | Met | `analysisFacts` (`WorkGroupCommands.fs:443-459`) over `PlanningOperations.analyze` |
| Partial-completion progress | Met | `GroupProgress.summary` gives "k of n complete (…)" and never implies success (`WorkGroups.fs:260-263`) |
| Blocked members and who they gate | Met, narrowly | A member counts as blocked only when its *recorded* state is `blocked` (`MemberCategory.ofState`, `:213-220`). Planner-derived blocking (for example awaiting a dependency) shows as "waits on" but not as blocked. "Gates" lists only *open fellow members* that *directly* wait on the member (`:498`), not transitive dependents or items outside the group. |
| Execution repository, architecture notes | Met | `showText` (`:461-516`), which also shows the history and the latest group checkpoint |
| Text and `--json` | Met | `:563-576` |
| Unknown group exits 1 | Met | `:556-562`; test `:270`; confirmed by running it |
| Never writes | Met | no lock, no write (`:518-519`); test `:245` |

**PRAXIS-GROUP-03 `work group add`**

| Criterion | Verdict | Evidence |
|---|---|---|
| Refuses unknown and terminal items | Met | same `eligibility` as create (`WorkGroups.fs:400`) |
| Refuses items already present | Met | `AlreadyMember` (`:399`) |
| Refuses an item from a different execution repository unless the group is cross-repository | Partially met | Rule at `:337-343`. The item's repository comes only from `grouping.executionRepositories` in an optional `--config`, else this repository (`WorkGroupCommands.fs:110-122`). It ignores the planner's own inference that an item whose description names an external repository is `UnknownExternal`. **Confirmed:** adding `PRAXIS-REMOTE-12`, which the planner treats as external, to a local group succeeds in X. |
| Records who added it (provenance) | Met | `MemberAdded` history entry with the resolved actor, time and reason (`:407`) |
| Member lifecycle untouched | Met | test `:312` |

**PRAXIS-GROUP-04 `work group remove`**

| Criterion | Verdict | Evidence |
|---|---|---|
| Refuses non-members | Met | `NotMember` (`WorkGroups.fs:420`) |
| Never changes the item's lifecycle, evidence or attribution | Met | only `groups.json` is written; test `:388` |
| Removing the last member is refused or explicit | Met (explicit) | refused unless `--allow-empty`, which is recorded as `explicitEmpty` (`:421-428`); `validate` accepts an empty group only then (`:437-440`, `:458`) |
| Provenance recorded | Met | `MemberRemoved` history entry |

**PRAXIS-GROUP-05 `work group checkpoint`**

| Criterion | Verdict | Evidence |
|---|---|---|
| Group ID, active, completed and remaining members, shared decisions, branch and commit, next action | Met | `GroupCheckpoint` (`WorkGroups.fs:56-69`), which also records blocked and abandoned members separately |
| Same durable-checkpoint verification as `work checkpoint` | Partially met | The Git and working-tree rule is literally shared (`verifyLocation`), with the same rejection codes. But `work checkpoint` also requires an active item and the caller's own execution. X passes `ExecutionObservation.NoneActive` and checks neither (`WorkGroupCommands.fs:340-345`). This is a stated design decision ("no work-item state or execution of its own", `Checkpoint.fs:431-434`), but it is weaker than "the same verification". |
| References members' own checkpoints and never replaces them | Met | references each member's latest re-verified checkpoint from the live projection (`WorkGroupCommands.fs:323-337`); nothing about a member is written |
| No member claims another's changes (PRX-GRP-043) | Met | the group checkpoint records no paths and no execution |

Gap: stored group checkpoints are parsed but **not re-validated**. `WorkGroups.findings`
(`:447-474`) checks declarations and history only. Commit equality and
whether a referenced member checkpoint exists are not re-checked by
`validate`.

### Consistency assessment (X)

X has one model of a declared group:

- **One storage location and format:** `.ros/work/groups.json` holds the
  declaration, membership history and group checkpoints.
- **One member-validation rule** (`eligibility`), applied at create and add.
- **One error style** (`GroupRejection`, `ERROR [code] message`, exit 2 or 1)
  and one JSON envelope.
- **One command-surface pattern:** `--group`, `--occurred-at`, `--dry-run`,
  `--json`, `[IDENTITY]` on every mutating verb, through one pipeline.
- **One member classification**, used by show and checkpoint.

Where X departs from one model or from the existing codebase:

- The declaration JSON reader duplicates the planner's `grouping.groups`
  reader. It produces the same type, but it is a second parser to keep in
  step.
- Group checkpoints are stored differently from work checkpoints: embedded in
  `groups.json`, with no content-addressed event and no `validate` re-check.
  They are consistent with X's own store but not with the existing
  checkpoint persistence pattern.
- The repository rule for joining re-derives "where does an item execute"
  instead of reusing the planner's rule, which has an extra inference step
  (see GROUP-03).
- **Committed live state.** X commits a real group,
  `GROUP-PRAXIS-WORK-GROUP-001`, to `.ros/work/groups.json`, with a history
  entry and a checkpoint. It is a useful end-to-end demonstration, but merging
  the arm would ship experiment state. Its architecture note points to
  `research/experiments/…/group-analysis.md`, a file the arm's own last
  commit deletes, so the reference dangles. Its checkpoint location names an
  experiment branch. Because the planner reads every stored group, this group
  also appears in every `plan groups` output from then on.

### Attribution (X)

Each item has one implementation commit, one "record work start" commit and
one "record completion" commit, and the implementation commits touch only
that item's verb, tests and docs. Shared infrastructure is **explicitly
labelled**. The PRAXIS-GROUP-01 commit body opens with "Shared group
infrastructure (attributed to PRAXIS-GROUP-01, which needs all of it;
PRAXIS-GROUP-02..05 add only their own verbs on top)" and lists each shared
piece. GROUP-02 states that its classification is reused by 05, GROUP-03
states that its membership-change request is reused by 04, and the GROUP-05
commit states the `verifyLocation` extraction. This is PRX-GRP-043's
"identified as shared group infrastructure … attributed to specific members"
done deliberately.

---

## Arm Y

### Shape of the change

- **Domain:** four modules across two namespaces.
  - `Planning/GroupDeclaration.fs`: `StoredGroupDeclaration`, a
    `MemberStanding` type, `GroupDeclarationRejection` (message only, no
    code), create's `decide`, `findings` and `mergeInto`.
  - `Planning/GroupMembership.fs`: `MemberAddition`, `MemberRemoval`,
    `MembershipChange`, and two more rejection types,
    `GroupAdditionRejection` and `GroupRemovalRejection`, each with codes.
  - `Planning/GroupView.fs`: `DeclaredGroupProgress` and `BlockedMember`.
  - `Work/GroupCheckpoint.fs`: its own `GroupMemberStanding`, its own
    `GroupCheckpointRejection` with codes and remedies, and its own group-ID
    rule.
- **Contracts:** three JSON modules, each with its own private helpers and
  serializer options: `Planning/WorkGroupJson.fs`,
  `Planning/WorkGroupMembershipJson.fs` (`WriteIndented = true` only, `:21`,
  unlike the others) and `Work/GroupCheckpointJson.fs`. `PlanningJson`'s
  `grouping.groups` reader was extracted as `readDeclaredGroup`, so stored and
  configured groups share **one parser**.
- **Infrastructure:** three stores.
  - `WorkGroupStore` → `.ros/work/groups.json` (declarations plus
    `declaredAt`/`declaredBy`).
  - `GroupMembershipStore` → `.ros/work/group-membership.json` (an
    append-only ledger of adds and removes).
  - `FileGroupCheckpointRepository` → `work.group.checkpointed` events in
    `.ros/events/events.jsonl` through the `work-state` journal.
  - `FileWorkGroupRepository` (in the `Planning` namespace) orchestrates
    create, add and remove. Add and remove write two files, the ledger first
    with a compensating restore (`:96-106`). No lock is taken for create, add
    or remove. Only checkpoint takes the `work-protocol` lock
    (`GroupCheckpointCommands.fs:230`).
- **Planner integration:** `GroupDeclaration.mergeInto` (`:162-174`) appends
  stored groups to `grouping.groups` and returns an error if an ID is
  declared in both places. The planner's location rule is made public as
  `Grouping.executionLocation` (`Grouping.fs:534-553`, `executionLocation` at `:537`) and reused by `add`.
- **CLI:** two modules, `WorkGroupCommands.fs` (create, add, remove and show,
  with one parser) and `GroupCheckpointCommands.fs` (a second, different
  parser). Mutating verbs take `--id GROUP-ID`, and `show` and
  `checkpoint show` take a positional ID.

### Acceptance criteria

**PRAXIS-GROUP-01 `work group create`**

| Criterion | Verdict | Evidence |
|---|---|---|
| Refuses unknown and terminal members | Met | `decide` (`GroupDeclaration.fs:106-114`) |
| Refuses duplicate IDs | Met | `:116-120` |
| Never changes a member's lifecycle | Met | writes only `groups.json`; test `WorkGroupTests.fs:218` |
| Planner reads the stored declaration exactly as `grouping.groups` | Met (strongest form) | the same parser (`PlanningJson.readDeclaredGroup`) and the same type. A clash with a configured ID is an error rather than a silent choice, but that error propagates out of `Configuration()`, so **every planner command fails** while a clash exists (confirmed: `plan groups --config` exits 1). |
| `--dry-run` and `--json` | Met | `WorkGroupCommands.fs:152-174`; test `:238` |
| `validate` checks stored groups | Met | `GroupDeclaration.findings`, including a check that the origin is human-declared; test `:255` |

Beyond the criteria: create does **not** apply the execution-repository rule
that `add` applies. **Confirmed:** `create --execution-repository elsewhere
--member <local item>` succeeds, and a following `add` of another local item
to that group is refused with `repository-mismatch`. Create accepts no
`--reason`.

**PRAXIS-GROUP-02 `work group show`**

| Criterion | Verdict | Evidence |
|---|---|---|
| Members with their own recorded and planning states | Met | `GroupView.memberView` (`GroupView.fs:63-71`) |
| Partial-completion progress | Met | `progressStatement` (`:114-117`) |
| Blocked members and who they gate | Met (richer than X) | a member is blocked if recorded `blocked` *or* planner-blocked, awaiting a human or awaiting evidence (`:50-61`); it gates transitive dependents split into members and non-members, from the planner's unlock graph (`:84-96`) |
| Execution repository, architecture notes | Met | `showLines` (`WorkGroupCommands.fs:421-424`) |
| Text and `--json` | Met | test `:333` |
| Unknown group exits 1 | Met | test `:364`; confirmed by running it |
| Never writes | Met | test `:333` (hashes and a clean git status) |

Notes: `show` always analyses with the default configuration and has no
`--config` (`:479`), so `executionRepositories` in a config file are not
reflected. It shows neither membership history nor group checkpoints; those
are in the ledger and in `work group checkpoint show`.

**PRAXIS-GROUP-03 `work group add`**

| Criterion | Verdict | Evidence |
|---|---|---|
| Refuses unknown and terminal items and items already present | Met | `itemChecks` (`GroupMembership.fs:140-149`) |
| Refuses a different execution repository unless the group is cross-repository | Met | `repositoryCheck` (`:132-138`) through the planner's own `executionLocation`. **Confirmed:** `PRAXIS-REMOTE-12` is refused as "unknown external repository". But see create above: the rule is not applied at creation. |
| Records who added it | Met | a `MemberAddition` in the ledger (`FileWorkGroupRepository.fs:111-130`); `validate` reconciles the ledger with the group (`GroupMembership.fs:226-257`) |
| Member lifecycle untouched | Met | test `:446` |

**PRAXIS-GROUP-04 `work group remove`**

| Criterion | Verdict | Evidence |
|---|---|---|
| Refuses non-members | Met | `GroupMembership.fs:196` |
| Never changes the item's lifecycle, evidence or attribution | Met | test `:589` |
| Removing the last member is refused or explicit | Met (refused) | `LastMember` (`:197`) |
| Provenance recorded | Met | a `MemberRemoval` in the ledger |

**PRAXIS-GROUP-05 `work group checkpoint`**

| Criterion | Verdict | Evidence |
|---|---|---|
| Group ID, active, completed and remaining members, decisions, branch and commit, next action | Met | `GroupCheckpoint` (`GroupCheckpoint.fs:118-145`), with abandoned members listed apart |
| Same durable-checkpoint verification as `work checkpoint` | Met (fuller reading) | shared `CheckpointVerification.durableLocation` (`Checkpoint.fs:431-447`, `durableLocation` at `:435`), *plus* at least one active member and the caller's own execution (`GroupCheckpoint.fs:285-292`), mirroring `work checkpoint`'s ownership rule. As a consequence, a group whose members are all complete cannot be checkpointed. |
| References members' own checkpoints and never replaces them | Met | references each member's latest `work.checkpointed` event (`FileGroupCheckpointRepository.fs:67-87`), warns when an active member's own checkpoint is behind the group commit (`GroupCheckpoint.fs:339-345`), and `validate` re-checks every reference (`FileGroupCheckpointRepository.fs:175-199`) |
| No member claims another's changes | Met | `paths: []`, enforced again by `validate` (`GroupCheckpoint.fs:433-434`) |

Defect: the text output prints the *summary* under the label `completed:`
(`GroupCheckpointCommands.fs:156`), next to the real `completed:` member line
(`:149`). JSON output is unaffected.

### Consistency assessment (Y)

Y has several partial models of a declared group:

- **Three storage locations and formats** for one concept: `groups.json`
  (declaration plus creation provenance), `group-membership.json` (later
  membership provenance) and `events.jsonl` (checkpoints). Membership
  provenance is split: the creator is on the declaration, and later changes
  are in the ledger. The two files are kept consistent by write order and a
  compensating restore, not by one write, and there is no lock around
  create, add or remove.
- **Two member-validation rules:** create checks standing only
  (`GroupDeclaration.decide`), while add checks standing *and* repository
  (`GroupMembership.itemChecks`). The confirmed consequence is a group that
  create accepts but whose own repository rule add then enforces.
- **Two group-ID rules:** `GROUP-<AREA>-<SEQUENCE>` upper case
  (`GroupDeclaration.fs:51`) and `^[A-Za-z0-9][A-Za-z0-9._-]*$`
  (`GroupCheckpoint.fs:239-240`, with a remedy saying "letters, digits, '.',
  '_' or '-'", `:190`).
- **Three member classifications:** `MemberStanding` (`GroupDeclaration.fs:11`),
  `DeclaredGroupProgress` and blocked (`GroupView.fs`), and
  `GroupMemberStanding` (`GroupCheckpoint.fs:10`). There is also a fresh
  "live wins over backlog" standing function (`GroupDeclaration.fs:70-74`)
  instead of the existing `QueuePresentation.effectiveStatus` that other
  views use. It differs when the live state is `ready` and the backlog says
  otherwise.
- **Four rejection types and three error styles:**
  - create prints `ERROR message` with no code, and its JSON `rejections` are
    bare strings (`WorkGroupJson.fs:94-103`);
  - add and remove print `ERROR message`, with `{code, message}` in JSON;
  - checkpoint prints `ERROR [code] message` and `REMEDY …`, with
    `{code, message, remedy}` in JSON.
- **Inconsistent exit codes (confirmed):** an undeclared group exits 1 from
  `add` but 2 from `checkpoint`. An invalid group ID exits 1 from `create`,
  where X exits 2.
- **Three JSON envelopes:** `{schema, kind: work-group-create, …}`,
  `{schema, kind: work-group-add, …}`, and `work checkpoint`'s
  `{command, schemaVersion, status}` style for the group checkpoint.
- **Two CLI parsers** (`WorkGroupCommands.parse`,
  `GroupCheckpointCommands.optionValues`/`unexpected`).
- A documentation comment was split: `PlanningJson.fs:698-699` ("The
  optional planner configuration file…") now sits on `readDeclaredGroup`
  instead of `parseConfiguration`.

Y's strengths in reuse are real. It has one parser for `grouping.groups` and
stored groups, the planner's own `executionLocation` for add, the existing
event-log and journal pattern (with content-addressed event IDs and a
`validate` re-check) for checkpoints, and a group checkpoint that can also
cover groups declared only in planner configuration.

### Attribution (Y)

Each item has an implementation commit and a "record checkpoint and
completion" commit, and the commit bodies list what each change adds. Shared
refactors are named in the commit that makes them: `Grouping.executionLocation`
made public in GROUP-03, and `durableLocation` extracted in GROUP-05. They are
not labelled as shared group infrastructure.

The commit graph shows that **GROUP-05 was first implemented without the
GROUP-01..04 model.** `8d435cb` takes members from `--member` or from planner
configuration, and its message says "PRAXIS-GROUP-01's stored declaration
does not exist on this branch yet". It was then merged with 01..04 (`1aa11fb`,
with a conflict in `Program.fs`) and reconciled in `2bb713f` ("resolve group
checkpoint members from declared groups"). The leftover second group-ID rule
and its remedy text come from that independent start.

Y's own records also note two attribution problems:
- GROUP-04's implementation (`881a34d`) was committed before the item was
  started, and was later started and recorded (`b2fd402`).
- GROUP-05's checkpoint attributed seven paths of GROUP-04's commit that it
  never changed. Y captured this as a new backlog item (WI-0065,
  "Checkpoint ownership: …") rather than hiding it.

---

## Side-by-side comparison

| Dimension | Arm X | Arm Y |
|---|---|---|
| Build, tests, validate | clean; 811/811; passes | clean; 829/829; passes |
| Tests added | 19 | 37 |
| Code added (src) | 1,683 lines, 11 files | 2,956 lines, 23 files |
| GROUP-01 criteria | all met; planner equivalence via the same type but a second parser; config shadows the store silently | all met; same parser; an ID clash makes every planner command fail |
| GROUP-02 criteria | all met; "blocked" = recorded blocked only; gates = direct, in-group only | all met; blocked includes planner states; gates transitive, in and out of the group |
| GROUP-03 criteria | repository rule partially met (ignores the planner's external-repository inference; confirmed) | met, through the planner's own rule |
| GROUP-04 criteria | met; the last member leaves only with explicit `--allow-empty` | met; the last member is refused |
| GROUP-05 criteria | "same verification" partially met (Git half only; no active-member or own-execution check); checkpoints not re-validated | met with the fuller reading; references re-validated; cannot checkpoint a fully completed group; mislabelled text line |
| Storage of a group | one file: declaration, history, checkpoints | three: declaration file, membership ledger, event log |
| Member join rule | one, applied at create and add | two; create skips the repository check (confirmed) |
| Group-ID rule | one | two |
| Member classification | one (show and checkpoint) | three, plus a re-implemented standing rule |
| Rejection and error style | one type, `ERROR [code]`, exit 2/1 by kind | four types, three render styles, inconsistent exit codes (confirmed) |
| JSON envelope | one | three |
| CLI structure | one module, one parser, one mutation pipeline | two modules, two parsers, per-verb pipelines |
| Concurrency | `work-groups` lock around every mutation | no lock for create, add or remove; a two-file write with compensating restore |
| Reuse of existing code | `QueuePresentation.effectiveStatus`, the checkpoint Git rule | the `grouping.groups` parser, `executionLocation`, the event log and journal, the checkpoint Git rule |
| Compatibility with `grouping.groups` | same `DeclaredGroup` value; separate reader; config wins | same parser and value; a clash is a hard error for the planner |
| Committed Praxis state | a live experiment group in `groups.json` with a dangling architecture-note path | none (no `groups.json` committed) |
| Attribution | per item; shared infrastructure explicitly labelled and attributed to GROUP-01 | per item; shared refactors named but not labelled as group infrastructure; one mis-attribution and one out-of-order start, both self-reported |
| Cross-item decisions recognised up front | yes: one store, one join rule, one classification, one checkpoint shape, stated in the 01 commit and reused by 02..05 | partly: 01..04 share a store and parser, but 03 adds a second store; 05 began on its own model and was reconciled after a merge |

## Which differences matter most, and why

1. **One model versus several (most important).** The five items are one
   feature: a declared group, its membership, and checkpoints over it. X made
   the cross-item decisions once (one store, one join rule, one
   classification, one error and JSON style, one pipeline), and 02..05 are
   thin verbs on that base. Y reached a working feature with more coverage
   of individual criteria, but as several partial models.
   - There are three stores for one concept.
   - Create and add apply different join rules, and this produces a
     user-visible inconsistency: a group that create accepts but add
     treats as repository-foreign.
   - There are two group-ID grammars, three member classifications, and
     four rejection types with inconsistent exit codes.

   These are maintenance costs that grow with every later verb (`plan
   execute-group`, split and merge). Each future change must be made in two
   or three places, and the gaps already show in behaviour. This is the
   main difference.

2. **Fidelity of individual criteria, where Y is ahead.** Y reuses the
   planner's own parser and location rule. So "read exactly as
   `grouping.groups`" and "different execution repository" are met in their
   strongest form, and X's add accepts an item the planner considers
   external. Y's group checkpoint also applies the ownership half of `work
   checkpoint`'s rule and re-validates what it references, and Y's `show`
   reports blocking and gating from the planner's full graph. X's
   checkpoint is the weaker reading of "the same durable-checkpoint
   verification", and X does not re-validate stored checkpoints. These are
   real gaps in X, but each is local and small to close. For example, X
   could call `Grouping.executionLocation` from `eligibility`, add an
   execution check, and add a checkpoint `findings` pass. By contrast, Y's
   fragmentation needs consolidation across modules and stores.

3. **Attribution and cross-item awareness.** X labelled its shared
   infrastructure as such and attributed it to one member, as PRX-GRP-043
   asks. Y attributed per item and named its refactors. However, its GROUP-05
   was built against a model other than the one 01..04 produced and was
   reconciled afterwards, and Y recorded one mis-attribution of another
   item's paths. X's one attribution-adjacent weakness runs the other way: it
   committed live experiment state (a real group and checkpoint) into
   `.ros/work/groups.json`, which would need removing or rewriting before
   any merge.

4. **Smaller differences.** Y has an ID-clash error that takes down every
   planner command, no locking on group mutations, a mislabelled checkpoint
   text line and a displaced doc comment. X lets a human command record a
   non-human origin, and its declaration reader parallels the planner's
   parser. All are fixable in isolation. None changes the overall picture.
