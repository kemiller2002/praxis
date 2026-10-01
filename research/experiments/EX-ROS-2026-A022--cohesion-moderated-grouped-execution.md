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
      operations: [created]
      at: 2026-10-01T11:03:41.000Z
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
section 1 passes. At setup completion R1..R10 pass; R11 (the owner's
approval of cohorts, protocol and cost) and R12 (a separate, explicit
instruction to execute) are pending.

Nothing in this record or in the setup directory may change after the first
condition starts, except dated additions that record what happened.
Deviations are recorded, never silently corrected.

## Design

|                | shared / grouped | independent |
|----------------|------------------|-------------|
| high affinity  | A (1 session)    | B (5 fresh workers, sequential) |
| low affinity   | C (1 session)    | D (5 fresh workers, sequential) |

Independent variable: execution topology (one continuing reasoning context
for the cohort with a cohort-level analysis before implementation, versus
one fresh context per item). Moderator: cohort affinity, defined before
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
  architecture analysis (`research/experiments/EX-ROS-2026-A022-runs/<token>/cohort-analysis.txt`,
  following `docs/group-analysis-template.md`) before any product change, so
  premeditated architecture is distinguishable from later explanation;
  implements in the same context, cross-cutting where appropriate, with
  normal incremental commits, per-item checkpoints and completion, a
  per-criterion verification pass, no CI polling; finishes every item.
- **Independent (B, D)** (`conditions.txt` section 2): one fresh session per
  item, strictly in order on one branch; each receives only its item and the
  repository; may read earlier workers' code; never sees grouped material;
  same per-criterion verification instruction; commits, checkpoints and
  completes before the next worker starts.
- **Stale-checkout protection** (`start-guard.txt`): before every session
  receives any task, the orchestrator verifies, from the tool output in the
  session's event log, `HEAD == origin/<branch> == EXPECTED` (baseline, or the
  previous worker's final SHA read from the remote) with a clean tree;
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
evaluation: wall-clock span and summed session time; platform cost; model
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
committed (hash only) to every kit and the orchestration record. The
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
   (a) platform cost, (b) output tokens, (c) summed session time; also
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

- **A. High-affinity grouped advantage, little or no low-affinity
  advantage**: supports `HY-ROS-2026-A029` (cohesion moderates).
- **B. Similar grouped advantage at both affinity levels**: supports a more
  general shared-context effect; weakens cohesion as the moderator.
- **C. No meaningful grouped advantage at either level**: weakens
  `HY-ROS-2026-A029` and potentially `HY-ROS-2026-A028`.
- **D. Grouped quality materially worse despite resource savings** (in
  either cohort): weakens the claim that grouping gives net engineering
  value (for that affinity level).
- **E. Low-affinity grouped worse while high-affinity grouped better**:
  strong evidence that Praxis should choose execution topology by affinity.
- **F. Invalid or inconclusive**: a condition incomplete at a budget stop,
  a grouped condition that needed a continuation session, a failed
  commitment verification, or a confirmed cross-condition leak; reported as
  such, with whatever partial evidence survives.

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
