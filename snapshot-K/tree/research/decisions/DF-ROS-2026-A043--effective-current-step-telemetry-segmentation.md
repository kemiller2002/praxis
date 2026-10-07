---
id: DF-ROS-2026-A043
title: Step telemetry is effective-current; an execution's segmentation boundary is derived from its own first step, never stored or backfilled
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A022]
supporting_evidence: []
related_documents:
  - RQ-ROS-2026-A022
  - DF-ROS-2026-A042
  - docs/development-telemetry.md
  - docs/work-protocol.md
supersedes: []
superseded_by: []
tags: [telemetry, steps, effective-current, continuity, schema-evolution]
confidence: medium
derived_from: [RQ-ROS-2026-A022]
provenance:
  contributions:
    EXE-20260929T101141946Z-cddc93be:
      operations: [created]
      at: 2026-09-29T10:20:45.229Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Effective-current step telemetry segmentation decision (PRAXIS-CONT-11-SEGMENTATION)"
---

# Context

Steps (`step.started` / `step.completed` / `step.failed` events on an
execution record, PRAXIS-REMOTE-04) let usage and evidence be attributed
below an execution. They arrived after many executions had already run, and
an agent may learn of them, or be required to use them, partway through an
execution. `RQ-ROS-2026-A022` (CONT-080..086) requires that adopting steps
never forces a restart, never invalidates history, and never produces
reconstructed precision. `DF-ROS-2026-A042` did not say how the boundary
between unsegmented and segmented telemetry is represented.

# Decision

1. **The boundary is derived, not stored.** An execution's segmentation is
   a pure function of its `startedAt` and its projected steps
   (`Ros.Domain.Telemetry.TelemetrySegmentation.derive`):
   - no step: `execution-level`;
   - first step started at the execution's start: `step-level`;
   - first step started later: `step-level-adopted`, with the period
     `startedAt .. first step` execution-scoped;
   - unknown start: step-level from the first step, with whether earlier
     activity existed reported as unknown.

   No `stepTrackingStartedAt` field is added to the execution schema, so no
   record is migrated and no history can be backfilled. The derived value is
   exposed read-only as `telemetry.executions[].stepTrackingStartedAt`.

2. **Measurements are never moved.** A measurement with a `step` dimension
   belongs to that step. One without it is execution-scoped: before
   adoption (step attribution unavailable) or outside any step after it.
   No code path splits, allocates or estimates execution-scoped usage into
   steps.

3. **Unavailable is not zero.** In `telemetry usage --by step`, every
   execution with an execution-scoped period belongs to the
   `(outside any step)` group. An execution that reported nothing there is
   listed as unavailable (`total` is a lower bound, `complete: false`).

4. **Validation is permissive about history and strict about references.**
   A measurement without a step is always legal. A measurement naming a step
   its own execution never started is a `validate` finding. That check is
   F#-only, beside the checkpoint findings, so the Node-parity telemetry
   validator is unchanged.

5. **Successors segment their own telemetry.** `work continue` creates a new
   execution. Its steps and usage are its own and nothing is appended to the
   predecessor, which keeps its segmentation as recorded.

# Consequences

- Existing repositories need no migration. Every historical execution reads
  as `execution-level` or as whatever its own step events show.
- `work context`, `status` and `work continue` gain an additive
  `telemetry` object inside each continuity block. The differential tests
  already exclude `continuity`.
- The principle generalizes: a later observability capability applies from
  when it is adopted, and earlier records keep a truthful gap.

# Alternatives considered

- **Store `stepTrackingStartedAt` on the execution.** Rejected: it would
  duplicate a fact the events already carry, and it invites backfilling
  historical records.
- **Allocate earlier usage to the first step, or proportionally.**
  Rejected: fabricated precision (CONT-082).
- **Require a new execution to adopt steps.** Rejected: it would restart
  valid work only to gain observability (CONT-081).
