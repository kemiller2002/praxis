# Group analysis: GROUP-PRAXIS-WORK-GROUP-001 (PRX-GRP-040)

Grouped arm of `EX-ROS-2026-A021`, branch `experiment/a021-grouped`, written
before any code change. Members: `PRAXIS-GROUP-01` (`work group create`),
`-02` (`show`), `-03` (`add`), `-04` (`remove`), `-05` (`checkpoint`). Inputs
read: each item's backlog description (`./ros work show ID`), `./ros plan
explain-group GROUP-PRAXIS-WORK-GROUP-001 --config
research/experiments/EX-ROS-2026-A021-baseline/planner-config.json`,
`requirements/PLANNING-WORK-GROUPS.md` (PRX-GRP-001..090), and the code the
members touch (listed under "Common architecture").

## 1. What the group is

One CLI command family over **one durable record type**: a human-declared
execution group stored in Praxis state. Four of the five commands are thin
verbs over the same store (create, read, add, remove); the fifth appends a
group-level checkpoint to the same record. Every member's acceptance criteria
are statements about that one record, its validation rules and its
relationship to the members' own lifecycle, attribution and checkpoints.

## 2. Common architecture

The repository already fixes the layering (`ArchitectureTests`): Domain (pure)
<- Contracts (JSON) <- Application (ports) <- Infrastructure (files, Git) <-
Cli. The group follows it once:

| Layer | Shared piece (one for all members) | Existing code it builds on |
| --- | --- | --- |
| Domain | `Ros.Domain.Work.WorkGroups`: the stored-group model, the member-eligibility rule, pure decisions `create`/`add`/`remove`/`checkpoint`, the `show` progress projection, and stored-group validation. Compiled after `Planning/Model.fs` so it reuses `DeclaredGroup`, `GroupKind`, `GroupOrigin` rather than redefining them. | `Planning/Model.fs` (`DeclaredGroup`), `Work/Checkpoint.fs` (`GitDurableLocation`, `CheckpointVerification`), `Work/QueuePresentation.fs` (`effectiveStatus`), `Provenance/Actor.fs` |
| Contracts | `WorkGroupJson`: one reader/writer of `.ros/work/groups.json` and one JSON view per command. | `ActorJson`, `PlanningJson` style |
| Infrastructure | `FileWorkGroupRepository`: read all groups, atomic write under a lock, member lookup from queue + live context; `FilePlanningRepository` merges stored groups into `grouping.groups`. | `RegistryLock`, atomic temp+move writes, `FilePlanningRepository.readQueue/readLive` |
| Cli | `WorkGroupCommands`: one argument parser, one renderer family, one dispatch entry `work group <verb>`. | `CheckpointCommands`, `PlanCommands` |

The store is a separate file, `.ros/work/groups.json`, not a field on queue
items or live-context items. Reason: membership must never touch a member's
lifecycle record (PRX-GRP-002, acceptance of -01/-03/-04), and a separate file
makes that true by construction: no group command writes `queue.json`,
`current.json` or `events.jsonl`.

### Stored record (one shape, all commands)

```json
{
  "schemaVersion": 1,
  "groups": [
    {
      "id": "GROUP-...",
      "members": ["A", "B"],
      "kind": "shared-api-surface",
      "origin": "human-declared",
      "sharedContext": ["..."],
      "executionRepository": "repository-operating-system",
      "crossRepository": false,
      "architectureNotes": ["..."],
      "createdAt": "...",
      "createdBy": { "kind": "agent", "id": "...", ... },
      "history": [
        { "operation": "created|member-added|member-removed", "member": "B", "at": "...", "actor": {...}, "reason": "..." }
      ],
      "checkpoints": [ { group checkpoint, section 7 } ]
    }
  ]
}
```

The first seven fields are exactly the planner's `DeclaredGroup` fields and
use the same JSON names as `grouping.groups` in planner configuration, so the
planner can read a stored group "exactly as it reads grouping.groups" by
projecting the record to `DeclaredGroup`, with no second model.

## 3. Shared invariants

1. **Membership never changes a member's lifecycle, evidence or attribution**
   (PRX-GRP-002, -041, -043; acceptance of -01, -03, -04). Enforced
   structurally: group commands write only `groups.json`.
2. **One member-eligibility rule** for create and add: a member must be a
   known work item (in the queue or live context) and not terminal
   (`complete`, `abandoned` as `QueuePresentation.effectiveStatus` reports
   it). Unknown and terminal are distinct refusals.
3. **Group IDs are unique** across stored groups; `work group create` refuses
   a duplicate. Group ID syntax: `GROUP-` followed by upper-case
   alphanumerics and hyphens (PRX-GRP-010); member IDs use `WorkItemId.isValid`.
4. **A member appears at most once** in a group.
5. **Repository locality** (PRX-GRP-051): every member's execution repository
   equals the group's, unless the group is `crossRepository`. The same rule
   serves create and add.
6. **Groups are never empty**: removing the last member is refused unless
   the caller passes `--allow-empty`, which is recorded explicitly in history
   (the acceptance of -04 allows "refused or explicit"; one explicit flag
   keeps both honest).
7. **Every mutation carries provenance**: the resolved actor, the real
   `--occurred-at` and an optional reason, appended to `history`; entries are
   never rewritten (mirrors "preserve existing provenance").
8. **Partial completion** (PRX-GRP-042): a group never reports itself
   complete because some members are; `show` reports each member's own state.
   Terminal members stay members (they completed inside the group); only
   *adding* a terminal item is refused.
9. **Checkpoints are durable and non-substituting** (PRX-GRP-044, -043): a
   group checkpoint passes the same Git verification as `work checkpoint`
   (local HEAD == upstream remote head, no meaningful uncommitted change),
   references members' own latest checkpoint IDs, and never writes a member
   checkpoint or claims paths.
10. **Deterministic** (PRX-GRP-075): stored order is insertion order for
    members and history; groups are kept sorted by ID; the same input produces
    byte-identical output.

## 4. Conflicting requirements and their resolution

| Tension | Resolution |
| --- | --- |
| -01 "refuses terminal members" vs PRX-GRP-042 partial completion (members become terminal later) | Eligibility applies when a member **joins** (create, add). `validate` accepts terminal members of stored groups and rejects only unknown members, duplicates and structural errors. |
| -04 "removing the last member is refused **or** explicit" | Refused by default; `--allow-empty` makes it explicit and is recorded in history. `validate` accepts an empty group only when its last history entry is an explicit empty removal. |
| Planner configuration (`--config`) and stored groups can both declare an ID | Stored groups are merged into `grouping.groups`; a configuration group with the same ID wins for that plan (an explicit per-invocation input is the narrower, newer source), and the stored one is not duplicated. `work group create` refuses IDs already stored; it cannot see every possible `--config` file, so this precedence is documented instead. |
| "same durable-checkpoint verification as work checkpoint" vs a group has no single execution or lifecycle state | Reuse only the Git half of `CheckpointVerification` (location + working tree). Extract it as a public function so both commands share one implementation instead of copying it; the work-item half (active item, resolved execution) does not apply to a group. |
| -03 provenance "who added it" vs never impersonate | Actor is resolved with the same `ProvenanceCommands.withResolvedActor` as every mutating command; no free-form author flag. |
| Execution repository of an *item* is not recorded on the item | Same source the planner uses: `grouping.executionRepositories` in an optional `--config FILE`, else this repository's name (the planner's `Repository`). |

## 5. Dependencies and order

Planner order: `01 -> 02 -> 03 -> 04 -> 05` (critical path `01 -> 02 -> 05`).
It is also the implementation order here:

1. **-01 create** carries the shared infrastructure (model, store, eligibility
   rule, planner merge, validation, dispatch skeleton). Every other member
   depends on the store existing.
2. **-02 show** is read-only over the store plus the planner's analysis (for
   planning states and dependencies). -05 depends on it for the
   active/completed/remaining classification.
3. **-03 add** and **-04 remove** are mutations reusing -01's eligibility,
   locality and provenance paths; they are independent of each other.
4. **-05 checkpoint** reuses -02's classification and the extracted Git
   verification.

## 6. Reusable abstractions (built once)

- `WorkGroups.MemberStatus` (`Unknown | Open of state | Terminal of state`)
  and `WorkGroups.eligibility`: create and add.
- `WorkGroups.locality`: create and add.
- `WorkGroups.mutate` pattern: `validate args -> load store -> pure decision
  -> append history -> atomic write`, one function taking the decision;
  create/add/remove/checkpoint differ only in the decision.
- `WorkGroups.progress`: classification of members into completed, active,
  blocked, remaining, with who each blocked member gates; used by show and
  checkpoint.
- `WorkGroupJson` record codec: one reader and one writer; the planner merge,
  validate, show and every mutation read through it.
- `CheckpointVerification.verifyLocation`: shared with `work checkpoint`.
- One CLI argument parser/renderer (`--json`, `--dry-run`, error codes) for the
  family: exit 0 success, 1 refusal / unknown group, 2 invalid arguments.

## 7. Group checkpoint shape (-05)

```json
{
  "id": "gcp-<hash>",
  "recordedAt": "...",
  "actor": {...},
  "summary": "...", "nextAction": "...",
  "decisions": ["shared architectural decision", ...],
  "members": { "completed": [...], "active": [...], "blocked": [...], "remaining": [...] },
  "memberCheckpoints": [ { "workItemId": "A", "checkpointId": "..." } ],
  "location": { "repository", "branch", "commit", "remote": {name,url}, "remoteBranch", "remoteCommit" },
  "verification": { "status": "verified", "mechanism": "git-remote-observation" }
}
```

It records no paths and no execution attribution: those stay on members' own
checkpoints (no attribution laundering).

## 8. Compatibility

- No change to existing file formats. `groups.json` is new and optional; its
  absence means no stored groups, so the planner output of every existing
  repository is unchanged (planner JSON, fingerprints and the frozen baseline
  documents remain comparable).
- `.ros/` is Praxis state, already non-meaningful for attribution, so group
  commands never create attribution obligations.
- The planner's `DeclaredGroup` and configuration JSON names are reused, not
  renamed.
- `work checkpoint` behaviour is unchanged by extracting its Git verification
  (same rejections, same order); existing checkpoint tests guard it.
- Node's CLI is not extended (F# is the only CLI, `DF-ROS-2026-A030`).

## 9. Common tests (one fixture, one helper set)

One test module `WorkGroupTests` with pure domain tests and CLI tests through
the real binary using the existing `PraxisCli` / `GitFixture` helpers (bare
remote, pushed branch). Shared fixture: a queue with open, captured,
complete and abandoned items and one live active and one blocked item.

- Domain: eligibility (unknown, terminal, duplicate), locality, last-member
  rule, history append, progress classification, validation findings,
  determinism, JSON round trip.
- CLI: create/show/add/remove/checkpoint happy paths and refusals; `--json`
  and `--dry-run` write nothing (file fingerprint unchanged); member lifecycle
  files byte-identical after every group command; `plan groups` reports a
  stored group exactly as the equivalent `--config` declaration; `validate`
  reports a corrupted stored group; group checkpoint refused on unpushed or
  dirty state and recorded on pushed clean state, referencing a member
  checkpoint without altering it.

## 10. Where one design serves several items

- The store and codec serve all five.
- Eligibility + locality serve -01 and -03.
- The history/provenance entry serves -01, -03, -04 and -05.
- Progress classification serves -02 and -05.
- The planner merge makes -01's acceptance ("the planner reads the stored
  declaration") automatically true for groups modified by -03/-04.
- Validation written once covers every state the mutations can produce.

## 11. Risks of solving each item separately

- **Several store models**: e.g. create writes a new file while add patches
  queue items (a `groups` field), and checkpoint writes into
  `current.json`; three places to read, and membership edits touching member
  lifecycle records, violating invariant 1.
- **Divergent member rules**: create refusing terminal items while add only
  checks existence, or two different notions of "terminal".
- **Inconsistent provenance**: add recording an actor, remove recording a
  free-text author, create recording nothing.
- **Duplicated Git verification** in the group checkpoint, drifting from
  `work checkpoint`'s rules (the acceptance requires "the same" verification).
- **Planner blind spots**: only -01 wiring the planner merge, so a group edited
  by add/remove is read differently, or the merge being implemented twice.
- **Conflicting CLI conventions**: different flag names for the group
  (`--group` vs positional), different exit codes for "unknown group".
- **Show and checkpoint disagreeing** on which members are completed.

## 12. Risks of solving them together (and mitigations)

- Shared infrastructure landing under one item could launder attribution:
  it is labelled "shared group infrastructure" in the commit message and
  attributed to `PRAXIS-GROUP-01`, whose acceptance needs all of it; later
  members only add their own verb.
- Context pressure over five items: mitigated by this written plan and a
  checkpoint after each member naming completed and remaining members.
- Over-building for later members inside -01: -01 contains only what its own
  acceptance needs plus the store shape later members append to (history and
  checkpoints arrays exist from the start so the format does not change).

## 13. Shared decisions (for later member executions)

- D1: store is `.ros/work/groups.json`, one record per group, fields named as
  in planner `grouping.groups`.
- D2: one eligibility rule (known, non-terminal) at join time only.
- D3: repository locality from `--config` `grouping.executionRepositories`,
  else this repository; `crossRepository` lifts it.
- D4: every mutation appends a history entry with the resolved actor.
- D5: last-member removal refused unless `--allow-empty` (recorded).
- D6: group checkpoint = shared Git verification + references to members'
  own latest checkpoints; no paths, no execution claims.
- D7: command surface `work group create|show|add|remove|checkpoint`, group
  ID via `--group` (positional for `show`, like `work show ID`), `--json`
  everywhere, `--dry-run` on mutations, exit codes 0/1/2.
