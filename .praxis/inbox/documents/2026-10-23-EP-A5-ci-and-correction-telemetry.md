# EP-A5: Record CI executions and correction cycles per work item

Priority: P2
Owner system: Praxis (execution telemetry)
Target repositories: praxis
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Program: kemiller2002/cohort-based-engineering `program/IMPLEMENTATION-PLAN.md` A5.

## Finding

- **Telemetry already covers a lot.** It records tokens, cost, wall time, verified requirements and rework, and keeps
  unavailable values distinct from zero.
- **Two measures the program depends on are missing:**
  - **CI executions and minutes per work item.** CI appears only as planner observations (PRX-PLAN-020).
  - **Correction cycles as a defined metric.** The reliability report's rework frequency is only a proxy.
- **This intake adds only those two.** Token completeness is already covered by REL-TOKEN-001/002, PRAXIS-REL-004 and
  VAC-CONT-001-R2. Session-aware cost is VAC-002-02, forwarded from cohort-based-engineering.

## Requirements (proposed)

- **EP-A5-R1.** At completion, record per work item `ci.executions` and `ci.minutes` from the CI provider's runs on
  the item's commits. When the provider is unreachable or unauthenticated, record `unavailable`, never 0.
- **EP-A5-R2.** Define `correction.cycles` as completion refusals plus post-completion reopenings, derived from
  recorded events.
- **EP-A5-R3.** The reliability report shows cost per accepted requirement, with the availability of each component.
- **EP-A5-R4.** Register the new metric ids in the metric registry; unmapped provider data stays in raw telemetry.

## Acceptance evidence

Tests for available and unavailable CI data, for correction-cycle derivation, and for the report fields.
