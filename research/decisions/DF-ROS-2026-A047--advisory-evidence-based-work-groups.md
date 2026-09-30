---
id: DF-ROS-2026-A047
title: Work groups are advisory, evidence-based recommendations over unchanged work items; grouped execution waits for experimental evidence
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-30
updated: 2026-09-30
research_area: repository-operating-system
decision_type: architecture
supporting_evidence: [EV-ROS-2026-A059]
related_documents:
  - EV-ROS-2026-A059
  - EX-ROS-2026-A021
  - requirements/PLANNING-WORK-GROUPS.md
  - DF-ROS-2026-A046
  - docs/planning.md
supersedes: []
superseded_by: []
tags: [planning, grouping, determinism, attribution]
confidence: medium
provenance:
  contributions:
    EXE-20260930T102847967Z-f42c2262:
      operations: [created]
      at: 2026-09-30T11:17:12.025Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0064: evidence-based work groups and the frozen grouping experiment"
---

# Context

`requirements/PLANNING-WORK-GROUPS.md` asks whether related work should be
reasoned about, and executed, together, and requires testing that before any
grouped execution becomes default. `DF-ROS-2026-A046` already made planning
advisory, deterministic and evidence-based; this decision extends it.

# Decision

1. **Groups never replace work items.** A group records why items belong
   together. Members keep their own lifecycle state, acceptance criteria,
   evidence, attribution, telemetry and checkpoints; the group model has no
   field that could carry another member's changes (PRX-GRP-041..043).
2. **Explainable evidence only.** Each signal is labelled explicit, derived
   or inferred, in the PRX-GRP-022 priority. Title similarity and ID families
   are inferred and can never form a group; affinity that cannot be assessed
   is `unknown`, not `none`.
3. **Separate relations.** Context affinity, dependency, collision risk and
   parallel safety are computed separately. A group defaults to one reasoning
   owner and never to one agent per member; parallelism is recommended
   between groups under a named risk policy.
4. **Cohesion over size.** Deterministic clustering admits a member only
   with qualifying affinity to two thirds of the group, and splits (by size
   or by observed context pressure) and merges (by a shared architecture
   decision) are explained. Human declarations outrank inference.
5. **Execution happens where it belongs.** Every group names its execution
   repository; clustering never mixes repositories, and a mixed human group
   must be declared cross-repository or split.
6. **Read-only first.** Phase one is `plan groups`, `plan explain-group`,
   `simulate --groups` and `compare --groups`. The mutation surface (`work
   group ...`) and any grouped execution wait for `EX-ROS-2026-A021`.
7. **Savings are measured, not assumed.** Context acquisitions are counted;
   their token and time value stays unknown until measured.

# Consequences

- The planner can say which work to reason about together and why, but not
  yet whether doing so pays: that is `EX-ROS-2026-A021`.
- On this repository the model found no pre-existing cohort of the preferred
  size (`EV-ROS-2026-A059`), which is itself useful: most open work here is
  unrelated or untriaged.
- A single broad tag can attach a looser item to a strong group; the
  experiment pins its cohort by declaration and the rule is left for
  evidence-driven refinement.
- Group IDs of recommendations are stable for identical inputs only; durable
  IDs come from declarations (phase two would store them).
