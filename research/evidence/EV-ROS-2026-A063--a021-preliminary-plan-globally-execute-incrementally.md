---
id: EV-ROS-2026-A063
title: "Preliminary A021 observation: plan globally, execute incrementally may reduce AI context reacquisition"
status: draft
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-30
updated: 2026-09-30
research_area: repository-operating-system
evidence_type: primary
supports: [HY-ROS-2026-A028]
related_documents:
  - EX-ROS-2026-A021
  - EV-ROS-2026-A059
  - DF-ROS-2026-A047
  - requirements/PLANNING-WORK-GROUPS.md
tags: [planning, grouping, context, ai-execution, preliminary]
confidence: low
provenance:
  contributions:
    EXE-20260930T175751373Z-7eb9c757:
      operations: [migrated]
      at: 2026-09-30T17:57:56.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-07: authored by the repository owner in commit 3aef2bd without a provenance entry; this agent records the migration only and did not create or change the content"
derived_from: [3aef2bd]
---

# Preliminary A021 observation: Plan Globally, Execute Incrementally

## Status

This is a **preliminary observation**, not the result of
`EX-ROS-2026-A021`.

At the snapshot reviewed on 2026-09-30:

- the grouped arm had implemented all five cohort items and its tip was
  `5face886176839da30805c07fa893d2c18f3b5d2`;
- the control arm had complete session metrics for items 01 through 03 and an
  implementation commit for item 04 at
  `881a34dcbac9909e7efa8d2d41f7f491aa923a9a`;
- control item 04 recorded that `work start` was blocked by the session's
  permission policy, so its protocol state and metrics were incomplete;
- control item 05 had not run;
- the independent blind evaluation required by PRX-GRP-084 had not yet
  produced `EV-ROS-2026-A062` or its `findings.json`.

No hypothesis classification or execution-policy change follows from this
record alone.

## Quantitative signal before the control arm finished

The three completed control sessions already exceeded the one grouped session,
which implemented all five items, on several context-reacquisition measures.

| Measure | Control 01-03 only | Grouped 01-05 |
| --- | ---: | ---: |
| Wall time | 48.14 min | 60.36 min |
| Model requests | 148 | 114 |
| Output tokens | 103,125 | 110,627 |
| Cache-read tokens | 18,586,160 | 23,872,205 |
| Cache-created tokens | 430,682 | 289,298 |
| File reads | 91 | 52 |
| Searches | 104 | 53 |
| Builds | 22 | 16 |
| Test runs | 8 | 13 |
| Tool errors | 8 | 2 |
| Context acquisitions | 3 | 1 |
| Aggregate time to first code mutation | 16.67 min | 9.58 min |

The comparison is deliberately asymmetric: the control figures cover only
three of the five items. It is therefore not a final speed or cost comparison.
Its value is that the partial control arm had already performed more model
requests, searches, reads, builds, cache creation and tool-error recovery than
the grouped execution required for the complete five-item cohort.

Monetary cost remains unavailable and is not inferred from tokens.

## Candidate mechanism

The observation is consistent with a mechanism stronger than simple batching:

> For high-affinity work, an AI agent may benefit from reasoning about the
> whole requirement cohort and its architecture before production mutation,
> while still implementing, testing, committing and validating incrementally.

A fresh execution pays an epistemic startup cost: it must rediscover the
system, applicable governance, existing abstractions, invariants, prior
decisions and the implications of earlier work. When tightly related work is
split among fresh executions, that acquisition cost can be paid repeatedly.

A grouped execution can instead pay much of that cost once, establish shared
invariants and abstractions, and then implement the individual obligations
incrementally.

The candidate operating principle is named:

**Plan Globally, Execute Incrementally (PGEI).**

PGEI is not classic waterfall. It does not require completing all design
before any test, delaying feedback to the end, or preventing revision.
Implementation remains incremental; tests and compiler feedback are continuous;
and evidence that invalidates an architectural assumption causes replanning.

The distinction is:

`whole-cohort reasoning -> architecture/invariants -> incremental slices -> continuous validation -> replan when evidence requires it`

rather than:

`independent slice -> reacquire context -> local design -> discard context`
repeated for every tightly coupled slice.

## Preliminary architectural observation

This is not a substitute for the blind evaluator.

The grouped arm's pre-implementation analysis chose a shared aggregate and
persistence strategy, visible in a relatively unified implementation surface:

- `src/Ros.Domain/Work/WorkGroups.fs`
- `src/Ros.Contracts/Work/WorkGroupJson.fs`
- `src/Ros.Infrastructure/Work/FileWorkGroupRepository.fs`
- `src/Ros.Cli/WorkGroupCommands.fs`

By the control arm's item-04 snapshot, the independently evolved design had
separated declaration, view and membership behavior and introduced additional
persistence/contract surfaces, including:

- `src/Ros.Domain/Planning/GroupDeclaration.fs`
- `src/Ros.Domain/Planning/GroupMembership.fs`
- `src/Ros.Domain/Planning/GroupView.fs`
- `src/Ros.Contracts/Planning/WorkGroupJson.fs`
- `src/Ros.Contracts/Planning/WorkGroupMembershipJson.fs`
- `src/Ros.Infrastructure/Planning/FileWorkGroupRepository.fs`
- `src/Ros.Infrastructure/Planning/GroupMembershipStore.fs`
- `src/Ros.Infrastructure/Planning/WorkGroupStore.fs`

This difference may represent greater architectural fragmentation in the
independent arm, or it may represent a useful separation of concerns. Only the
blind acceptance/architecture evaluation should classify the difference.

The grouped shape also creates a falsification risk of its own: cohesion can
become concentration. Its larger shared modules must be examined for
monolithic responsibilities, not presumed superior merely because they are
unified.

## Proposed extension to HY-ROS-2026-A028

The existing hypothesis should be interpreted to include the following
candidate mechanism, subject to the completed experiment and replication:

1. **Global reasoning benefit.** High-affinity work benefits from
   whole-cohort requirements and architecture reasoning before mutation.
2. **Incremental execution benefit.** The resulting plan should still be
   implemented as separately testable, attributable and checkpointed slices.
3. **Context reacquisition cost.** Fresh executions impose measurable repeated
   discovery and architectural reconstruction cost.
4. **Context-pressure cost.** Increasing cohort size eventually imposes its
   own cost through context pressure, forgotten constraints, compaction or
   inconsistent late decisions.
5. **Cohesion threshold.** Grouping should therefore be selected by affinity
   and measured context pressure, not by a fixed batch size.
6. **Optimal cohort size.** The useful scheduling problem is to minimize the
   combined cost of context reacquisition, context pressure, coordination and
   rework while preserving quality.

A conceptual cost model is:

`total(group) = reacquisition(group) + context-pressure(group) + coordination/rework(group)`

The first term should generally fall as related work is grouped. The second
should eventually rise as the cohort becomes too large. Praxis should seek the
minimum rather than assume that either one-item execution or maximum batching
is universally best.

## Relationship to waterfall and sprint-style execution

The preliminary evidence does **not** establish that waterfall is superior to
Agile or iterative development.

It instead challenges one practice that can appear in sprint/story-oriented AI
execution: treating strongly coupled stories as independent reasoning
problems and expecting architecture to emerge without repeatedly paying
context-acquisition and reconciliation costs.

The candidate synthesis is:

- plan globally where work is strongly coupled;
- execute and validate incrementally;
- parallelize genuinely independent work;
- split a cohort when measured context pressure outweighs reuse;
- preserve each work item's acceptance criteria, lifecycle, attribution and
  evidence even when reasoning is shared.

## Decision gate

Do **not** make PGEI a Praxis default from this record.

Before a normative decision:

1. finish the original control arm without hiding its protocol deviations;
2. run the frozen blind evaluator and unblind only after its report is durable;
3. complete the planned A021 replication with isolated control executions;
4. compare quality, context-reacquisition and context-pressure evidence;
5. preferably replicate on at least one different high-affinity cohort.

If the result survives those checks, create a decision record for PGEI and
then update `docs/planning.md`, the planner requirements and scheduling
behavior. If replication contradicts the preliminary observation, retain this
record as evidence of the early signal and record the contradiction rather
than rewriting it away.

## Practical implication under test

The likely AI scheduling unit is not necessarily a sprint or an individual
story. It may be a **reasoning cohort**: the largest set of sufficiently
related obligations for which shared architectural understanding saves more
than the added context pressure costs.

That claim remains a hypothesis until the experiment and replication complete.

## Limitations

- One repository, one tightly related five-item cohort.
- The control arm was incomplete at this snapshot.
- The grouped arm's explicit up-front analysis was part of its treatment, so
  the observed effect cannot yet be separated into batching, planning and
  persistent-context components.
- Monetary cost was unavailable.
- No blind evaluator had classified architectural quality.
- The original cohort was intentionally high-affinity and may not generalize
  to loosely related work.
