---
id: EV-ROS-2026-A072
title: EX-ROS-2026-A022 target-repository gate outcome
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-02
updated: 2026-10-02
research_area: repository-operating-system
evidence_type: primary
supports: []
contradicts: []
related_documents:
  - EX-ROS-2026-A022
  - EX-ROS-2026-A023
  - HY-ROS-2026-A028
  - HY-ROS-2026-A029
  - requirements/PLANNING-WORK-GROUPS.md
  - research/experiments/EX-ROS-2026-A022-target-gate/README.txt
tags: [planning, grouping, affinity, experiment, target-selection, feasibility]
confidence: high
provenance:
  contributions:
    EXE-20261002T122339366Z-b15d6ac8:
      operations: [created]
      at: 2026-10-02T12:32:11.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Recorded the EX-ROS-2026-A022 target-repository gate outcome (no eligible target across 14 inspected repositories, Signal analysed) without changing the gate"
derived_from: [EX-ROS-2026-A022]
---

# Evidence

## Claim

EX-ROS-2026-A022 remains unexecuted because no inspected repository
satisfies the preregistered same-repository requirement for both a matched
high-affinity cohort and a low-affinity control cohort.

The gate exposed an experimental-design constraint rather than an absence of
genuine implementation work.

This record is about **feasibility only**. It neither supports nor
contradicts HY-ROS-2026-A028 or HY-ROS-2026-A029: no arm ran, so no
implementation effect was observed.

## 1. Selection protocol

The gate applied is the one in EX-ROS-2026-A022 ("Target-repository gate"
and "Pre-registered affinity classification"), merged to `main` in PR #145
(2026-10-01) **before** any candidate was inspected. It was applied unchanged:

1. the target must not be `kemiller2002/praxis`;
2. inventory all ready work at a frozen baseline, creating no tasks;
3. one repository must supply at least five genuine ready high-affinity items
   **and** at least five genuine ready low-affinity items (ten in total);
4. reject the repository if either cohort would need padding or newly
   invented obligations;
5. do not substitute two repositories, which would confound repository with
   affinity.

"Ready" means the effective lifecycle state Praxis itself reports
(`work list`, which prefers the live in-flight record over the backlog's raw
status field; `docs/work-protocol.md`, "Local backlog"). It does not mean the
raw `status` field in `queue.json`. That raw field is misleading on its own:
it reads `ready` for 22 limen items and 103 strata items that are complete.

## 2. Candidates inspected

Thirteen repositories were on the allowlist supplied with the selection
request (`kemiller2002/praxis` was readable for protocol only and excluded as
a target). `kemiller2002/signal` was inspected afterwards at the owner's
request and is part of the final candidate set.

The inventory was re-derived on 2026-10-02 at the frozen SHAs below by
`research/experiments/EX-ROS-2026-A022-target-gate/inventory.py`. That script
runs Praxis 3.6.0 `work list --json` against a throwaway copy of each
repository. Its output is `inventory.json`, and `--check` reproduces it.
Every repository's `main` was unchanged between the first pass and the
re-derivation.

| Repository | Baseline SHA | Pinned / installed ROS | Items | Effective states | Ready |
| --- | --- | --- | ---: | --- | ---: |
| forma | `1564993` | 3.1.4 / 3.1.4 | 52 | 46 complete, 5 captured, 1 active | 0 |
| forma-studio | `e8ea17b` | 3.1.4 / 3.1.4 | 13 | 13 complete | 0 |
| folio | `eefceba` | 3.1.4 / 3.1.4 | 18 | 17 complete, 1 captured | 0 |
| limen | `303b7be` | 1.2.1 / 3.1.3 | 146 | 143 complete, 3 captured | 0 |
| iter | `321767f` | 3.6.0 / 3.6.0 | 1 | 1 complete | 0 |
| dokimos | `e641048` | 3.1.4 / 3.1.4 | 30 | 27 complete, 1 captured, 2 active | 0 |
| vigila | `bc02edd` | 3.0.3 / 3.1.4 | 31 | 24 complete, 7 captured | 0 |
| chrona | `495db07` | 2.0.1 / 3.1.3 | 9 | 7 complete, 1 blocked, 1 active | 0 |
| summa | `6290d36` | 2.0.1-main.78.1 / 3.1.4 | 7 | 6 complete, 1 active | 0 |
| strata | `3512d32` | 2.0.1 / 3.1.4 | 105 | 104 complete, 1 ready (`WI-0101`) | 1 |
| conditor | `a925ca5` | none | 0 | no Praxis backlog | 0 |
| aegis | `e629468` | 3.0.0 / 3.1.4 | 188 | 182 complete, 6 blocked | 0 |
| percepta | `6df2dbc` | 3.4.0 / 3.4.0 | 6 | 6 complete | 0 |
| **signal** | `637218a` | 1.2.1-main.16.1 / 3.1.4 | 27 | 7 complete, 19 captured, 1 active | 0 |

Full SHAs are in `inventory.json`. For summa, vigila and chrona, every remote
branch was also inspected, and none holds a ready item on any branch.

## 3. Why every candidate failed

Twelve of the fourteen fail the ready-count requirement alone. The largest
number of unfinished items in any one of them, counting every non-complete
state, is seven (vigila: seven `captured` SDE-conformance and UI-decision
items). That is still below ten before affinity is considered. strata has
one ready item. conditor has no Praxis backlog. iter, forma-studio and
percepta have only completed work.

Reaching ten would require promoting captured items to ready, or inventing
items, for the experiment's sake. The gate forbids both.

Signal fails differently. It is analysed next.

## 4. Signal findings

Signal has substantial genuine work: **19 specified backlog items**,
`WI-0002` to `WI-0020`, migrated from the requirement corpus under
`input-documents/` (`source: requirements-migration`; captured 2026-08-31
and 2026-09-22, three to four weeks before A022 existed). Each has a detail
record (`.ros/work/items/WI-*.md`) with five acceptance criteria, a
reciprocal requirement-group table, an explicit Dependencies section and, for
most, dated requirement extensions. The work is real. What disqualifies
Signal is its shape. Measurements below come from
`research/experiments/EX-ROS-2026-A022-target-gate/signal/signal_structure.py`
(output `signal-structure.json`, reproducible with `--check`) and from the
deterministic planner run on a copy (`signal/plan-groups.*`,
`signal/plan-analyze.json`).

### 4.1 Readiness failure

- `WI-0002` to `WI-0020`: all **captured** on `main` and on every one of
  Signal's eight remote branches. None is ready, and the planner reports each
  as `not-runnable, captured`.
- `GH-5` ("Prepare Signal implementation baseline"): **active** in the live
  record since 2026-09-22T22:12:02Z, with no durable checkpoint and an open
  telemetry execution. `queue.md` shows it as `ready` only because that
  projection is regenerated on backlog writes, not on `work start`.
  Reproduced on Praxis 3.6.0 and filed as `PRAXIS-QUEUE-MD-LIVE-STATE`.
  Details: `signal/gh5-state-investigation.txt`.
- On `main`, `src/` holds only two README files, and `HANDOFF.md` and
  `context/CURRENT-STATE.md` describe a draft-charter bootstrap. GH-5's
  four-tier skeleton and accepted first slice ("Publish, Respond, Score")
  exist only on the unmerged branch `work/gh-5-readiness` (`0c018eb`). That
  branch has no pull request, and its own verification script passes.

The backlog items are not implementable until GH-5 lands. Promoting them to
`ready` for the experiment would be padding.

### 4.2 Low-affinity-control failure

The A022 low-affinity cohort requires no dependency edges between members,
no shared domain invariant, no common storage or API decision, no predicted
shared production file and no common bounded feature.

- **Relation density.** The detail records state 125 explicit relations
  (72 hard: depends on / extends / gates; 53 coupling: integrates /
  collaborates / feeds / consumes / supplies). These directly connect 84 of
  the 171 possible item pairs. No item is isolated, and every item has at
  least six direct partners.
- **No edge-free cohort.** Across all 11,628 five-item subsets of the 19
  items, **0** have no explicit relation between their members. Counting
  only hard relations, 31 subsets qualify.
- **Shared domain contract.** Every item other than WI-0002 is linked to
  WI-0002 by hard relations (depends on, extends, or gated by the
  verification programs WI-0010 and WI-0020). WI-0002 "establishes the
  contract baseline consumed by WI-0003 through WI-0009": the canonical
  SurveyTemplate, SurveyInstance, SurveyResponse, SurveySubmission,
  SurveyGroup, SurveyResult, SurveyGroupResult and AdminReportState
  contracts. All 31 hard-edge-free subsets consist entirely of members linked
  to that contract, so every candidate low cohort shares a domain invariant.
- **Planner caution.** From Signal's machine-readable metadata alone, the
  deterministic planner rates WI-0002, -0003, -0004, -0006 and -0007 as
  "affinity none". Their dependencies are recorded only in the Markdown
  detail records, and `queue.json` carries no `dependsOn`. A planner-only
  selection would therefore have produced a false low-affinity cohort out of
  the most tightly coupled core items. A022's requirement for corroborating
  structural signals is what catches this.

Signal can therefore supply a natural high-affinity cohort but not, from the
same repository, the low-affinity negative control.

### 4.3 Matching and scale concern (a confound, separate from eligibility)

The items are subsystem-scale. By distinct requirement-group IDs (original
migration plus later extensions), WI-0002 cites 25 groups across 11 source
documents (18 groups in its original table). WI-0007, the scoring engine,
cites 42. WI-0010, the cross-cutting test program, cites 52. Across the 19
items the range is 4 to 52.

Every item has exactly five acceptance criteria, so acceptance-criteria count
does not track scope. A five-item cohort would amount to building a large
part of the product. Comparing such a cohort with a cohort of
ordinary-sized items would confound affinity with task scale. This is a
matching problem for any design that uses Signal, not a judgment that the
items are wrongly sized.

**Summary:** Signal is a strong potential future high-affinity subject whose
domain-wide shared contracts make it unsuitable for the same-repository
low-affinity negative control required by A022.

## 5. No implementation arm ran

- No baseline `B` was frozen, no cohort was frozen, and no `experiment/a022-*`
  branch exists (checked by remote branch listing on 2026-10-02).
- No work item in any candidate repository was created, promoted, split,
  edited or transitioned for A022. Signal was not modified at all.
- No two-repository substitution was made.

### Superseded earlier design (recorded for completeness)

Branch `claude/praxis-a022-experiment-setup-xpq6a6` (seven commits,
2026-10-01 10:44Z to 11:19Z) contains an earlier, unmerged A022 setup. It
forked from `main` at `f68565f`, before the protocol now on `main` was
preregistered (PR #145; its EX-ROS-2026-A022 was created 2026-10-01 16:06Z).
That setup:

- targeted `kemiller2002/praxis` itself;
- captured and triaged its five high-affinity cohort items itself, with its
  own acceptance criteria, at the baseline;
- claims IDs that collide with records on `main` (its own EX-ROS-2026-A022
  and HY-ROS-2026-A029 under different titles) and claims
  EV-ROS-2026-A071.

Both of the first two choices are excluded by the protocol on `main`. The
branch ran no arm. It is neither merged nor deleted by this work, and this
record does not use EV-ROS-2026-A071, to avoid adding a further ID
collision. Its pairwise, twelve-dimension affinity model is reusable design
input and is cited as such by EX-ROS-2026-A023.

## 6. Interpretation

1. **The gate worked as designed.** It prevented an execution that would
   have needed manufactured cohorts.
2. **Real backlogs in this repository family are poorly shaped for a
   same-repository two-cohort design.** Mature repositories have completed
   their queues (limen 143, aegis 182, strata 104 complete). Young ones hold
   a few captured items. The one rich backlog (Signal) is one connected
   domain. A repository that has both enough ready work and both affinity
   extremes at the same moment appears to be rare.
3. **The affinity mechanism remains untested.** A021 and R2 measured only the
   high-affinity half. A022 could not instantiate the low-affinity control.
   Confidence in HY-ROS-2026-A029 does not change.

## 7. Limitations

- The candidate set is the supplied allowlist plus Signal. Other repositories
  were not inspected, and the conclusion does not extend to them.
- "Ready" was read from Praxis state only. Open GitHub issues were not
  inventoried: API access was not available for the allowlisted repositories
  during the pass, and imported issues (`GH-*`) already appear in the queues.
  The gate counts ready Praxis work, so this does not change the outcome.
- The inventory is a snapshot. A repository can become eligible later
  through its own development.
- Signal's relation graph is read from human-written Dependencies sections,
  with a deterministic clause classifier (`signal_structure.py`). A different
  reading of "coupling" verbs would change the 0-subset figure, but not the
  shared-contract finding.

## 8. Implications for experiment design

- Requiring one repository to supply both affinity levels at once is the
  binding constraint. A follow-on design should compare grouped and
  independent execution **within** a repository and treat repository as a
  **blocking factor**, letting affinity vary **across** blocks. Simply
  assigning high affinity to repository A and low affinity to repository B
  would make repository and affinity indistinguishable.
- Affinity should be a frozen, measured quantity built from structural
  evidence, including dependency text in item records, not a planner label
  alone (see 4.2).
- Task scale must be measured and matched or modelled explicitly (4.3).
- Readiness is enforced by each repository's own lifecycle. An experiment
  waits for it and never creates it.

These implications are taken up by the preregistered follow-on
EX-ROS-2026-A023. EX-ROS-2026-A022 itself is unchanged in its criteria and
is marked `blocked`.

## Reproduction

    # clones of each repository checked out at the SHAs in inventory.json, in DIR
    python3 research/experiments/EX-ROS-2026-A022-target-gate/inventory.py \
        --clones DIR --praxis ./praxis \
        --check research/experiments/EX-ROS-2026-A022-target-gate/inventory.json
    python3 research/experiments/EX-ROS-2026-A022-target-gate/signal/signal_structure.py \
        --signal DIR/signal \
        --check research/experiments/EX-ROS-2026-A022-target-gate/signal/signal-structure.json
