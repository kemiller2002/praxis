---
id: DF-ROS-2026-A046
title: A deterministic, read-only F# planner recommends evidence-backed execution waves; unknown stays unknown and parallelism is conservative
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
decision_type: architecture
supporting_evidence: [EV-ROS-2026-A058]
related_documents:
  - EV-ROS-2026-A058
  - requirements/PLANNING-OPTIMIZATION.md
  - docs/planning.md
supersedes: []
superseded_by: []
tags: [planning, scheduling, telemetry, determinism]
confidence: medium
provenance:
  contributions:
    EXE-20260929T172554288Z-0f639674:
      operations: [created]
      at: 2026-09-29T18:08:50.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-01: advisory planner and its first shadow experiment"
---

# Context

Praxis records work state, checkpoints and execution telemetry but offers no
view of what to do next, in what order, or in parallel. The requirements in
`requirements/PLANNING-OPTIMIZATION.md` ask for a planner that informs
execution without performing it, and whose first release runs in shadow mode.

# Decision

1. **Advisory only.** `praxis plan analyze|simulate|compare|explain|replay|
   freshness` read through an Application port with no write member and print
   to stdout. Stale state is reported with a recommended reconciliation, never
   repaired.
2. **Pure deterministic core.** All planning logic lives in
   `Ros.Domain.Planning` with no clock, file, Git, network or LLM access; the
   CLI supplies the timestamp. Ordering is ordinal with explicit tie-breaks,
   and identical inputs render byte-identical logical plans.
3. **Evidence, not age.** State is stale only when evidence shows it: a
   checkpoint commit reachable from the integration branch, a merged PR or an
   existing tag named by a blocker, a completed prerequisite, or CI results a
   caller supplies. The planner never contacts GitHub or CI itself.
4. **Unknown is first-class.** Estimates are option-valued ranges with
   confidence. Monetary cost is unknown unless enough `cost.*` telemetry
   exists; the cost strategy and budget constraints then report unavailable or
   cannot-evaluate rather than a number.
5. **Explicit assumptions.** The fraction of effort a checkpoint's next action
   implies remains, and the balanced weights, are configuration reported in
   every plan and explanation, not hidden constants.
6. **Conservative parallelism.** A separate collision graph rates every pair
   safe, elevated, unknown or conflict. Unknown overlap and conflicts are never
   co-scheduled; shared Praxis state files make every pair elevated until
   `PRAXIS-STATE-MERGE-01`. Each strategy names the risk policy it applies.
7. **No universal optimum.** `compare` reports a Pareto frontier and
   diminishing returns instead of one best plan.

# Consequences

- The planner is safe to run anywhere, including CI, and its output can be
  saved and checked for staleness later.
- Duration estimates are only as good as execution telemetry; the first
  experiment (`EV-ROS-2026-A058`) shows they are weak today, so the planner
  states low confidence rather than overstating precision.
- Remaining-work fractions are uncalibrated assumptions until replay of
  continuations provides evidence.
- Autonomous execution remains out of scope; any move toward it needs a
  separate decision backed by shadow-mode evidence.
