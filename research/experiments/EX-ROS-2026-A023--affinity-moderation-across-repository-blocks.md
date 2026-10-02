---
id: EX-ROS-2026-A023
title: "Affinity moderation of grouped execution across repository blocks"
research_area: repository-operating-system
status: proposed
created: 2026-10-02
tests_hypotheses:
  - HY-ROS-2026-A029
  - HY-ROS-2026-A028
inputs:
  - EV-ROS-2026-A064
  - EV-ROS-2026-A070
  - EV-ROS-2026-A072
outputs: []
related_theories: []
related_documents:
  - EX-ROS-2026-A022
  - EX-ROS-2026-A021
  - HY-ROS-2026-A028
  - HY-ROS-2026-A029
  - requirements/PLANNING-WORK-GROUPS.md
  - docs/planning.md
tags: [planning, grouping, affinity, experiment, blocked-design, multi-repository, falsification]
provenance:
  contributions:
    EXE-20261002T122339366Z-b15d6ac8:
      operations: [created, modified]
      at: 2026-10-02T12:36:06.000Z
      last: 2026-10-02T12:47:36.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Preregistered the blocked multi-repository follow-on to A022: within-repository grouped/independent contrasts, repository as blocking factor, frozen measured affinity, scale classes and a setup-adjusted ratio"
      evidence: [EV-ROS-2026-A072]
derived_from: [EX-ROS-2026-A022, EV-ROS-2026-A072]
---

# Experiment

## Research question

Does the effect of grouped versus independent execution on resources and
architecture vary with pre-measured work-item affinity, once every
grouped/independent comparison is made **within** a repository block?

Secondary: is any grouping benefit explained by affinity (context reuse)
rather than by fresh-session setup cost, context pressure, repository
identity, or task scale?

## Motivation

- `EX-ROS-2026-A021` and its R2 replication found, on one high-affinity
  Praxis cohort, that grouped execution cost about 2.4x less and produced a
  more unified architecture with comparable acceptance quality
  (`EV-ROS-2026-A064`, `EV-ROS-2026-A070`). Neither run had a low-affinity
  control, so neither can tell a cohesion mechanism apart from generic
  one-session setup amortization (`HY-ROS-2026-A029`).
- `EX-ROS-2026-A022` was preregistered to supply that control as a
  same-repository 2x2. Its target gate found no eligible repository among
  fourteen inspected (`EV-ROS-2026-A072`). Real backlogs were either drained,
  too small, or, in Signal's case, rich but connected across one domain, so
  no single repository supplied both affinity extremes at once. A022 stays
  `blocked` with its criteria unchanged. **This experiment does not amend or
  replace it.**
- The binding constraint is "one repository, both affinity levels, at the
  same time". This design removes it by blocking on repository, while still
  never comparing grouped and independent execution across repositories.

## Design overview

A **randomized complete block design with a measured moderator**:

- **Block** = one repository at one frozen baseline, with one natural cohort
  of genuine ready items.
- **Treatment** (within block) = grouped execution (G) versus independent
  execution (I) of exactly the same cohort from exactly the same baseline.
- **Moderator** (across blocks) = the cohort's affinity, measured and frozen
  before any arm runs (section "Affinity measurement").
- **Stratifier** = the cohort's scale class (section "Task-size controls").

```
Block b (repository R_b, baseline B_b, cohort C_b; affinity A_b and scale class S_b frozen first)
  arm G_b: one fresh session implements every member of C_b
  arm I_b: one fresh session per member, serially, no carried private context
  effect:  r_b = ln(I_b / G_b)        computed within the block only
Across blocks: does r_b (and its setup-adjusted form) increase with A_b within a scale class?
```

Repository identity is never the treatment. It cannot be confounded with
affinity in the way "high = repository A, low = repository B" would be,
because every block contributes its own G-versus-I contrast. The moderation
question is then about how those within-block contrasts relate to measured
affinity. What blocking cannot remove is that affinity and repository vary
together *across* blocks. That residual threat is handled by the
repository-effect checks under "Analysis" and by the optional double block.

**Double block (preferred where it exists).** A repository that, at its
frozen baseline, genuinely contains both an eligible high-affinity cohort
and an eligible low-affinity cohort in the same scale class contributes two
blocks sharing one baseline. That is exactly A022's 2x2. If such a
repository appears, A022's own gate is satisfied, and the orchestrator must
record that A022 became runnable; it then chooses, before any arm starts and
with the reason recorded, whether to run A022 or include that repository
here as a double block.

## Status gate and feasibility

The experiment moves from `proposed` to `active` only when the target
inventory (next section) yields an eligible **minimum design**:

- at least **two high-affinity blocks and two low-affinity blocks within one
  scale class**, from at least four distinct repositories (or three
  repositories plus one double block);
- the **preferred design** is three high and three low in one scale class.
  With six blocks, perfect separation has an exact permutation probability of
  1/20 = 0.05 under the null. With four blocks it is 1/6, so a four-block
  result is recorded as exploratory.

If the inventory cannot yield the minimum design, the experiment is set to
`blocked` and the inventory is recorded. Cohorts are never padded, items are
never promoted or split for the experiment, and one affinity level is never
supplied by a different scale class.

Medium-affinity blocks are allowed and are reported. They enter the
continuous analysis but do not count toward the minimum.

`kemiller2002/praxis` is excluded as a block, as in A022. Its agents can read
the hypotheses, protocols and prior results under `research/`.

## Target selection

### Candidate repositories

All repositories in the owner's Echelon Foundry family that use Praxis (or
ROS), except `kemiller2002/praxis`. The candidate list and the reason for
each inclusion or exclusion are committed before inventory.

### Inventory (frozen, tool-run)

For each candidate, at a frozen SHA:

1. Effective lifecycle state is read with the Praxis CLI `work list --json`
   against a throwaway copy (the procedure of
   `research/experiments/EX-ROS-2026-A022-target-gate/inventory.py`).
2. Item records, Dependencies sections, cited requirements and the
   deterministic planner (`plan groups --json`, `plan analyze --json`,
   pinned `--as-of`) are captured.
3. Output files are committed with SHA-256 sums before any cohort is chosen.

### Item eligibility

An item may enter a cohort only if all of these hold:

- its effective state at the frozen SHA is `ready`;
- **provenance-based readiness**: its `ready` transition, and its capture,
  are attributed to an execution or contributor other than this
  experiment's, and both occurred before the inventory was frozen. An item
  readied "for the experiment" is ineligible, whoever readied it;
- it has stated acceptance criteria (in its description or detail record);
- its implementation is not already present at the baseline (checked
  against the item's acceptance criteria and recent history).

### Cohort formation (deterministic)

For each repository, candidate cohorts are formed **only** from eligible
items, as follows:

- **Natural high-affinity candidates**: each planner group, and each
  connected component of the explicit-relation graph (see A1 below),
  restricted to eligible items.
- **Natural low-affinity candidates**: maximum sets of eligible items with
  no pairwise strong structural signal, enumerated exhaustively when there
  are 20 or fewer eligible items, otherwise greedily in ordinal ID order.
- Cohort size: **3 to 6, natural**. There is no fixed five. Within one scale
  class, the high and low cohorts that are compared must have equal size, or
  differ by one with the difference recorded and analysed through the
  per-session normalization below. Items are never split, and unrelated items
  are never added to a cohort to make up a size.

Each candidate cohort's affinity profile and scale profile are computed by
the frozen instruments. One cohort per block is then selected by this
precedence:

1. it satisfies its affinity class rule;
2. it falls in the scale class that maximizes the number of complete
   high/low pairs across repositories;
3. it has the smallest scale-index distance to the median of the opposite
   affinity class in that scale class;
4. ties are broken by ordinal repository name, then the smallest member ID.

The selection log records every rejected candidate cohort and its reason.
If a cohort fails a rule, the selection changes, never the rule.

## Affinity measurement

Affinity is a **measured, frozen variable**. It is computed by a committed,
versioned instrument from repository evidence at the baseline. No LLM's
rating is authoritative. Optional human or LLM observation sheets may be
attached as an audit, and are reported but never used for classification.

Four quantities are recorded and **kept distinct**, as in
`docs/planning.md`: **affinity**, **dependency**, **collision risk** and
**parallel safety**. Dependency edges count as one affinity signal, but the
dependency structure (sequence constraints) is reported separately. Collision
risk and parallel safety come from the planner and are never folded into
affinity.

### Pairwise affinity signals

Each is computed per unordered member pair, with the shared elements listed
so every cell can be audited. Signals marked **strong** mean one item's
decisions bind the other's.

| ID | Signal | Deterministic source | Strength |
| --- | --- | --- | --- |
| A1 | Explicit relation | structured `dependsOn`; item-record Dependencies sections classified into hard (depends on / extends / gates) and coupling (integrates / feeds / consumes / supplies) clauses, ranges expanded (the `signal_structure.py` classifier) | strong if hard, weak if coupling only |
| A2 | Shared production paths | files and modules named in item text or acceptance criteria and resolved against the baseline tree, plus files defining identifiers named in the item text | weak |
| A3 | Shared domain types | identifiers named in both items that resolve to type or module definitions at the baseline, or to a contract another eligible item defines | strong |
| A4 | Shared invariants and requirements | both items cite the same requirement, requirement group, invariant or decision ID (`RQ-`, `PRX-`, `DF-`, repository requirement-group IDs) | strong |
| A5 | Shared storage or API boundary | both name the same persisted path, schema, wire contract, CLI command or route family | strong |
| A6 | Shared tests and fixtures | baseline test files that reference identifiers or paths named by both items | weak |
| A7 | Historical co-change | over the last 200 first-parent commits at the baseline, Jaccard of the commit sets touching each item's A2 path set is at least 0.2 | strong when at least 0.2 |
| A8 | Architecture-tag overlap | shared non-generic tags (planner signal) | weak |

**Generic elements are excluded** from every signal and listed per
repository before computation. These are paths and identifiers touched by
most changes, for example a CLI dispatcher, a command reference, or
repository-wide validation. Shared governance documents, build tooling and
ordinary test harnesses never count, as in A022.

### Cohort affinity measures

- **Primary moderator, structural affinity `A_b`**: the fraction of member
  pairs with **at least two independent strong signals** (counting A1 hard,
  A3, A4, A5 and A7). This is a count-based proportion in [0, 1] with no
  weights.
- **Profile**: per-signal pair coverage, the minimum per-member connectivity,
  and the median number of signals per pair.
- **Planner corroboration** (secondary): planner group membership, affinity
  label and confidence, the planner's estimated context reuse,
  `collision risk` and `parallel safe`.

### Classification rules

- **High**: `A_b >= 0.6`, and every member has at least one strong signal
  with at least half of the other members.
- **Low**: all of A022's low-affinity constraints hold. These are: no
  explicit relation (A1, hard or coupling) between members; no shared
  invariant (A4) and no shared storage or API boundary (A5); no predicted
  shared production file (A2, generic elements excluded); no common bounded
  feature; and planner evidence that at least two members could safely
  execute concurrently. In addition, `A_b <= 0.1`.
- **Medium**: everything else.

### Known instrument limits (from EV-ROS-2026-A072)

The planner reads only machine-readable metadata. On Signal it rated tightly
coupled core items "affinity none" because their dependencies existed only
in Markdown. The planner therefore can **never** establish low affinity on
its own. A1 must read item records, and the low rule requires agreement from
the structural signals.

### Manipulation check (pre-registered, post hoc)

After all arms finish, **realized** pairwise overlap is measured from arm
`I_b`, where each item was implemented separately. This is the Jaccard
overlap of changed production files, together with shared new types and
shared store or format changes. Prediction: the realized overlap in
high-classified blocks exceeds that in low-classified blocks. If the frozen
classification does not order realized overlap, the moderator is declared
mis-measured. The result is then classified "insufficient evidence"
(measurement) rather than reinterpreted. The classification is never
revised.

## Task-size controls

Scale is measured for every item and cohort before selection by the same
instrument:

| Measure | Source |
| --- | --- |
| Acceptance-criteria count | item description or detail record |
| Requirement-group count | distinct requirement or requirement-group IDs cited (including dated extensions) |
| Expected production files | size of the A2 path set (a lower bound; recorded as such) |
| Test burden | criteria that require tests, fixtures or verification programs, plus the A6 test files |
| Dependency depth | longest hard-relation chain from the item within the repository backlog |
| Implementation surface | distinct projects or modules among the expected files |
| Estimated effort | planner estimate when available, else recorded as unavailable |

**Scale classes** (frozen thresholds):

- **S (small)**: no more than 5 expected production files, no more than 3
  requirement groups, and dependency depth no more than 1.
- **L (subsystem)**: more than 15 requirement groups, or more than 15
  expected production files, or the item defines a contract that other
  backlog items consume.
- **M (medium)**: everything else.

A cohort's class is the class of its largest member.

Rules:

- **Primary comparisons are made only within one scale class.** High and low
  blocks from different classes are never contrasted in the primary
  analysis.
- Within a class, the selected high and low cohorts' summed
  acceptance-criteria counts and summed expected production files must each
  be within a factor of 2 of each other (high/low ratio between 0.5 and 2.0),
  or the pair is not formed.
- Because every item has the same five criteria in some repositories
  (Signal), acceptance-criteria count alone never establishes a match.
- Ratios are also reported **per item** (`ln(I/G)/n`) as a sensitivity
  check against cohort-size differences.

## Treatment procedures

The A021, R2 and A022 procedures carry over unchanged unless stated here.

Common to both arms, within a block:

- the same baseline `B_b`, created as two branches at `B_b`;
- the same provider, model identifier, runtime version, repository
  instructions, environment and pre-granted permissions; unavoidable drift
  is recorded;
- each arm receives only its own items' requirements and acceptance
  criteria, plus normal repository state;
- no arm reads its paired arm, any other block's arm, or this experiment's
  design records (the design lives in `kemiller2002/praxis`, which is not a
  block).

**Grouped arm (G).** One fresh session handles the whole frozen cohort.
Before production mutation it commits a cohort analysis (the PRX-GRP-040
template, including the reuse inventory). It then implements members
incrementally, preserving each member's lifecycle, acceptance criteria,
evidence, commits and checkpoints. Shared infrastructure is attributed
explicitly. Before completing any member it runs the per-member,
per-criterion verification pass (PRX-GRP-045).

**Independent arm (I).** One fresh session per member, run serially in a
frozen order (ordinal item ID unless a hard dependency requires otherwise),
on one branch, each starting from the previous session's pushed head. No
private context is carried between sessions. Serial execution holds
concurrency constant, so the experiment measures shared reasoning context,
not parallel scheduling.

A failed or abandoned session is kept as part of its arm and recorded. It is
never silently replaced. A rerun is a new replication with its own
identifier.

## Randomization

After the protocol, the instruments, the baselines and all cohorts are
frozen:

- the **block execution order** and, within each block, the **arm order** (G
  first or I first) are drawn at random;
- a salted SHA-256 commitment to both draws is committed before the first
  session. The salt is stored outside the evaluators' reach and revealed
  with the results. The A021 R2 lesson applies: the salt must be durably
  escrowed, for example in the orchestrator's sealed run record, so the
  commitment can be verified at unblinding.

## Replication and noise

- Every arm in every block runs once (R=1).
- To estimate run-to-run variance, at least **two blocks** (one high, one
  low, chosen at random among eligible blocks before execution) run both arms
  **twice** (R=2), with independent sessions from the same baseline.
- The noise band is the larger of the two absolute replicate differences in
  `ln(I/G)` for each primary measure. Effects inside the noise band are
  treated as no effect.

## Blinding

As in A022. Evaluators see neutral pair labels per block (for example
"Block 3: arm 1 and arm 2"), are told which two implementations share a
cohort, and are not told the arm identity, the block's affinity class, its
scale class, any resource data or branch names. A salted mapping commitment
is committed before evaluation and revealed only after the evaluation
reports are durable. One fresh evaluator, who implemented nothing, evaluates
each block. One evaluator may cover several blocks, but never a block whose
arms it has seen unblinded.

## Metrics

Collected per arm, with unavailable values recorded as unavailable and never
as zero. Sources are platform session records, Praxis telemetry, and the
A021 transcript instrument (`EX-ROS-2026-A021-harness/session_metrics.py`,
used unchanged and pinned by hash).

**Resources and context.** Platform cost; elapsed and active time; input,
output, cache-read and cache-write tokens; model requests; tool calls; file
reads; repeated file reads; searches; governance and architecture reads;
builds; test runs; retries; failed approaches; time to first productive
mutation; context resets; compactions; merge conflicts; peak context.

**Setup cost (new).** For every session, the **setup phase** is defined as
everything before the session's first read of an item-specific file or first
production mutation. It covers repository instructions, governance reads,
environment inspection and initial builds. Setup tokens, time and reads are
recorded per session. This makes the generic setup-cost mechanism directly
measurable.

### Primary measures (frozen)

1. **Resource primary**: platform cost, `r_b = ln(I_b / G_b)`.
2. **Mechanism primary**: repeated discovery, defined as repeated file reads
   plus repeated searches plus governance and architecture reads,
   `d_b = ln(I_b / G_b)`.
3. **Setup-adjusted primary**: `r*_b = ln((I_b - (n_b - 1) * s_b) / G_b)`
   for cost, where `s_b` is the median per-session setup cost in `I_b`. This
   removes the cost that merely having `n` fresh sessions imposes. If the
   adjusted numerator is not positive, the value is recorded as undefined and
   reported.

All other resource measures are secondary and are reported as ratios
without selection.

## Quality guardrails

The blind evaluation reports, per implementation, concrete and evidenced
findings on:

- acceptance defects;
- regressions;
- duplicated abstractions;
- conflicting abstractions;
- domain-rule consistency;
- API, storage and serialization consistency;
- test gaps and missing adversarial cases;
- unnecessary dependencies;
- maintainability and change surface;
- cross-item rework.

There is no aggregate quality score. After unblinding, each block is
classified as grouped comparable or better, mixed local tradeoff, grouped
materially worse, or insufficient evidence. A block's resource result counts
toward support only if its quality classification is not "grouped
materially worse".

## Analysis plan (frozen)

Primary analysis runs over blocks in the scale class that holds the minimum
design. Other classes are reported descriptively.

1. **Within-block effects**: `r_b`, `d_b` and `r*_b` for every block.
2. **Moderation test**: the Spearman correlation between `A_b` and each of
   `r*_b` and `d_b`, with an exact permutation p-value over block labels,
   plus the **separation statistic**: are all high blocks' values greater
   than all low blocks' values?
3. **Repository-effect checks**:
   - leave-one-block-out, where the conclusion must not flip;
   - the between-repository spread within an affinity class must be smaller
     than the high-versus-low difference;
   - per-block repository characteristics (language, size, test-suite
     runtime, governance volume) are reported and correlated with `r_b`;
   - if a double block exists, its within-repository contrast must agree in
     direction with the cross-block contrast.
4. **Scale check**: the Spearman correlation between the scale index and
   `r_b` within the primary class. Repeat the moderation test using per-item
   ratios.
5. **Context pressure**: compactions, peak context, criteria verified as not
   met, and late inconsistent decisions in G arms, compared by affinity
   class.
6. **Noise**: every effect is compared with the noise band.

No metric, threshold, rule or class is chosen after seeing results.

## Pre-registered interpretations

Each result is classified as exactly one primary interpretation, with
qualifiers allowed:

1. **Affinity / context-reuse mechanism supported**: `r*` and `d` increase
   with affinity (complete separation, or Spearman at least 0.6 with exact
   p of 0.10 or less in the preferred design); high blocks show the A021
   architecture advantage and low blocks do not; no high-block quality loss;
   the manipulation check passes.
2. **Generic setup-cost mechanism**: raw `r_b > 0` in most blocks of both
   classes, `|r*_b|` within the noise band in both classes, and no
   separation by affinity.
3. **Context-pressure threshold**: high blocks benefit, while low blocks
   have `r*` of 0 or less together with compaction, peak-context or
   forgotten-criterion evidence in their G arms.
4. **Repository-specific effect**: any repository-effect check fails
   (leave-one-out flip, between-repository spread as large as the class
   difference, or a double block contradicting the cross-block direction).
5. **Task-scale effect**: the scale check correlates at least as strongly as
   affinity, or the moderation disappears under per-item normalization.
6. **No reproducible grouping effect**: `|r_b|` and `|d_b|` are within the
   noise band in most blocks of both classes.

Two further outcomes are possible:

- **Quality tradeoff**: grouped is cheaper but materially worse on
  acceptance correctness in one or more high blocks.
- **Insufficient evidence**: protocol deviations, a failed manipulation
  check, missing primary metrics, or an unmet minimum design.

## Falsification criteria

- `HY-ROS-2026-A029` is **weakened** if low-affinity blocks show the same or
  larger setup-adjusted benefit (`r*`) or discovery reduction (`d`) than
  high-affinity blocks (median of low at least median of high, or Spearman
  of 0 or less), without corresponding cohesion evidence in the low blocks.
  It is also weakened if the high blocks lose acceptance quality.
- `HY-ROS-2026-A028` is **weakened** if, in the high-affinity blocks, G does
  not reduce repeated discovery relative to I (`d_b` of 0 or less in most
  high blocks), does not improve architectural consistency, or does so only
  with a quality loss.

A null or contrary result is a successful experiment. Rules and
classifications are not altered to preserve a hypothesis.

## Signal's anticipated role

`kemiller2002/signal` is a plausible future **high-affinity, scale-class L**
block. For example, its answer, rules and scoring items (WI-0004, WI-0006,
WI-0007) are hard-linked and share the WI-0002 contracts
(`EV-ROS-2026-A072`). It is **not** pre-selected. At inventory time its
items must be genuinely ready under Signal's own lifecycle, which first
requires GH-5's baseline to be merged and completed. Its actual relation
graph and scale must also be re-measured. Signal is never used as a low
block.

Because its items are subsystem-scale, Signal can enter the primary analysis
only if class L also has eligible low-affinity blocks. Otherwise it is
reported descriptively, or treated as a replication of A021 at a different
scale.

## Stopping rules

- An arm is never stopped because early metrics look unfavourable.
- An arm is stopped only for safety, unrecoverable infrastructure failure,
  or a protocol violation that invalidates it. That block is then recorded
  as deviated and excluded from the primary analysis, never rerun under the
  same identifier.
- If, after execution, fewer valid blocks remain than the minimum design,
  the result is "insufficient evidence". The experiment is not extended by
  recruiting blocks after results are seen, unless a new replication
  identifier and a fresh preregistered inventory are used.
- No implementation arm is merged into its repository before the block's
  blind evaluation and unblinding. Merging afterwards is the repository
  owner's decision.

## Deviations

Every deviation is recorded in the run record when it is observed, with the
block, arm, time, cause and effect on the primary measures. This includes
model or runtime drift, a stale start commit, a permission block, a failed
session, missing telemetry, an evaluator exposure, or an instrument defect.
An instrument defect discovered after freezing is fixed in a new instrument
version. The frozen classification still stands, and the defect's effect is
reported.

## Required artifacts before any arm starts

Under `research/experiments/EX-ROS-2026-A023-setup/` (plain-text files):

- the candidate repository list with inclusion reasons;
- the affinity and scale instrument (versioned, with tests and a
  reproduction command), the generic-element lists per repository, and
  SHA-256 sums;
- the inventory outputs per repository at frozen SHAs, and the planner
  outputs;
- all candidate cohorts with their affinity and scale profiles, the
  selection log with rejections, and the selected blocks;
- the matching table per scale class;
- the randomization and blind-mapping commitments;
- the exact G and I prompts, environment parity notes and the orchestration
  procedure;
- the metric collection procedure, including setup-phase extraction;
- the evaluator prompt and findings template;
- the replicate-block draw.

## Completion criteria

The experiment is complete only when:

1. every selected block has completed both arms or has a recorded
   invalidating deviation;
2. every block's blind evaluation is committed before unblinding;
3. the mappings and randomization are unblinded and verified against their
   commitments;
4. all within-block measures, the setup adjustment, the manipulation check,
   the noise band and the analysis plan are computed from recorded sources;
5. one pre-registered interpretation is assigned and the falsification
   criteria are evaluated;
6. results are recorded as evidence, `HY-ROS-2026-A028` and
   `HY-ROS-2026-A029` are updated, and A022's status is revisited;
7. raw and derived artifacts reproduce from immutable SHAs.

## Cost estimate (planning only)

With six blocks of about five items, plus two replicate blocks, there are
about 8 G sessions, about 40 I sessions and 6 to 8 evaluator sessions. At the
A021/R2 per-arm costs ($9 to $26 per arm), platform cost is about $250 to
$450, plus orchestration. This is an estimate, not a measurement.

## Current status

Preregistered on 2026-10-02 under `PRAXIS-PLAN-EXP-03`, after A022's target
gate failed (`EV-ROS-2026-A072`) and **before** any A023 inventory, cohort or
instrument run. No block, baseline or cohort has been selected, and no
session has run. Next step: build and freeze the affinity and scale
instrument, then run the target inventory.
