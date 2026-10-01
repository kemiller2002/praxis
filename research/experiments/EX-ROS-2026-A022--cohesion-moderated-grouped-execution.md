---
id: EX-ROS-2026-A022
title: Cohesion-moderated grouped execution - 2x2 of cohort affinity by execution topology (frozen protocol)
research_area: repository-operating-system
status: proposed
created: 2026-10-01
tests_hypotheses:
  - HY-ROS-2026-A029
  - HY-ROS-2026-A028
inputs:
  - EV-ROS-2026-A071
  - EV-ROS-2026-A064
  - EV-ROS-2026-A063
related_theories: []
related_documents:
  - EX-ROS-2026-A021
  - research/experiments/EX-ROS-2026-A022-setup/README.txt
  - requirements/EXECUTION-ORCHESTRATION.md
  - requirements/PLANNING-WORK-GROUPS.md
  - docs/group-analysis-template.md
tags: [planning, grouping, context, cohesion, affinity, experiment, protocol, blind-evaluation]
provenance:
  contributions:
    EXE-20261001T105134505Z-4886c699:
      operations: [created, modified]
      at: 2026-10-01T11:03:41.000Z
      last: 2026-10-01T11:18:32.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "EXP-A022-DESIGN: frozen 2x2 protocol for cohesion-moderated grouped execution"
derived_from: [EX-ROS-2026-A021]
---

# Experiment

## Research question

Does the benefit of executing a cohort of work items in one shared reasoning
context, rather than one fresh context per item, depend on how
architecturally cohesive the cohort is? Specifically: is the grouped-over-
independent advantage in execution efficiency and cross-item architectural
consistency greater for a high-affinity cohort than for a low-affinity
cohort, without a material loss of acceptance quality?

This is not a test of "waterfall versus iterative development". Both
topologies implement, test, commit and checkpoint incrementally; the grouped
one additionally reasons about the whole cohort before mutating
(`HY-ROS-2026-A028`, PGEI).

## Hypotheses tested

- `HY-ROS-2026-A029` (cohesion moderates shared-context value): primary.
- `HY-ROS-2026-A028` (shared context reduces repeated work or improves
  consistency): replicated in a different subsystem by the high-affinity
  comparison (conditions A and B).

## Status and gate

**Proposed; designed and frozen; not executed.** Setup recorded on
2026-10-01 under work items `EXP-A022-BASELINE` and `EXP-A022-DESIGN`. No
condition has run and no cohort item has been implemented.

Execution may begin only when every item of
`research/experiments/EX-ROS-2026-A022-setup/readiness-checklist.txt`
section 1 passes. At setup completion R1..R9 and R14 pass and R10 is met by
the setup commit the orchestrator records; R11 (the owner's approval of
cohorts, protocol, cost and the temporary deletion of execution branches),
R12 (a separate, explicit instruction to execute) and R13 (protective tags,
pushed by the orchestrator) are pending. The setup PR must not be merged to
main before the reveal.

Nothing in this record or in the setup directory may change after the first
condition starts, except dated additions that record what happened.
Deviations are recorded, never silently corrected.

## Design

|                | shared / grouped | independent |
|----------------|------------------|-------------|
| high affinity  | A (1 session)    | B (5 fresh workers, sequential) |
| low affinity   | C (1 session)    | D (5 fresh workers, sequential) |

Independent variable: execution topology (one continuing reasoning context
that analyses the whole cohort before implementing it, versus one fresh
context per item that analyses and implements only that item). Both
topologies write an architecture analysis with a reuse inventory before any
product change; A021 gave that step to the grouped arm only, so "plan before
mutating" was confounded with "share one context". A022 removes that
confound. Moderator: cohort affinity, defined before
execution. Held constant: baseline, items and criteria, instructions,
model, runtime, environment, permissions, toolchain, harness, metrics
instrument, evaluator instructions.

## Baseline

`B = f806f817e4656e00550313d839b59ceea87a682d` (main `f68565f` plus two
Praxis-state-only commits that capture and triage the cohort items; CI green,
run 36851089199). The design (this record, `HY-ROS-2026-A029`, the setup
directory) was committed after `B`, so no implementer can read it from its
own branch. Branch creation and the protective tag:
`EX-ROS-2026-A022-setup/baseline.txt`.

## Cohorts (frozen)

Order is ordinal by ID (it also satisfies the one declared dependency).

High affinity (local control plane, PRX-CTL family of
`requirements/EXECUTION-ORCHESTRATION.md`): `PRAXIS-CTL-02` typed work-state
read API, `PRAXIS-CTL-03` executions/receipts/evidence read API,
`PRAXIS-CTL-04` transition requests with structured refusals,
`PRAXIS-CTL-05` stateless host and restart reconstruction, `PRAXIS-CTL-06`
multi-repository aggregation (depends on `-02`). 27 acceptance criteria.

Low affinity (five subsystems): `ACTOR-KIND-ANCHOR` (provenance input
validation), `ATTR-RECONCILE-SYMLINK-SUBMODULE` (Git reconciliation
identity), `PRAXIS-CN-GL-KINDS` (artifact kinds, GitHub #56),
`PRAXIS-PLAN-ERROR-HISTORY` (planner calibration history, PRX-PLAN-170..173),
`PRAXIS-STATE-MERGE-01` (Praxis state merge). 26 acceptance criteria.

Inventory, rejected candidates and reasons, scope, dependencies and
confounders: `EX-ROS-2026-A022-setup/cohorts.txt`; items verbatim:
`criteria/cohort-high.txt`, `criteria/cohort-low.txt`; selection evidence:
`EV-ROS-2026-A071`.

If an item proves impossible or invalid after freezing, the condition blocks
it with its reason and continues; it is reported as not completed in that
condition. The cohort is never changed and nothing is substituted.

## Affinity definition (before execution)

`EX-ROS-2026-A022-setup/affinity/model.txt`. Per pair of items, twelve
observable dimensions recorded from pre-execution information only (shared
concepts, durable state written by at least one side, invariants, files,
public surfaces, producer/consumer, ordering, validation rules, error model,
transaction boundary, tests, assumption propagation), each with the shared
elements that make it present; five are "strong" (state, invariants,
producer/consumer, transactions, assumption propagation). Generic elements
(the CLI dispatcher, `docs/cli.md`, `validate`) are excluded and listed. No
composite score. Rule fixed before computation: HIGH = every pair has a
strong dimension, median count >= 5, half the pairs have two or more strong
dimensions; LOW = no pair has more than one strong dimension, at most a fifth
of pairs have any, median count <= 1. Result: HIGH and LOW respectively
(`affinity-matrix.txt`); the planner (`plan groups`) corroborates with one
recorded discrepancy (`planner/README.txt`).

## Method

Exact protocols and every message sent to a session are in
`EX-ROS-2026-A022-setup/protocol/`; the runbook is `orchestration.txt`.

- **Grouped (A, C)** (`conditions.txt` section 1): one fresh session;
  start guard; reads every item; writes and pushes a cohort-level
  architecture analysis (`research/experiments/EX-ROS-2026-A022-runs/<token>/analysis-g.txt`,
  following `docs/group-analysis-template.md`) before any product change, so
  premeditated architecture is distinguishable from later explanation;
  implements in the same context, cross-cutting where appropriate, with
  normal incremental commits, per-item checkpoints and completion, a
  per-criterion verification pass, no CI polling; finishes every item.
- **Independent (B, D)** (`conditions.txt` section 2): one fresh session per
  item, strictly in order on one branch; each receives only its item and the
  repository; writes and pushes an item-level architecture analysis
  (`analysis-w<n>.txt`, same template sections) before any product change;
  may read earlier workers' code; never sees grouped material; same
  per-criterion verification instruction; commits, checkpoints and
  completes before the next worker starts.
- **Both**: no subagents (a grouped session that delegates would no longer
  be one context); the earlier grouping experiments' records may not be read
  (they state the expected result); no CI polling.
- **Stale-checkout protection** (`start-guard.txt`): before every session
  receives any task, the orchestrator verifies, from the tool output in the
  session's event log, that HEAD, the remote branch (`git ls-remote`) and
  the tracking ref all equal EXPECTED (baseline, or the previous worker's
  final SHA read from the remote), that the checked-out branch and its
  upstream are the run branch, and that the tree is clean (sessions are
  created with the run branch as their outcome branch);
  otherwise the session is repaired (local reset only) or replaced and the
  incident recorded, and a mismatch on the remote stops the condition.
  After each worker: the remote head equals the reported SHA, descends from
  EXPECTED, its first new commit's parent is EXPECTED, and there are no merge
  commits. Expected and actual start SHAs are recorded for every session.
  This closes A021-R2 deviation C2.
- **Environment parity** (`environment-parity.txt`): same model, runtime,
  environment, permission mode, baseline, toolchain and instructions; all
  four conditions launched within 15 minutes in a random order; model and
  runtime recorded per session from `get_session`; unavoidable differences
  recorded.
- **Stop conditions**: all items completed or blocked; one replacement or
  continuation session per failure (recorded; for a grouped condition a
  major deviation); budget stop per condition at 8 hours or USD 120, and 3
  hours per independent worker.

## Measurement

`EX-ROS-2026-A022-setup/measurement/metrics-schema.json`, collected by the
orchestrator after all conditions finish and kept separate from quality
evaluation: summed active session time (work-prompt delivery to final
event, excluding guard and orchestrator gaps; primary), wall-clock span and
orchestration overhead (reported); platform cost; model
requests; input, output, cache-read and cache-write tokens; file reads;
searches; files read more than once (within a session and across a
condition); governance-document reads; builds; test runs; failed
builds/tests; tool errors and retries where observable; context compactions;
cold starts; commits; merge commits; changed files; source and test LOC added
and removed; CI runs and failed CI runs; time to first product edit;
guard incidents and deviations. Raw inputs are preserved. Unknown is null,
never zero. Nothing is combined into a score; token counts are not quality.
The orchestrator's own session is excluded from every condition (A021-R2
counted its control orchestrator in the control arm).

## Blind quality evaluation

`EX-ROS-2026-A022-setup/blinding/`. Two kits, one per cohort, each holding
that cohort's two implementations under neutral, randomly drawn arm IDs; the
grouped/independent assignment is drawn independently per kit. Each blind
arm is one squashed commit on the baseline with a neutral author, date and
message, `.ros/` reset to the baseline, run directories (metrics, analysis)
removed, and experiment terms redacted from lines the arm added in
documentation and comments by one rule for every arm (code is never altered;
residual matches are logged). One fresh evaluator session per kit, same
model for both, with identical instructions; the evaluator sees no metrics,
costs, durations, sessions, branch names of executions, commit history or
logs, and is not told the hypothesis or the kit's affinity. It reports
concrete findings per acceptance criterion, confirmed defects with
reproductions, and cross-item findings (invariant consistency, duplicated
concepts, conflicting validation, storage fragmentation, API and error-model
consistency, transactions and concurrency, architecture duplication,
cross-item integration, tests, maintainability), each favouring an arm or
"equivalent" or "indeterminate"; no overall score.

## Mapping commitment and reveal

`blinding/commitment-reveal.txt` and `a022_commitment.py`. After execution
and before any kit exists: a CSPRNG-drawn mapping and 256-bit salt in a
canonical JSON payload (keys sorted, no whitespace, ASCII, UTF-8, no
trailing newline; experiment ID, arm IDs, cohort, mode, run token, execution
branch, final SHA, kit IDs, salt); commitment = SHA-256 of those bytes,
committed (hash only) to every kit and the orchestration branch's public log. The
plaintext never enters a repository before the reveal; it is escrowed three
ways (owner file with confirmed receipt, orchestrator transcript, and an
AES-256 ciphertext on the orchestration branch whose passphrase goes only to
the owner), and a decrypt-and-verify rehearsal must pass before kits are
built. After both evaluations are pushed: reveal, recompute (script and
`sha256sum`), require an exact match, record the verification, and only then
associate findings with conditions. A mismatch invalidates the blinding
claim and is reported as such. This fixes A021-R2's unrecoverable salt.

## Pre-registered prediction

For the high-affinity cohort, grouped execution (A) is expected to reduce
repeated context acquisition and rework relative to independent execution
(B) and to improve cross-item architectural consistency without a material
loss in acceptance quality. The grouped-over-independent advantage is
expected to be materially greater for the high-affinity cohort than for the
low-affinity cohort (C versus D). Grouped execution is not required to win
the low-affinity comparison.

## Analysis plan (pre-registered)

Per cohort, comparisons are within the cohort (grouped versus independent on
the same items), so differences in cohort size and difficulty affect both
sides of each comparison.

1. **Resource advantage** per cohort: the ratio independent/grouped for
   (a) platform cost, (b) output tokens, (c) summed active session time; also
   reported, not primary: wall-clock span, model requests, cache-read
   tokens, file reads, searches, files read more than once across the
   condition, governance reads, builds, test runs. "Material" means a ratio
   of at least 1.25 (or at most 0.80 for a grouped disadvantage).
2. **Interaction (moderation)**: R_high / R_low for each of (a)-(c).
   "Materially greater for high affinity" means R_high / R_low >= 1.25 for at
   least two of the three; "similar" means all three within 0.80..1.25.
3. **Acceptance quality** per arm from the blind evaluation: criteria met,
   partially met, not met; confirmed defects by severity. A "material loss"
   for grouped in a cohort means at least two more confirmed major-or-
   critical defects or two more unmet criteria than the independent arm, or
   any confirmed critical defect (data loss, security, corrupt state) not
   mirrored in the independent arm.
4. **Consistency**: cross-item findings by category and which arm each
   favours. A grouped consistency advantage in a cohort means at least two
   more cross-item findings favouring grouped than independent, excluding
   "equivalent", "indeterminate" and "not-applicable".
5. **Context pressure**: compactions, late inconsistent decisions or
   forgotten criteria in the grouped sessions (from the evaluation and the
   event logs).

These thresholds are reading aids for classifying one run, not statistical
tests; with one run per cell no significance is claimed. All raw numbers and
findings are reported alongside.

## Outcome classes (interpretations, fixed before execution)

- **A. High-affinity grouped advantage, little/no low-affinity advantage**:
  supports `HY-ROS-2026-A029` (cohesion moderates).
- **B. Similar grouped advantage at both affinity levels**: supports a more
  general shared-context effect; weakens cohesion as the moderator.
- **C. No meaningful grouped advantage**: weakens `HY-ROS-2026-A029` and
  potentially `HY-ROS-2026-A028`.
- **D. Grouped quality materially worse despite resource savings**: weakens
  the claim that grouping gives net engineering value (at that affinity
  level).
- **E. Low-affinity grouped worse while high-affinity grouped better**:
  strong evidence that Praxis should choose execution topology by affinity.
- **F. Invalid or inconclusive** (protocol failure).

### Decision table (exhaustive, with precedence)

Inputs, per cohort c in {high, low}, from the analysis plan:
- G_c (resource): **adv** if at least two of the three primary ratios
  (independent/grouped: cost, output tokens, active time) are >= 1.25;
  **dis** if at least two are <= 0.80; otherwise **none**.
- Q_c (quality): **loss** if the grouped arm shows a material quality loss
  (analysis plan item 3); otherwise **ok**.
- K_c (consistency): **adv**, **dis** or **none** per analysis plan item 4
  (the mirror condition for **dis**).
- M (moderation): **greater** if R_high/R_low >= 1.25 for at least two of
  the three primary ratios; **similar** if all three lie in 0.80..1.25;
  **mixed** otherwise.

Classification, first matching row wins:

| # | Condition | Class |
| --- | --- | --- |
| 1 | a condition incomplete at a budget stop, a grouped continuation session, a failed commitment verification, or a confirmed cross-condition or evaluator leak | F |
| 2 | G_high = adv, Q_high = ok, and (G_low = dis or Q_low = loss or K_low = dis) | E |
| 3 | G_high = adv, Q_high = ok, M = greater, G_low = none | A (clean) |
| 4 | G_high = adv, Q_high = ok, M = greater, G_low = adv | A (with a general effect) |
| 5 | G_high = adv and G_low = adv and M = similar | B |
| 6 | G_high != adv and G_low != adv | C |
| 7 | G_high = adv, Q_high = loss | D (high) |
| 8 | anything else (for example M = mixed, or G_high != adv with G_low = adv) | unclassified: reported descriptively, no support claimed |

Qualifiers, reported with every class: D is also flagged for any cohort with
G_c = adv and Q_c = loss (for example "A (clean) + D(low)"); K_high = adv
strengthens A or E and its absence is stated; K_low is expected to be
**none** for lack of cross-item decisions and does not count for or against
A. A grouped context-pressure finding (compactions, late inconsistent
decisions) is reported with any class.

## Acceptance criteria

The experiment is complete when all four conditions have run under this
protocol (or stopped under its stop conditions), metrics are collected per
the schema, both kits are blindly evaluated and pushed, the reveal is
verified against the commitment, predictions are compared with observations,
and the outcome is classified. It succeeds whatever the outcome.

## Falsification criteria

`HY-ROS-2026-A029` is weakened by outcomes B, C or D and by a larger grouped
advantage for low than for high affinity; supported by A or E. See the
hypothesis record.

## Controls

Same baseline, items, criteria, instructions, model, runtime, environment
and harness for all conditions; affinity frozen before execution; the
design kept out of the arms' history; condition-neutral branch names;
start guard; no cross-condition visibility, checked from event logs; random
launch order; orchestrator excluded from metrics; blind evaluation with
per-kit randomized neutral IDs and a salted, escrowed commitment.

## Threats to validity

| Threat | Mitigation or how it is reported |
| --- | --- |
| One repository | Stated; no generalization beyond Praxis; replication elsewhere is a follow-up |
| Model/provider dependence | One model for all implementers, recorded per session; any fallback is a deviation; evaluator preferably a different model |
| Cohort-size differences | Equal sizes (5 and 5) by rule |
| Work-item complexity imbalance | Criteria counts close (27/26); per-item scope recorded; within-cohort ratios used. Smaller items carry relatively more cold-start overhead, which inflates the grouped advantage for the low cohort, i.e. biases against the hypothesis |
| Overlap of complexity/novelty with affinity | High cohort is new API surface (more design freedom, more room for inconsistency); low cohort mostly fixes existing behaviour. Reported as a confounder; prediction 2 (resource ratios) is less exposed to it than the consistency comparison |
| Affinity classification subjectivity | Frozen, auditable observations and relations; rule fixed before computation; selection changes logged; planner corroboration with its discrepancy reported |
| Designer-authored high cohort | Items split along existing requirement IDs; criteria quote the requirements; recorded (as in A021) |
| Sequential integration effects | Inherent to the independent protocol (later workers see earlier code); identical in kind to the grouped condition's own evolving tree |
| Context-window pressure | Measured (compactions, late inconsistencies); a grouped continuation session is a major deviation |
| Stale checkout | Start guard before every session, verified from tool output; post-conditions after every worker |
| Branch drift | Every condition starts at B on its own branch; main is irrelevant to the arms; tag protects B |
| Differing CI state | CI results collected after the run; sessions told not to poll; CI on the same workflow for all branches |
| Runtime/tool installation differences | One harness note; installed SDK recorded per session; no condition-specific repair |
| Evaluator leakage | Kits only; forbidden sources listed; sources accessed reported; one kit per evaluator |
| Treatment leakage through filenames/commits/comments | Neutral branch names; squashed neutral commits; run directories removed; `.ros/` reset; mechanical redaction of added lines; harness rule 5; residual leaks logged |
| Different agent behaviour despite same prompt | One run per cell: no causal claim; outcome classified, raw data reported |
| Learning effects in orchestration | One orchestrator follows a fixed runbook; launch order randomized; all conditions run concurrently |
| Platform drift over time | Concurrent launch; per-session model and runtime recorded |
| Implementers know they are in an experiment | Same knowledge in all conditions; harness note identical |
| Excluded items left ready in the backlog | Prompts restrict each condition to its items; checked afterwards |
| Low-cohort internal couplings (one shared file, one shared store) | Recorded in the matrix and `cohorts.txt`; findings on those pairs read accordingly |
| Two low items (`PRAXIS-CN-GL-KINDS`, `PRAXIS-STATE-MERGE-01`) could involve design choices the owner might reject | Criteria fix the direction where the source states one; the evaluator judges against the frozen text |
| Compound treatment (planning versus shared context) | Both topologies write an analysis with a reuse inventory before product changes; only its scope and the context's continuity differ |
| Subagent use dissolving the single context, and transcript metrics missing subagent work | Subagents forbidden in every session; event logs checked; platform usage (primary) covers the whole session |
| Transcript-metrics snapshot taken before each session's last steps (A021-R2 G2/C3) | Same rule in every session; it undercounts each independent worker, so transcript metrics favour independent slightly; primary metrics come from platform usage and event timestamps |
| Implementers inferring the hypothesis from earlier records at B | The harness note forbids reading the earlier grouping records; event logs checked; residual risk recorded (the item text names EX-ROS-2026-A022) |
| Evaluator differences confounded with cohort (one evaluator session per kit) | Same model and instructions for both; recorded as a threat; a second independent evaluation per kit is recommended if budget allows |
| Execution history visible in GitHub Actions after the branches are archived | Evaluators forbidden from Actions runs other than the two blind commits; residual risk recorded |
| Mapping recoverable from repository contents by tree identity | Token table and per-token records sealed; execution branches archived as encrypted bundles and deleted before kits exist, restored after the reveal |

## Setup review (three passes, 2026-10-01)

**Pass 1, experimental validity.** Cohort selection used only pre-execution
information; no implementation of any item existed. Affinity is defined and
classified before execution by a frozen rule; the first computation rejected
`PRAXIS-CTL-01` and the change is logged. The comparison tests cohesion as a
moderator because each cohort has both topologies; the main confounders
(novelty, size, designer authorship) are recorded. The stale-checkout
failure cannot recur silently: the guard runs before any task and is checked
from tool output, and post-conditions catch a wrong parent. Fixed during the
pass (design decisions taken while checking these questions): exclusion of the orchestrator from condition costs; per-criterion
verification instruction given to both topologies (A021 found independent
executions more faithful to individual criteria, so the grouped prompt alone
should not get it).

**Pass 2, blinding.** Branch names, commit messages and history, file paths
(run directories), session metadata (`.ros/`), metrics, comments and docs
(redaction of added lines), generated artifacts and test names were
reviewed (`blinding-protocol.txt` section 3). Fixed during the pass: the kit
builder first redacted pre-existing baseline text in modified files and
matched legitimate domain terms ("orchestration", "shared context"); it now
scans only lines the arm added and uses experiment-specific terms only.
Unavoidable: style, diff size, redaction density, and shared infrastructure
itself.

**Pass 3, reproducibility.** Exact SHAs (baseline, setup commits), every
prompt verbatim, the runbook, stop conditions, the commitment's byte-exact
canonicalization with an independent `sha256sum` recomputation, a rehearsed
escrow, and deviation recording are present. Fixed during the pass: the
metrics schema now distinguishes what the transcript instrument provides
from what must come from event logs; the launch-order draw command was
corrected.

### Independent adversarial review (after the three passes)

A separate, read-only reviewer session re-ran the checks and reported 21
defects (5 high, 7 medium, 11 low). All of them were fixed before setup
completion, except that the protective tags could not be pushed from this
session; that step is now the orchestrator's.

High-severity defects:
- The start guard could pass with the right commit checked out on the wrong
  branch or on a detached HEAD.
- The guard read a tracking ref that might be missing, instead of the
  remote itself.
- Both guard defects were reproduced on throwaway repositories and then
  fixed: `ls-remote`, an explicit refspec, branch and upstream checks, and
  `outcome_branch`.
- The token-to-condition mapping and the execution branches would have let
  anyone with repository access recover the mapping by tree identity. Both
  are now sealed, and the branches are archived before kits exist.
- Provenance execution IDs and timestamps would have survived into blind
  trees. They are now neutralized.
- The primary time metric included orchestrator latency, which independent
  conditions pay five times. It is now active time.

Medium-severity defects:
- Evaluators could reach the design on main or on the default branch.
- The outcome classes were not exhaustive; the decision table was added.
- Only the grouped arm had the analysis step, and subagents were
  uncontrolled.
- Implementers could read earlier records that state the expected result.
- The redaction pattern both over-matched and under-matched.
- The escrow relied on one tool with no fallback.
- The confound between evaluator and cohort was not listed.

Low-severity defects:
- Placeholders, labels and the setup SHA were incomplete or undefined.
- The baseline and selection commits were unprotected; the tag push was
  refused here and is delegated to the orchestrator.
- A commit-message rule contradicted the analysis commit message.
- "Product path" had two definitions.
- One cross-reference was wrong.
- The freeze rule contradicted the mid-run fix rule.
- A fetch step was missing.
- Some stop cases were unspecified.
- An ordering claim could not be verified from Git; it is now stated as
  such.
- The harness note was missing two CI steps.

## Product implication (future; not implemented)

If `HY-ROS-2026-A029` is supported, Praxis planning could estimate
work-item affinity before execution and choose an execution topology:
strongly coupled cohort -> one shared reasoning context; weakly coupled
work -> independent or parallel contexts; intermediate -> partition into
affinity clusters. The planner already computes metadata affinity
(`plan groups`); this experiment's observation model and the planner's
coarse path-overlap signal (one recorded discrepancy) would both inform
such a feature. Nothing is changed in Praxis on the strength of this
design.

## Differences from EX-ROS-2026-A021 and A021-R2

- A low-affinity cohort and a different subsystem for the high-affinity one.
- Affinity defined by a frozen observation model, not by the planner alone.
- Start guard and post-conditions (R2 deviation C2).
- Byte-exact, salted, escrowed commitment with a rehearsal (R2 salt loss).
- Two kits, independently randomized, one evaluator session per kit.
- Orchestrator excluded from condition metrics.
- Support files are plain text or JSON (R2 deviations G1 and C5).
- Harness note updated for the F#-only repository (no npm build).

## Launch

Once R11 and R12 pass, start a fresh orchestrator session (same environment
and model as intended for the implementers) with:

> You are the experiment orchestrator for EX-ROS-2026-A022 in
> kemiller2002/praxis. Follow
> research/experiments/EX-ROS-2026-A022-setup/protocol/orchestration.txt
> exactly, from step O1 to step R, using the setup commit <SETUP-SHA>
> (baseline f806f817e4656e00550313d839b59ceea87a682d). You implement nothing
> and evaluate nothing. Record every step in the run record on
> experiment/a022-orchestration and commit and push after each step. Record
> deviations; never silently repair. Stop and report if any readiness check
> fails.

## Results

None yet.

## Analysis

None yet.

## Conclusion

None yet.

## Registry updates required

On completion: a results evidence record; status updates for
`HY-ROS-2026-A029` and `HY-ROS-2026-A028`; this record's Results, Analysis
and Conclusion; and, if supported, a proposal (not an adoption) for
affinity-based execution topology in the planner.
