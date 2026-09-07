---
id: SDE-METHOD-005
title: Engineering Metrics
status: draft
version: 0.1.0
created: 2026-09-02
updated: 2026-09-02
related_documents:
  - reference/GLOSSARY.md
  - method/AGENT-EXECUTION-RULES.md
tags: [method, metrics]
---

# Engineering Metrics

Status: RECOMMENDED. Distinguishes routine engineering metrics (collect on
every real project) from research-only metrics (belong in a controlled
experiment, not routine engineering telemetry).

## Priority engineering measures (collect routinely)

- Search operations
- Repair loops
- Semantic decisions
- Boundary decisions
- Manual discoveries
- Required change sites
- Silent semantic failures (found by deliberate no-op-style challenge or by
  production incident)
- Integration failures
- Build/test attempts
- Files inspected
- First trustworthy detection stage (which row of
  `method/VERIFICATION-METHOD.md`'s enforcement map actually caught this
  defect)

## Where reliable orchestration data is available, also capture

- Tool calls
- Tokens
- Elapsed duration

## The evidence-class rule (REQUIRED)

> Agent self-report and orchestrator-observed telemetry are different
> evidence classes.

Experiment 3 [EV-HN-2026-0005] demonstrated directly that an agent may not
have access to its own authoritative usage metrics (both agents in that
trial correctly reported "unavailable"), while the orchestrating
infrastructure held authoritative figures neither agent could see. It also
demonstrated that even orchestrator-held telemetry can be an incomplete
figure that reads as complete unless the caveat is carried forward: the
harness figures for Condition B covered only a resumed portion following an
interruption, with ~78% of that condition's own eventual log preceding the
counted window.

**Rule:** never estimate unavailable telemetry. Label every reported figure
with its evidence class:

- `SELF-REPORT` — the executing agent's own account.
- `HARNESS-TELEMETRY (complete)` — orchestrator-observed, covering the
  entire run.
- `HARNESS-TELEMETRY (partial)` — orchestrator-observed, but known to cover
  less than the entire run; state explicitly what is missing and why, and
  never present a partial figure's percentage delta against a complete
  figure as a confirmed comparison.
- `NOT OBSERVABLE` — genuinely unavailable in this environment. Use this
  label; never substitute an estimate.

## What counts as a research-only metric

Metrics defined specifically for a controlled A/B trial (e.g., the precise
as-experienced vs. architecture-potential MDR reconciliation Experiment 3
introduced to make two agents' incompatible self-reports comparable) belong
in the experiment's own metrics schema, not in routine per-project
engineering dashboards, unless a project is itself running a controlled
comparison. Routine engineering work should track the "priority engineering
measures" above; it does not need MDR/MaDR/BCA-style research metrics
computed on every change.

## Metrics diagnose; they are not targets to game

Per `framework/REP-SPECIFICATION.md`'s own Quality Metrics guidance: define
the denominator or rubric for any percentage reported, and do not let a
metric override evidence quality. A change that minimizes "search
operations" by skipping verification is not an SDE improvement; it is a
metric being gamed.
