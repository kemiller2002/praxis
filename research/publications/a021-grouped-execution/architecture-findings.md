# A021 architectural evidence audit

This audit applies one rubric, the same way, to the four implementations of the work-group cohort (`PRAXIS-GROUP-01..05`, `praxis work group create|show|add|remove|checkpoint`) from baseline `8b4ffa392e93b19bf39f6672a608954c934cb815`. It reads the code diffs against the baseline and runs a small set of CLI probes. It tests the claim that grouped execution produced substantially more unified cross-item architecture in both executions, and it tries to falsify that claim.

Machine-readable form: [`data/architecture-findings.json`](data/architecture-findings.json). Validator: [`scripts/check_architecture_findings.py`](scripts/check_architecture_findings.py). The validator checks the schema and canonical formatting. It also checks that every cited commit exists, that every cited symbol (code evidence and `spec_refs`) appears inside its cited line range, and that every probe claim matches the raw probe outputs in [`data/probes/`](data/probes/), which [`scripts/run_probes.py`](scripts/run_probes.py) produces. Result: 32 findings, 16 comparisons, 112 Git citations and 44 probe claims verified.

## Implementations (identities verified)

| ID | Branch and head | Blind alias | Identity check |
|---|---|---|---|
| A021-grouped | `experiment/a021-grouped` `5face886…` | arm X `9c978cd…` | `src/`, `tests/` and `docs/` tree hashes are identical (X adds only a commit that removes the experiment directory). |
| A021-independent | `experiment/a021-control` `8ea9be22…` | arm Y `5d100ed…` | Same tree hashes for `src/`, `tests/` and `docs/`. |
| R2-grouped | `experiment/a021-r2-grouped` `0b5284c3…` | arm N `f6d0b5c…` | Same tree hashes for `src/`, `tests/` and `docs/`. |
| R2-independent | `experiment/a021-r2-control` `8bb8f78a…` | arm M `72a6c04…` | Same tree hashes for `src/`, `tests/` and `docs/`. |

These checks confirm the mappings in EV-A064 (X = grouped, Y = independent) and in POST-UNBLINDING-METRICS (N = grouped, M = independent).

## Rubric and categories

The rubric has eight dimensions:

- **D1** persistence and store model
- **D2** member admission and the repository rule (create vs add)
- **D3** lifecycle and member classification (show vs checkpoint)
- **D4** checkpoint durability and ownership validation
- **D5** the error, JSON and command contract
- **D6** the mutation and concurrency model
- **D7** reuse of baseline rules and abstractions. Feature-internal shared helpers (one parser, pipeline or renderer) are scored under D5 and D6, so D7 does not count them a second time.
- **D8** duplicated incompatible abstractions inside the feature

Each dimension gets one of these categories:

- **unified**: one implementation or shared rule serves every item that needs the concern, and every verb shows the same behavior on the same input.
- **duplicated-compatible**: more than one implementation or representation exists, but no input the CLI can reach produces contradictory behavior. The difference is in organization, naming, file layout or wording.
- **divergent**: more than one implementation exists, or one shared input is derived differently, and concrete code or runtime evidence shows different behavior or invariants for the same input. A difference in organization alone never qualifies.
- **missing**: the concern is not implemented where the items need it.
- **not-assessable**: the concern cannot be judged at this commit, for example when the sensitivity analysis excludes the item that would exercise it.

Every finding is recorded with one of three bases: **observed** (verified in code or by a probe here), **evaluated** (an evaluator's judgment, cited) or **inferred** (this audit's own inference).

## What was executed

- **Builds.** `dotnet build src/Ros.Cli/Ros.Cli.fsproj -c Release -p:FSharpCoreImplicitPackageVersion=10.1.400` with SDK 10.0.112, in detached worktrees of all four heads. Each build finished with 0 warnings and 0 errors.
- **Tests.** The test suites were not run here. Test counts are the evaluators' figures.
- **Probes.** [`scripts/run_probes.py`](scripts/run_probes.py) runs every probe. It runs each arm's own `ros-fs.dll` against a throwaway local clone of the arm's head, with a fixed `--occurred-at` and a fixed actor. Normalized raw outputs are in `data/probes/<implementation>.json`; they contain no machine paths, and operational-failure reference IDs are replaced. P1–P10 are deterministic: a second run on R2-independent produced byte-identical probe records. The concurrent-create probe (CC) is not deterministic, so it records per-run counts over 5 runs. `check_architecture_findings.py` verifies every probe-based claim (`probe_claims`, 44 claims) against these files. The probes use four captured items:
  - `PRAXIS-PROBE-01`, `-03` and `-04`: described as "local work";
  - `PRAXIS-PROBE-02`: described as "Implemented in an external repository", which the baseline planner's `executionLocation` infers as `UnknownExternal`.

  A021-grouped takes `--group`; the other arms take `--id`.

| Probe | A021-G | A021-I | R2-G | R2-I |
|---|---|---|---|---|
| P1 create local group {01,03} | 0 | 0 | 0 | 0 |
| P2 add external-by-description 02 to it | **0 (accepted)** | 1 (refused) | **0 (accepted)** | 1 (refused) |
| P3 create {01,02} | 0 | 0 | 0 | 0 |
| P4 create with ID `bad-id` (`--json`) | 2, coded | 1, `kind: work-group-create` | 2, coded with remedy | 1, bare string |
| P5 add to an undeclared group | 1 | 1 | 1 | 1 |
| P6 checkpoint an undeclared group | 1 | **2** | 1 | 1 |
| P7 show an undeclared group with `--json` | 1, JSON | 1, JSON plus stderr | 1, JSON | 1, **no JSON** (planner error text on stderr) |
| P8 create with an unknown member (`--json`) | 1, code `unknown-member` | 1, `kind: work-group-create` | 1, code `unknown-work-item` | 1, bare string in `errors` |
| P9 create `--execution-repository elsewhere` {01,03} | 1 (`repository-mismatch`) | **0** | 1 | **0** |
| P10 add local 04 to that group | 1, but only as unknown group (P9 created nothing) | 1 (`repository-mismatch`) | 1, unknown group | 1 (`repository-mismatch`) |
| CC: 5 runs x 8 concurrent creates; exit 0 / groups stored / lost updates (totals) | 17 / 17 / 0 | **40 / 8 / 32** | 31 / 31 / 0 | **40 / 7 / 33** |

**Scope of P2 and P3.** P2 tests add only. At create, P3 shows that all four arms accept the item the planner infers to be external, the independent arms included.

**CC.** Per-run counts vary between executions:

- **Independent arms:** every process exits 0 and only 1–2 groups are stored per run.
- **Grouped arms:** every exit-0 run is stored, and the remaining processes exit 1 with "unexpected operational failure". That comes from contention on the pre-existing `RegistryLock`, as in EV2: 23 of 40 runs fail in A021-grouped and 9 of 40 in R2-grouped.

The claim is the presence or absence of lost updates, not the exact counts. A single earlier run gave 5/5, 8/1, 6/6 and 8/1.

## Comparison matrix (8 dimensions x 2 studies x 2 arms)

Category columns show grouped / independent. "Beh." marks a behavioral difference between the arms.

| Dim | A021 categories | A021 direction | Beh. | Conf. | R2 categories | R2 direction | Beh. | Conf. |
|---|---|---|---|---|---|---|---|---|
| D1 store | unified / duplicated-compatible | grouped | no | high | unified / duplicated-compatible | grouped | no | high |
| D2 admission | unified / divergent | grouped | yes | high | unified / divergent | grouped | yes | high |
| D3 classification | unified / divergent | grouped | yes | medium | divergent / divergent | equivalent | no | medium |
| D4 checkpoint validation | missing / unified | **independent** | yes | high | missing / unified | **independent** | yes | high |
| D5 contract | unified / divergent | grouped | yes | high | unified / divergent | grouped | yes | medium |
| D6 concurrency | unified / divergent | grouped | yes | high | unified / divergent | grouped | yes | high |
| D7 baseline reuse | divergent / unified | **independent** | yes | high | divergent / unified | **independent** | yes | high |
| D8 incompatible duplicates | unified / duplicated-compatible | grouped | no | medium | unified / unified | equivalent | no | high |

Counts:

- **A021:** grouped is more unified on 6 dimensions, 4 of them with a behavioral difference (D2, D3, D5, D6). Independent is better on 2 (D4, D7).
- **R2:** grouped is more unified on 4 dimensions, 3 of them behavioral (D2, D5, D6). Independent is better on 2 (D4, D7). D3 and D8 are equivalent.

**D7 relabelled from "mixed" (follow-up round).** The evidence on baseline reuse points one way. The independent arms extracted and reused the `grouping.groups` reader, `Grouping.executionLocation`, `effectiveStatus` and (in A021) the event log and journal. The grouped arms re-derived the location rule, and P2 shows what that costs. The only grouped advantage the earlier "mixed" label rested on was feature-internal helpers (one parser, pipeline and renderer), which D5 and D6 already score. Counting them again under D7 would double-count, so D7 is scored for independent in both studies.

## Dimension notes (evidence in the JSON)

- **D1.** A021-independent's "three stores" are two new group files plus the baseline event log. The event log is a deliberate reuse for checkpoints that EV1 itself credits. Under sequential use, `validate` reconciles the ledger and the group (`GroupMembership.findings`). The behavioral cost appears only under concurrency, which D6 scores. R2-independent keeps membership provenance inside `groups.json` and puts checkpoints in a second file. That is separation of concepts, as the R2 evaluator said (M-F10).
- **D2.** Both grouped arms have one admission rule, used by create and add (`eligibility` / `admit`). Both re-derive the execution repository without the planner's `UnknownExternal` inference, so the add in P2 is accepted. Both independent arms check the repository only at add (introduced by item 03 in each study), so the create in P9 is accepted and the add in P10 is refused. No arm enforces the planner's inferred location at create (P3).
  - **Specification.** Every PRAXIS-GROUP item cites `requirements/PLANNING-WORK-GROUPS.md`. At `8b4ffa3`, lines 114–118 of that document hold PRX-GRP-051: "Every group MUST identify where implementation occurs. A group containing work from different repositories MUST be explicitly cross-repository or be split into repository-local groups."
  - **Independent arms.** The create-time omission is therefore a violation of a cited requirement, not literal compliance with the item text. R2's blind evaluator counted exactly this as confirmed defect M-D2, and counted the stored `ExecutionRepository = None` as M-D1. A021-independent's create has the same two gaps; neither A021 evaluator scored them against PRX-GRP-051.
  - **Grouped arms.** Their general rule is what PRX-GRP-051 requires, not a generalization beyond the specification, and both always store an execution repository. But they implement the rule without the planner's external inference, so an item the planner treats as another repository's work joins a local group: at add (P2) and at create (P3). That falls short of the requirement's second sentence.
  - `spec_refs` in the JSON cite the requirement text.
- **D3.** A021-grouped is genuinely unified. `WorkGroups.progress` serves show and checkpoint, and both inputs reduce to `QueuePresentation.effectiveStatus`. In R2, both arms' show and checkpoint disagree for abandoned members and for blocked members:
  - R2-grouped's checkpoint reuses show's view, but takes completed members from `RecordedState` and remaining members from planner `Status`, so an abandoned member appears in no list. Meanwhile show counts it as complete.
  - R2-independent's show uses planner status, and its checkpoint uses `effectiveStatus` with an abandoned list of its own.
- **D4.** In both studies the independent checkpoint applies the ownership half of `work checkpoint` (an active member and the caller's own execution), rejects blank decisions and re-validates stored checkpoints. Both grouped checkpoints share only the Git durability rule. A021-grouped also stores a blank `--decision` verbatim (`WorkGroups.fs:546`), which neither A021 evaluator reported.
- **D5.** Grouped is uniform in both studies. A021-independent mixes two envelope families, three rejection shapes and inconsistent exit codes (P4, P6). R2-independent has one parser for four verbs and one envelope family, and its unknown-group exit codes are consistent. Its create, add and remove rejections are uncoded strings that exit 1, and its show has no JSON on not-found (P7).
- **D6.** Both independent arms have unlocked read-modify-write for create, add and remove. Here this is a runtime-observed lost update: over 5 CC runs, 40 of 40 processes exit 0 and only 8 (A021) and 7 (R2) groups are stored. Both grouped arms serialize every group write. A021-grouped uses its own `work-groups` lock, which does not serialize with `work checkpoint`. R2-grouped reads member facts before taking the `work-protocol` lock.
- **D7.** In both studies the independent arms reuse more baseline rules:
  - the `grouping.groups` reader;
  - `Grouping.executionLocation`;
  - `effectiveStatus`;
  - in A021, the event log and journal.

  The grouped arms re-derive the location rule, which causes P2, and R2-grouped also re-derives terminal standing. The grouped arms' feature-internal shared helpers (one pipeline, one codec) are scored under D5 and D6, not here. Direction: independent in both studies.
- **D8.** A021-independent's second group-ID grammar (`^[A-Za-z0-9][A-Za-z0-9._-]*$`) is a strict superset of the first. It is applied only after a group has been resolved from stored declarations or from planner configuration, whose IDs the baseline does not constrain. That makes it duplicated, but no incompatibility is shown. In R2, each arm has a single grammar.

## Stale-start sensitivity

**A021-independent, item 05.** Commit `8d435cb` has `8b4ffa3` as its parent, so the item started from the arm start rather than the branch head. The following came from that item:

- the checkpoint classification (D3);
- the second group-ID grammar (D8);
- the `{command, schemaVersion}` checkpoint envelope and exit 2 for an undeclared group (part of D5);
- the separate checkpoint parser;
- the arm's whole D4 advantage.

What changes when item 05 is excluded:

- **D3 and D8:** the independent divergences disappear, so these dimensions become not-assessable or unified.
- **D4:** the independent advantage disappears too, so this sensitivity analysis is symmetric.
- **D2, D5, D6 and the D1 organization difference:** these survive. Create (`51086ac`) emits string rejections and takes no lock. Add and remove (`13e26f4`, `881a34d`) emit coded rejections, add the second store and apply the repository check only at add.

So EV-A064's statement that the finding holds without item 05 is upheld for D2, D5, D6 and D1, but not for D3 or D8.

**R2-independent, items 02 and 03.** Both started from stale checkouts:

- **Item 02** (`962b1f4`, stale) designed show as a planner view (`Grouping.show` in `PlanCommands`). That design plausibly caused the show-specific contract gaps (P7, planner schema) and the show half of D3. Excluding item 02, D5 still diverges: create, add and remove rejections are uncoded, checkpoint rejections are coded.
- **Item 03** (`b05fd8b`, stale) merged items 01 and 02 (`5a03452`) before its implementation commit `dae9788`, so it could see create's code. Staleness is therefore an implausible cause of the D2 divergence.
- **D1, D4 and D6** come from items 01 and 05, which started correctly.

Overall, the R2 divergences that matter (D2, D5, D6) do not depend on the stale items.

## Where this audit disagrees with the evaluators or the summaries

1. **R2 evaluator, N-F01, N-F03 and section 10 ("both reuse ... planner Grouping"); POST-UNBLINDING ("execution-repository ... enforcement").** Refuted in part. R2-grouped's `memberFacts` (`WorkGroups.fs:272-296`) ignores the planner's `UnknownExternal` inference. P2 adds an external-by-description item to a local group with exit 0, and R2-independent refuses it. This replicates EV1's X-1 finding for A021-grouped. The R2 evaluator missed it, so the A021 finding that "independent reused more baseline rules" also replicates in R2, which EV-A070 does not say.
2. **R2 evaluator, N-F09, and R2-grouped's own comment ("a membership decision cannot race a lifecycle transition").** Not supported. Member facts and the planner view are read before the lock (`WorkGroupCommands.fs:194-210, 266-269, 410-422`). Lost-update protection holds.
3. **R2 evaluator, M-R1 (design risk only).** Refuted as a design-risk-only claim. The lost update is observed at runtime: CC had 40 of 40 processes exit 0 and 7 groups stored over 5 runs. The evaluator's concern is thereby strengthened.
4. **R2 evaluator, section 10 ("N checkpoint reuses show ... mostly cohesive").** Weakened. Show and checkpoint disagree for abandoned members (`WorkGroups.fs:467, 495, 630`), so R2-grouped is behaviorally divergent on D3, as R2-independent is.
5. **R2 evaluator ("M create/add/remove ... call separate application operations").** Overstated. One parser, one Application module (`WorkGroupOperations`) and one port serve all three.
6. **EV1 and EV-A064, "three stores".** Counting inflation: two of the three are new group stores, and the third is the reused baseline event log.
7. **EV1, "two group-ID rules" read as an incompatibility, and "three member classifications" vs one.** The grammars are nested and applied to different domains, and they come from stale item 05. A021-grouped also has two classifications (admission standing and progress category). The substantive difference is show vs checkpoint only.
8. **EV1 and EV2, omission.** A021-grouped accepts and stores a blank `--decision` (`WorkGroups.fs:546`). A021-independent rejects it, and R2's N-D1 is the same class of defect.
9. **Any reading that independent's D2 asymmetry "follows the acceptance criteria literally".** Contradicted by the cited PRX-GRP-051 and by R2's M-D2. See the D2 note.
10. **EV-A070 and POST-UNBLINDING, "R2 reproduces that qualitative pattern".** Partly upheld:
   - **Replicated:** D2, D5, D6, D1 (as organization only) and the independent advantages on D4 and D7.
   - **Not replicated:** D3, now equivalent; D8, now equivalent.
   - **Differently sized:** R2-independent is far less fragmented on the command surface than A021-independent.

## Where independent execution was better (both studies)

- **Checkpoint ownership and validation depth (D4):** active member, own execution, blank-decision rejection and re-validation of member checkpoint references.
- **Reuse of baseline rules (D7):** the planner's location rule (P2), the declaration reader, `effectiveStatus`, and in A021 the event log and journal.
- **Test breadth (evaluated):** 37 vs 19 tests in A021, and 47 vs 27 in R2 (from 839 and 819 tests against the 792-test baseline).
- **Abandoned members:** R2-independent's checkpoint keeps abandoned members explicit.

## Falsification results

| Claim | Result |
|---|---|
| A021-independent used "three stores" | weakened |
| Two group-ID grammars form an incompatible duplication | weakened (stale item 05) |
| "Three member classifications" vs one | weakened |
| Grouped arms' single join rule enforces the repository invariant | weakened (it is PRX-GRP-051's required rule, but without the planner's external inference; P2 at add, P3 at create) |
| Independent create/add asymmetry is literal compliance with the item text | refuted (PRX-GRP-051; R2 M-D2) |
| R2-grouped enforces repository compatibility | refuted (P2) |
| R2-grouped mutations cannot race lifecycle transitions | weakened |
| R2-independent lost update is only a design risk | refuted (observed defect; CC 40/40 exit 0, 7 stored) |
| Grouped show and checkpoint share one classification in R2 | refuted |
| Grouped arms have no internal inconsistencies the evaluators missed | refuted (blank decisions in A021-grouped; location, terminal rule, show/checkpoint and facts outside the lock in R2-grouped) |
| EV-A064: the finding holds without control item 05 | upheld for D1, D2, D5, D6; not for D3, D8 |
| A reviewer would see the R2 difference as smaller than A021's | upheld |
| Grouped produced *substantially* more unified cross-item architecture in both executions | weakened: fits A021, overstated for R2 |

## Verdict

**A021.** The finding is moderately strong. Grouped is more unified on six dimensions, and four of them show concrete behavioral differences:

- create/add admission, the error and exit-code contract, and locking. The probes reproduce these three (P2–P10, CC).
- show/checkpoint classification. This one comes from code reading only; no probe exercises D3.

The independent create-time omission behind the D2 difference also violates the cited PRX-GRP-051. The other two (store layout and duplicate grammars) are organizational. Some evaluator counts are inflated: the "three stores", the "two grammars" and the "three classifications". Two of the six dimensions (D3, D8) come from the independent arm's stale-start item 05, and the independent arm's own D4 advantage comes from that item as well. Without item 05, the A021 contrast rests on D2, D5 and D6 plus store organization. That is still a real cohesion difference, but a narrower one than EV-A064 states.

**R2.** The direction replicates, but the margin is clearly smaller. Grouped is behaviorally more unified on three dimensions: one admission rule (D2), one coded error contract (D5) and locked writes (D6). The independent lost update is now observed at runtime. Store layout is the only other grouped advantage, and it is organizational. Show/checkpoint classification and group-ID grammar are equivalent. Independent is again better on checkpoint ownership validation (D4) and on reuse of baseline rules (D7). On D2, the independent asymmetry violates PRX-GRP-051 (M-D2), while the grouped rule is the required one without the planner's external inference. The R2 evaluator missed that the grouped arm repeats A021-grouped's location-rule gap. A reasonable reviewer would read R2 as a weak-to-moderate replication of "more unified cross-item invariants and mutation infrastructure". It is not "substantially more unified architecture", and the paper should state the counter-findings (D4, D7) with equal prominence in both studies.

## Reproduce

```sh
python3 research/publications/a021-grouped-execution/scripts/check_architecture_findings.py
```

To repeat the probes, build each head as above, then run, for example:

```sh
python3 research/publications/a021-grouped-execution/scripts/run_probes.py --impl A021-grouped \
  --cli <worktree>/src/Ros.Cli/bin/Release/net10.0/ros-fs.dll --source <worktree> \
  --scratch <scratch-dir> --out research/publications/a021-grouped-execution/data/probes/A021-grouped.json \
  --group-flag=--group --head 5face886176839da30805c07fa893d2c18f3b5d2
```

The other arms use the default `--group-flag=--id`. In the anonymous bundle, which ships no Git history, run the checker with `--no-git`: the commit and line checks are skipped, and every probe claim is still verified against `data/probes/`.
