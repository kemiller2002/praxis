# A021 and R2: protocol and validity audit

Audit of `EX-ROS-2026-A021` (grouped versus independent execution of `PRAXIS-GROUP-01..05`) and its R2 same-setup re-execution, written as a hostile SANER/MSR reviewer would. The machine-readable threat list is `data/threats.json` (29 threats, sorted by severity, deterministic JSON).

Audited on 2026-10-07 from Git objects, branch history and GitHub's server-side push log (`gh api repos/kemiller2002/praxis/activity`). The working clone was shallow and had to be unshallowed to resolve `2d0ec5c`. Session transcripts are not in the repository, so every claim about what a session read or did comes from that session's own self-report.

## Severity scale

| Severity | Meaning |
| --- | --- |
| fatal | Invalidates a claim or metric *as currently stated*. Re-analysing the existing data cannot repair it. The claim has to be dropped or reworded, or new data collected. |
| major-manageable | Invalidates or materially biases a headline figure or conclusion. Re-analysis, sensitivity figures or explicit reframing with existing data can repair it. |
| moderate | Biases or weakens a result. It must be disclosed, but it does not change the headline. |
| minor | A record-keeping or precision issue. |
| informational | Checked and found adequate. Reported so readers can see it was checked. |

## Fatal threats

### T01. The treatment is a bundle, not "shared context"

The grouped prompt (`EX-ROS-2026-A021-harness/prompts/grouped.txt`) differs from the control prompts in five ways, not one:

1. one session for all five items;
2. a mandatory read of `./ros plan explain-group` with the experiment's planner configuration;
3. a **committed, pre-coding cross-item design document** covering PRX-GRP-040: "common architecture, shared invariants, ... reusable abstractions, ... where one design can serve several items, and the risks of solving each separately";
4. an instruction to label shared changes as "shared group infrastructure";
5. milestone checkpoints that name the completed and remaining members.

The control prompts never say the items form a group, never show the planner's group explanation, and never ask for consistency across items.

R2 widened the gap. Its grouped analysis (`experiment/a021-r2-grouped` `e604b81`, 392 lines, 20 sections) adds Persistence strategy, Validation strategy, JSON and output contracts, Failure and exit-code conventions, and "Anticipated duplication if implemented separately". Those are exactly the dimensions on which the R2 evaluator later found the grouped arm stronger (see T08).

**Invalidates:** attributing the architectural-consistency difference to *shared context*, the working title "Does Shared Agent Context Reduce Architectural Drift?", and any claim that PGEI was tested as distinct from instructed upfront design.

The existing data cannot separate the components: no arm has shared context without the design step, or the design step without shared context. The finding survives only for the bundle: "one session plus a mandated cross-item design analysis" versus "uninstructed per-item sessions".

### T02. Neither A021 quality evaluation was blind

The primary evaluator received full branches rather than the "patch series exported under neutral names" the protocol requires.

- `experiment/a021-arm-x` (`9c978cd`) has the grouped head `5face88` as its parent. Its history contains `99592467` "... EX-ROS-2026-A021 **grouped-arm** session metrics" and `9e8fc2e4` "group analysis for GROUP-PRAXIS-WORK-GROUP-001 (PRX-GRP-040)".
- The blinding commits themselves delete `research/experiments/EX-ROS-2026-A021-grouped/...` on arm-x (`9c978cd`) and `.../EX-ROS-2026-A021-control/metrics/...` on arm-y (`5d100ed`). The tip commit of each "blind" branch names its arm.
- The evaluator states that it read them: "Commit messages and committed Praxis state on both arms name experiment labels. I read them" (`EX-ROS-2026-A021-evaluation/evaluation.txt`, lines 26-35). It also used the merge commit `1aa11fb`, which exists only in arm Y.
- The second evaluator (`EV-ROS-2026-A067`, section 3) reports "I believe I know the mapping". It saw the leaking strings at steps 1-2, before reading any code.

**Invalidates:** the description of A021's evaluation as blind, the "two blinded evaluations" framing, and the evaluators' subjective judgements (which difference "matters most", whether gaps are "small to close") as blind evidence.

**Survives:** the objective structural facts, which anyone can re-verify from the code: number of stores, join rules, rejection types, ID grammars, exit codes, tests added, and the confirmed behavioural defects.

## Threat list (sorted by severity)

| ID | Sev. | Study | Threat | One-line justification |
| --- | --- | --- | --- | --- |
| T01 | fatal | both | Treatment bundle | Only the grouped arm was told to design for cross-item consistency, so "shared context" cannot be isolated. |
| T02 | fatal | A021 | A021 evaluations not blind | Arm names were in the commit subjects and deletion paths the evaluator read; the second evaluator self-reported knowing the mapping. |
| T03 | major | R2 | 58.72% counts a control-only orchestrator | Without session C ($3.49) the cost reduction is 52.33%. A021 counted no orchestrator. |
| T04 | major | both | Elapsed-time definition drift | R2's 61.08% includes 43.0 min of orchestration gaps; on summed session time it is 44.4%. A021's primary 45% is a different quantity. |
| T05 | major | both | Stale checkouts hit only control | 4 of 10 control sessions started stale (A021 02 and 05, R2 C2 and C3). A single grouped session cannot be affected. |
| T06 | major | both | n = 1 per cell | Two cells and two runs; the items are dependent; R2 is a correlated resample (the A021 and R2 grouped arms changed the same 14 files). |
| T07 | major | R2 | R2 not preregistered; prompts not archived | No R2 protocol existed before its branches were created (15:21Z). Prompts differ from A021's (deviations O2, E2) and are in no ref. Metrics were defined after unblinding. |
| T08 | major | R2 | Designer knew interim A021 outcomes | R2 launched after A021's grouped arm and control items 01-03 had finished. The R2 analysis headings match the dimensions the evaluator scored. |
| T09 | major | R2 | R2 blinding was procedural only | The evaluator's GitHub connector could reach the arm branches (mapping recoverable by tree identity) and `main`, which held EV-A064 and the merged grouped-derived implementation. |
| T12 | major | A021 | Outcome class contradicts the falsification rule | HY-A028 said "weakened if ... loses quality". The control arm met criteria more faithfully, yet A021 was classed "clearly beneficial". |
| T21 | major | both | Post hoc mechanism | PGEI was formed from A021 data during the arms (`7385838b`, `3aef2bd5`); HY-A029 was created after R2 unblinding. |
| T10 | moderate | R2 | Evaluator = unblinder = analyst = hypothesis updater | GPT-5.6 Sol evaluated, unblinded, computed ratios and updated HY-A028. |
| T11 | moderate | both | Isolation never audited | The protocol's event-log check was not done in either study. Text overlap is low (0.18), but reading the other arm cannot be excluded. |
| T13 | moderate | A021 | Failed attempts in control cost and time | [Lead correction 2026-10-07: attempt 2 implemented and pushed item 04 before blocking, so excluding it was wrong. The corrected S1 excludes only the stalled attempt 1: cost reduction about 52% and summed session time about 39% (macros `\AcostExclStalledReduction`, `\AactiveExclStalledReduction`, computed by `build_tables.py`). The superseded figures were 46.4% / 30.7%.] Permissions changed mid-arm. |
| T14 | moderate | both | Toolchain setup scales with sessions | Each R2 worker re-derived the .NET workaround. R2's harness note was weaker than A021's. |
| T15 | moderate | both | Design-entailed metrics | Prompts say "following AGENTS.md", so reading it once per session is expected. Transcript metrics are lower bounds and miss the control-04 implementation. |
| T17 | moderate | both | Acceptance-verification asymmetry | R2 ran no evaluator fixture or local build and relied on the arms' own tests. Test counts are not quality. |
| T18 | moderate | A021 | Same-family evaluator; rubric foregrounds the target | The first A021 evaluator ran on the implementers' model (sessions.json). [Lead correction: the second (kit) evaluator's model is not recorded; only the first is known to share the implementers' model.] Rubric item 3 asks for "one model of a declared group". |
| T19 | moderate | both | Role overlap | The cohort author, designer, orchestrator and analyst are one lineage. The owner authored PGEI mid-experiment. |
| T20 | moderate | both | External validity | One repository, feature family, model and platform. A022 is blocked (EV-A072). |
| T24 | moderate | A021 | Unrecorded deviations | Control-02 stale clone, branch-instead-of-patch evaluation input, no isolation audit, no step telemetry in the control arm. |
| T16 | minor | both | Cross-record inconsistency | The R2 records say A021 platform cost was "unavailable" and quote script tokens (176,304) as if they were platform figures. |
| T22 | minor | both | Concurrency and platform events | The arms ran concurrently and R2 overlapped A021. Two A021 workers restarted (`worker_epoch` 2). |
| T23 | minor | both | Unverifiable randomisation | A021's "drawnAt" is later than the branch creation it governed. The R2 commitment salt is lost. The mapping itself is independently verified. |
| T26 | minor | both | Lifecycle and attribution not blind-evaluated in R2 | `.ros` was reset in the blind trees; only the orchestrator checked lifecycle. |
| T25 | info | both | Grouped analysis cost counted, analysis not evaluated | The analysis cost is inside the grouped totals, which is conservative for resource claims. |
| T27 | info | both | Baseline and criteria equivalence | Verified: all arms branch from `8b4ffa3`, and the R2 SHA256SUMS check passes. |
| T28 | info | both | Model identity | claude-opus-5-5 is configured and last-served in every implementing session. |
| T29 | info | A021 | Protocol freeze | Frozen at `370e0a4` (pushed 11:18:15Z). Arms branched at 11:46:20Z. No later edits to method, prompts or predictions. `2d0ec5c` is the planner code commit. |

## Notes on the major threats

**T03 and T04: resource arithmetic.** Recomputed from `EX-ROS-2026-A021-R2-run.txt` section 4 (`claude/a021-r2-orchestration-e0gty2` `063e6b58`) and `sessions.json`:

| Figure | As reported | Sensitivity |
| --- | --- | --- |
| R2 cost reduction | 58.72% (includes control orchestrator C) | 52.33% (implementing workers only) |
| R2 time reduction | 61.08% (C start to C last update, 143.55 min) | 44.42% (sum of C1-C5, 100.55 min; 43.0 min of gaps) |
| R2 output-token ratio | 1.978x | 1.781x (workers only) |
| A021 cost reduction | 55.95% | about 52% excluding only the stalled attempt 1 (corrected; the earlier 46.39% wrongly excluded the implementing attempt 2) |
| A021 session-time reduction | 45.07% (61.18 vs 111.37 min) | about 39% excluding only the stalled attempt 1 (corrected; earlier 30.71%) |
| A021 first-start to last-finish | 79.06% | includes a 2 h permission block |

Neither study counts the top-level orchestrator: A021 `session_01E8aLYDesB49uhTD6Mpg4yo`, R2 `session_0186wu2wQu7VnhTJQnVPeQuc`. The control arm needed more orchestration in both studies: seven session creations and a permission approval in A021, five worker hand-offs in R2. The direction (grouped cheaper and faster) survives every sensitivity row. The magnitudes do not survive as stated.

**T05: stale checkouts.** `EV-ROS-2026-A064` says "items 02-04 started from the correct heads". `EV-ROS-2026-A066` contradicts this: control-02 began on a stale clone, and its `.ros` state was reconciled by hand. The full list:

- A021 control-02: stale clone, Praxis state only;
- A021 control-05: code built on `8b4ffa3`, followed by merge conflict `1aa11fb`;
- R2 C2: `962b1f4`, then merge `fa821cc`;
- R2 C3: `b05fd8b`, then merge `5a03452`.

[Lead correction: the cause of the stale starts is unknown; calling the hazard "structural" is not supported.] A single grouped session cannot start stale between items, so the hazard, whatever its cause, can affect only the per-item arms. EV-A064 shows that the A021 consistency finding survives without item 05, because item 03 alone added a second store. R2 has no equivalent analysis excluding GROUP-02.

**T08 and T07: R2 independence.**

- The R2 arms were created at 15:21Z on 2026-09-30, after A021's grouped arm (12:51Z) and control items 01-03 (13:05Z) had finished.
- The owner's PGEI observation was committed at 16:14Z (`3aef2bd5`) and drew on that interim data.
- The R2 prompts were "fuller R2 instructions given by the repository owner" (deviation O2) and are not archived anywhere.

R2 therefore cannot be presented as an independent, preregistered replication. It is a same-setup re-execution designed by someone who knew the interim outcome.

**T09: R2 blinding.** What protected the R2 blinding:

- squashed neutral commits;
- `.ros` reset to the baseline;
- no arm-label strings in the blind diffs (checked);
- a different model family for the evaluator;
- a server-side push order: evaluation `bf8baec` pushed 2026-10-01T06:00:42Z, unblinding `6b53851` pushed 10:27:51Z;
- identical `EVALUATION.txt` and `findings.json` blobs at `bf8baec`, HEAD and `origin/main`.

What did not: the evaluator could reach the mapping at any time. Its arm-N tree is identical to `experiment/a021-r2-grouped` and arm-M to `experiment/a021-r2-control` on `src/`, `tests/` and `docs/` (0 differing files; this is how the mapping was later recovered). `main` already held `EV-ROS-2026-A064`, which says the grouped arm has "one store" (PR #132, 17:55Z on 9-30), and the phase-two implementation built on the A021 grouped arm (PR #134, `da83cd24`). The kit README forbids only EV-A060..A062 and the arm branches. Compliance rests entirely on the evaluator's self-report.

**T12: classification.** HY-A028's preregistered falsification includes "or loses quality". A021's quality evidence is mixed:

- The control arm reused the planner's parser and `executionLocation`, verified checkpoints more fully and wrote more tests.
- The grouped arm's `add` accepts an external item (confirmed defect).
- The control arm has an unlocked-store data-loss defect (A067).

"Clearly beneficial" overstates this. The defensible class is: saves repeated context and cost, improves cross-item consistency, per-criterion quality mixed. The hypothesis is also disjunctive ("reduces repeated work, improves consistency, or both"), so it is weakly falsifiable.

## Protocol deviations register

| # | Study | Deviation | Recorded in | Recorded at the time? |
| --- | --- | --- | --- | --- |
| A1 | A021 | Identical harness note added to every prompt: toolchain, timestamps, metrics script, no PRs | `sessions.json`; protocol Results | yes |
| A2 | A021 | Control-04 attempt 1 stalled with nothing pushed ($1.85) | `sessions.json` | yes |
| A3 | A021 | Control-04 attempt 2 pushed `881a34d`, then blocked on a `./ros work` permission prompt and was abandoned after about 2 h | `sessions.json` | yes |
| A4 | A021 | Owner approval carried into control-04 attempt 3 and control-05 via `extra_allowed_tools` and harness line 5; prompt and permissions differ from earlier sessions | `sessions.json` | yes |
| A5 | A021 | Control-05 began from `8b4ffa3` instead of `8c175d5`, then merged `1aa11fb` (conflict) and reconciled `2bb713f` | `sessions.json`; EV-A064 | yes |
| A6 | A021 | Control-02 began on a stale clone; `.ros` state reconciled by hand as a union; stash left in container; metrics inflated | `EV-ROS-2026-A066` only | **no** (missing from `sessions.json`; EV-A064 says 02 started correctly) |
| A7 | A021 | Control-03 started 13 min after control-02 because of the orchestration check-in cadence | `sessions.json` note | yes |
| A8 | A021 | `metrics/control-04.json` covers only bookkeeping attempt 3; control-05 metrics written before its last two commits | `sessions.json` | yes |
| A9 | A021 | `get_session` platform usage supplements the transcript metrics | protocol Results | yes |
| A10 | A021 | Evaluator received full branches with history, not neutral patch series; arm labels visible | `evaluation.txt` (self-report); EV-A064 "Blinding was partial" | partly (not recorded as a protocol deviation) |
| A11 | A021 | Evaluator did not verify isolation from session event logs, as the protocol required | not recorded | **no** |
| A12 | A021 | No step telemetry in the control arm (0 step events, against 25 in the grouped arm) | not recorded | **no** |
| A13 | A021 | Evaluation file written as `.md` under `research/experiments`, failing validation; copied as `evaluation.txt` | `evaluation.txt` | yes |
| A14 | A021 | Second (kit) evaluation: scrubber missed "grouped-arm" and "control branch"; mapping known | EV-A067 section 3 | yes |
| A15 | A021 | Kit prep script never validated with the grouped branch as input before use | EV-A065 "Validation performed" | yes |
| A16 | A021 | Grouped arm committed a live experiment group to `groups.json`, whose note points to a missing file | EV-A064 Quality | yes (as a finding) |
| A17 | A021 | Mapping "drawnAt 16:52Z" is later than the blind branches' creation (16:51:55Z); no commitment | `blind-mapping.json`; GitHub activity | **no** |
| R1 | R2 | No .NET preinstalled; dotnet-sdk-10.0 10.0.112 from apt; FSharpCore override in every build | run record E1, C1 | yes |
| R2 | R2 | Harness note differs from A021 ("install .NET with the repository's own scripts"; metrics script "if helpful") | run record E2 | yes |
| R3 | R2 | Reserved tag `config:auto-create-pr:draft` on sessions; no PR opened | run record E3 | yes |
| R4 | R2 | Group analysis committed as `.md`, then moved to `.txt` | run record G1 | yes |
| R5 | R2 | Grouped metrics captured before GROUP-05 completion (11 of 13 commits) | run record G2 | yes |
| R6 | R2 | C2 and C3 started from a stale `8b4ffa3` checkout and merged later (`fa821cc`, `5a03452`) | run record C2; EV-A070 | yes |
| R7 | R2 | Worker metrics captured before final steps | run record C3 | yes |
| R8 | R2 | Worker metrics schemas differ; missing fields left unrecorded | run record C4 | yes |
| R9 | R2 | Control run log is `.md` under `research/experiments`; final tip `8bb8f78` not re-validated | run record C5 | yes |
| R10 | R2 | Orchestration commits made without a Praxis work item | run record O1 | yes |
| R11 | R2 | Prompts differ from A021's frozen prompts (owner's fuller instructions) and are not archived | run record O2 | partly (texts not preserved) |
| R12 | R2 | Isolation compliance not checked from event logs | run record section 5 | yes |
| R13 | R2 | Control arm had its own orchestrator session C, a structural difference from A021, counted in control cost | run record section 4; POST-UNBLINDING | **no** (not flagged as a deviation) |
| R14 | R2 | No frozen R2 protocol; metrics defined after unblinding | not recorded | **no** |
| R15 | R2 | Evaluator had no local .NET or GitHub DNS; used exact-SHA CI without the FSharpCore override; no evaluator fixture or concurrency probe | `EVALUATION.txt` section 5; `EVIDENCE-PENDING-ID.txt` | yes |
| R16 | R2 | Commitment salt not recoverable; commitment `b5873a54...` unverified; mapping established by blob identity | POST-UNBLINDING; EV-A070 | yes |
| R17 | R2 | Post-unblinding record first numbered EV-ROS-2026-A063 (collision), later renumbered EV-A070 | `6b538516` file list | **no** |

## Claims that survive

Each is the narrowest statement still supported after the threats. The threats that bound it are in brackets.

1. **Resources (A021 and R2).** For this cohort, on this platform and model, the single grouped session used less platform-reported cost and output tokens than the sequence of per-item sessions in both runs. The reductions were 46-56% in A021 and 52-59% in R2, depending on whether failed attempts and the control orchestrator are counted. Summed implementing-session time was 31-45% lower in A021 and 44% lower in R2. These are single-run observations, not effect estimates. [T03, T04, T06, T13, T14]
2. **Repeated context (A021).** The control sequence re-read repository instructions and governance documents once per session and made at least twice as many model requests, file reads and searches. Part of this follows mechanically from the number of sessions, and the control figures are lower bounds. [T15]
3. **Cross-item architecture (A021 and R2).** In both runs the grouped arm, which was instructed to write a cross-item design before coding, produced one group store, one member-admission rule and one output/error convention. The per-item arm, which had no such instruction, produced two or three stores and divergent rules (A021), or a create/add admission inconsistency and less unified mutation (R2). The facts are re-verifiable from code. The attribution is to the grouped-with-mandated-analysis bundle, not to shared context alone, and the A021 judgements were not blind. Some of the control divergence comes from stale checkouts. [T01, T02, T05, T08]
4. **Local quality (A021 and R2).** Per-item execution was stronger on several local concerns in both runs: planner reuse and checkpoint verification in A021, checkpoint ownership validation in R2. Confirmed acceptance defects were found in both arms of both runs (two each in R2). Neither execution mode was better on acceptance quality overall. [T12, T17]
5. **Context pressure.** No compaction or context reset occurred in any session. Peak grouped context was about 32% of the window at five items. Nothing can be inferred about larger cohorts. [T06, T20]
6. **Predictions (A021).** The planner's frozen order, critical path, conflict rating and safe-concurrency predictions held. Its duration prediction missed in both arms. The protocol freeze is verified by server-side push times. [T29]

## Claims that do not survive

- "Shared agent context reduces architectural drift" as a causal or mechanism claim (T01).
- "A021 was blindly evaluated" and "two blinded evaluations" (T02). Only R2 had a blind evaluation, and its blinding was procedural (T09).
- "58.72% platform-cost reduction" and "61.08% elapsed-time reduction" as unqualified measures of execution mode (T03, T04).
- "Grouping clearly beneficial for high-affinity work" as the A021 outcome class (T12).
- R2 as a replication under the frozen A021 protocol, or as independent evidence (T07, T08, T06).
- PGEI or affinity moderation as tested or supported (T21).
- Any statistical, general or cross-repository claim (T06, T20).
- The POST-UNBLINDING statement that A021 platform cost was unavailable (T16).
- The evidence-matrix row "R2 evaluator remained blind to mapping while evaluating — Direct". This is a self-report, not direct evidence (T09).

## Required disclosures (minimum)

1. The treatment definition, and the asymmetric instructions (T01).
2. The A021 evaluations were not blind, with the specific leaks (T02).
3. The sensitivity table above, plus a single time definition across both studies (T03, T04, T13).
4. All four stale-checkout events, and their arm asymmetry (T05).
5. n = 1 per cell, no inference, and R2 as a same-setup re-execution designed with knowledge of interim A021 outcomes, with its prompts unavailable (T06, T07, T08).
6. That R2 blinding was procedural, plus the server-side push timestamps (T09, T10).
7. Post hoc status of PGEI and HY-A029 (T21).
8. Role overlaps: designer and cohort author; orchestrator and analyst; R2 evaluator and unblinder (T10, T19).
9. The deviations register above, including the five deviations that were never recorded at the time.

## Could not determine

- **The R2 prompt texts.** They are absent from every ref, so prompt symmetry, and whether the prompts carried A021 lessons, cannot be audited.
- **Whether any implementing session read the other arm or the other study's branches.** The transcripts are not in the repository.
- **Whether the R2 evaluator saw A021's results.** Its GitHub connector could reach `main`, and its "prior context" was a conversation whose contents are unknown.
- **The cause of the stale checkouts.** Whether other A021 control sessions (01, 03, 04) also hit stale clones without noticing is unknown.
- **Orchestrator costs.** The top-level orchestrator costs in both studies, and the A021 orchestrator's time spent on control hand-offs, are unknown.
- **The A067 kit evaluator's session, model and start time.** Its provenance says `model: unknown`, so whether it could have seen EV-A064 (pushed before its report at 18:16:10Z) is unknown.
- **The cause and effect of `worker_epoch: 2`** on the A021 grouped and control-05 sessions.
- **Whether the A021 label draw was random.** No commitment or seed was recorded.
