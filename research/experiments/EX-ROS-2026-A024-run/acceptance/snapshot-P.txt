# EX-ROS-2026-A024 acceptance scoring: snapshot-P

**Result: 36 of 36 scorable criteria pass** (36 pass, 0 fail, 0 indeterminate).

## Method

I built `snapshot-P/tree` in a scratch copy with `npm run build:fsharp -- -p:FSharpCoreImplicitPackageVersion=10.1.400` (.NET SDK 10.0.112; the build succeeded). I ran its CLI (`ros-fs.dll --root <fixture>`) against a scratch git repository that has a bare `origin` remote. The fixture holds these work items:

| Item | State |
|---|---|
| FX-A | active |
| FX-B | ready, depends on FX-E |
| FX-C | complete |
| FX-D | abandoned |
| FX-E | blocked ("waiting") |
| FX-X | captured |

"No state change" was checked with content hashes of the fixture before and after each command, and with `cmp` on the backlog, context and event files.

## Criteria

| ID | Status | Evidence (abridged; full text in snapshot-P.json) |
|---|---|---|
| 01.1 | pass | `ros work group create --id GROUP-FX-001 --member FX-A --member FX-E --kind shared-architecture --execution-repository fx --shared-context 'shared ctx one' --architecture-note 'arch note one' --reason why --occurred-at 2026-10-07T11:00:00Z --json IDENT` -> ... |
| 01.2 | pass | `ros work group create --id GROUP-FX-001 --member FX-A --member FX-NOPE ...` -> "ERROR [unknown-member] work item 'FX-NOPE' is not tracked by this repository ... work group create rejected; nothing was recorded", rc 1, H unchanged (cc5097c974f6565e before a... |
| 01.3 | pass | `create ... --member FX-A --member FX-C` -> "ERROR [terminal-member] work item 'FX-C' is complete; a terminal item cannot join a group", rc 1; with FX-D -> "... 'FX-D' is abandoned ...", rc 1; H unchanged in both. |
| 01.4 | pass | After GROUP-FX-001 exists: `create --id GROUP-FX-001 --member FX-B ...` -> "ERROR [duplicate-group] a group 'GROUP-FX-001' is already declared", rc 1, H 5450470a4e8024b5 unchanged; `--json` variant prints {status:"rejected", rejections:[{code:"duplicate-gro... |
| 01.5 | pass | Copies of .ros/work/queue.json, .ros/context/current.json, .ros/events/events.jsonl taken before create; after the successful create `cmp` reports all three byte-identical; `git status --short` shows only `?? .ros/work/groups.json`. Domain: WorkGroups.creat... |
| 01.6 | pass | `ros plan groups --json --as-of 2026-10-07T12:00:00Z` with the stored group, versus a copy of the fixture with groups.json removed and `--config cfg.json` declaring the same group in grouping.groups: python comparison printed "groups equal: True ungrouped e... |
| 01.7 | pass | `create --id GROUP-FX-001 --member FX-A --member FX-E --dry-run IDENT` -> prints the would-be group and "dry run: nothing was recorded", rc 0; H unchanged (cc5097c974f6565e) and `ls .ros/work` shows no groups.json. |
| 01.8 | pass | `create ... --json` emitted a JSON document {command:"work group create", schemaVersion:1, groupId, status:"recorded", group:{...}}; rejection variant emits status "rejected" with coded rejections. |
| 01.9 | pass | Copy fx3: edited groups.json to id 'bad id' and member FX-GHOST; `ros validate` printed "ERROR .ros/work/groups.json:groups.bad id.id: 'bad id' is not a valid group ID" and "ERROR .ros/work/groups.json:groups.bad id.members: member 'FX-GHOST' is not a work ... |
| 02.1 | pass | `ros work group show GROUP-FX-001` -> "FX-A  recorded active; planning active", "FX-E  recorded blocked; planning blocked"; --json members carry recordedState and planningState. |
| 02.2 | pass | show -> "Progress: 0 of 2 complete, 1 active, 1 blocked, 2 remaining ..."; JSON progress {members:2, complete:0, abandoned:0, active:1, blocked:1, remaining:2}. |
| 02.3 | pass | show -> "Blocked members:\n  FX-E (waiting): gates FX-B" (FX-B dependsOn FX-E); JSON blocked [{workItemId FX-E, blockReason waiting, gates [FX-B]}]. |
| 02.4 | pass | show -> "Execution repository: fx"; JSON executionRepository "fx", crossRepository false. |
| 02.5 | pass | show -> "Architecture notes:\n  - arch note one"; JSON architectureNotes ["arch note one"]. |
| 02.6 | pass | Text output shown above for `ros work group show GROUP-FX-001`, rc 0. |
| 02.7 | pass | `ros work group show GROUP-FX-001 --json` -> rc 0, JSON document with command "work group show", progress, members, blocked, latestCheckpoint, history. |
| 02.8 | pass | `ros work group show GROUP-FX-999` -> "ERROR no group 'GROUP-FX-999' is declared", rc 1 (also rc 1 with --json). |
| 02.9 | pass | Full-tree hash over every file and path including .git: before 871469628bd16cfa, after running show (text), show --json and show of an unknown group: 871469628bd16cfa (identical). |
| 03.1 | pass | `ros work group add --id GROUP-FX-001 --member FX-B --reason 'needs it' --occurred-at 2026-10-07T12:01:00Z --actor-kind human --actor adder` -> "FX-B added to GROUP-FX-001 (now 3 member(s)), executing in fx, by human:adder", rc 0; groups.json members now en... |
| 03.2 | pass | `add --member FX-NOPE` -> "ERROR [unknown-member] ...", rc 1, H unchanged (5450470a4e8024b5). |
| 03.3 | pass | `add --member FX-C` -> "[terminal-member] ... is complete"; `--member FX-D` -> "... is abandoned"; rc 1, H unchanged. |
| 03.4 | pass | `add --member FX-A` -> "ERROR [already-member] work item 'FX-A' is already a member of GROUP-FX-001", rc 1, H unchanged. |
| 03.5 | pass | Non-cross group GROUP-FX-001 (fx): `add --member FX-B --execution-repository other-repo` and `add --member FX-B --config cfgrepo.json` (grouping.executionRepositories FX-B: other-repo) both -> "ERROR [repository-mismatch] work item 'FX-B' executes in other-... |
| 03.6 | pass | Cross-repository GROUP-FX-002: `add --member FX-X --execution-repository other-repo` -> "FX-X added ... executing in other-repo", rc 0; `add --member FX-B --config cfgrepo.json --json` -> status recorded, members [(FX-A,fx),(FX-X,other-repo),(FX-B,other-rep... |
| 03.7 | pass | Stored member entry {workItemId FX-B, addedAt 2026-10-07T12:01:00Z, addedBy {kind human, id adder}, reason "needs it"} and history entry {change member-added, subject FX-B, actor human:adder, reason "needs it"}. |
| 03.8 | pass | queue.json, context/current.json and events.jsonl copied before add; `cmp` after add -> all byte-identical; git status shows only ` M .ros/work/groups.json`. |
| 04.1 | pass | `ros work group remove --id GROUP-FX-001 --member FX-E --reason 'split off' ... --actor remover --json` -> status recorded, members [FX-A, FX-B]. |
| 04.2 | pass | `remove --id GROUP-FX-001 --member FX-X` -> "ERROR [not-member] work item 'FX-X' is not a member of GROUP-FX-001", rc 1, H unchanged (ba28a318e5225638). |
| 04.3 | pass | Hash of every .ros file except groups.json/locks (backlog, context, events, telemetry) = ea9f9195f647dfdd before, after removing FX-E, and after removing FX-B and FX-A; `work context` still shows FX-E blocked with its telemetry execution. Code: WorkGroups.r... |
| 04.4 | pass | With FX-A the sole member: `remove --member FX-A` -> "ERROR [last-member] 'FX-A' is the last member of GROUP-FX-001; pass --allow-empty ...", rc 1, state unchanged; with `--allow-empty` -> rc 0, "now no members; it stays declared, and its history is kept". |
| 04.5 | pass | History entry appended: {change member-removed, subject FX-E, occurredAt 2026-10-07T13:01:00Z, actor human:remover, reason "split off"}. |
| 05.1 | pass | `ros work group checkpoint --id GROUP-FX-003 --summary 'shared model done' --next-action 'finish B' --decision 'use one store' --decision 'pure domain' ...` -> rc 0; stored checkpoint has location {branch main, commit 7cbac37..., remoteCommit 7cbac37...}, s... |
| 05.2 | pass | Dirty src/a.txt: group checkpoint -> "ERROR [uncommitted-changes] meaningful uncommitted changes exist ...: src/a.txt", rc 1, state unchanged; `work checkpoint` gives the identical code/message. Unpushed commit: -> "ERROR [local-ahead] local HEAD has 1 comm... |
| 05.3 | pass | Group checkpoint memberCheckpoints [{FX-A: 0e85deb44228ba8176dc0d8a (FX-A's own `work checkpoint` id)}, {FX-B: null}, {FX-E: null}]; later [{FX-A: 0e85...}, {FX-B: ef9bbec2f69a026e998d8960}, ...]. validate flags references that are not that member's recorde... |
| 05.4 | pass | sha256 of events.jsonl+context/current.json before/after a group checkpoint: cee4920145b41aa2 / cee4920145b41aa2; `ros work checkpoint show FX-A` shows Checkpoint 0e85deb44228ba8176dc0d8a before and after. Rewriting a stored group checkpoint is flagged by v... |
| 05.5 | pass | Stored group checkpoint has "paths": [] and the group command writes no member event (event log hash unchanged); text output: "attributed: no path to any member; each member's changes stay attributed by its own checkpoints". Each memberCheckpoints entry ref... |

## Confirmed defects

1. **Adding an item to a cross-repository group with a foreign execution repository silently changes the planner's view of a different, repository-local group that already contains the same item, and validate reports nothing. groups.json then records the item as executing in two different repositories.**

   Reproduction: In fx: GROUP-FX-001 (repository-local, fx) contains FX-B recorded executionRepository fx. `ros work group create --id GROUP-FX-002 --member FX-A --cross-repository ...` then `ros work group add --id GROUP-FX-002 --member FX-B --config cfgrepo.json ...` (or --execution-repository other-repo for FX-X). `ros plan groups --json` now reports GROUP-FX-001 recommendedExecution "split-by-repository" with reason "members execute in different repositories (fx) and the group is not declared cross-repository", although GROUP-FX-001 itself was not changed. `ros validate` gives no groups.* finding. Cause: WorkGroups.declare (src/Ros.Domain/Planning/WorkGroups.fs) adds every stored member's non-local executionRepository to grouping.executionRepositories globally (distinctBy item id), and validate does not check one item's executionRepository consistency across groups. `--execution-repository` on `add` also lets a caller assert any location for an item without corroboration.

2. **`--json` is not honoured on some failure paths: `work group show` of an unknown group and argument/usage errors print text to stderr and nothing on stdout.**

   Reproduction: `ros work group show GROUP-FX-999 --json 2>/dev/null` -> empty stdout, rc 1. `ros work group add --id GROUP-FX-003 --occurred-at 2026-10-07T15:00:00Z --json 2>/dev/null` (missing --member) -> empty stdout, rc 2. By contrast, domain rejections from create/add/remove/checkpoint with --json do produce a JSON {status:"rejected"} document.

## Blinding

Used only this checkout (inputs/ and snapshot-P/) and scratch copies. Never fetched, listed, read, checked out or diffed any other branch, tag or ref, and used no GitHub search, API or web pages. I did not try to work out which experimental condition produced this snapshot and have not learned it. The branch name gives only the snapshot label, and commits.txt gives five per-item commit subjects, which carry no condition information. Snapshot's own unit suite (Ros.Tests.dll): 819 tests, 774 pass, 45 fail; 44 failures (including all 9 'work group cli' tests) fail at fixture setup with 'starter manifest references a missing source file: starter/greenfield/[record].md', and the 45th is 'the embedded scaffold is exactly what the starter manifests reference' (missing schemas/ros-[record]-authority.schema.json, starter/greenfield/[record].md). This looks like an artifact of how the evaluation package was sanitized (redacted file names), not snapshot behaviour, so it is not listed as a defect; the 45 'work group' domain/unit tests that do not need that scaffold pass.
