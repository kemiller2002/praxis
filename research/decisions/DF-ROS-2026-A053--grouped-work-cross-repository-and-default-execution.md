---
id: DF-ROS-2026-A053
title: Grouped work v2 - first-class cross-repository groups, the full group command surface, grouped execution by default for high-affinity groups behind enforced gates, and measured context reuse and cost
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-06
updated: 2026-10-06
research_area: repository-operating-system
decision_type: architecture
supporting_evidence: [EV-ROS-2026-A064, EV-ROS-2026-A070]
related_documents:
  - DF-ROS-2026-A047
  - DF-ROS-2026-A046
  - DF-ROS-2026-A052
  - EV-ROS-2026-A064
  - EV-ROS-2026-A070
  - EX-ROS-2026-A021
  - requirements/PLANNING-WORK-GROUPS.md
  - requirements/PLANNING-OPTIMIZATION.md
  - docs/planning.md
  - docs/development-telemetry.md
supersedes: []
superseded_by: []
tags: [planning, grouping, cross-repository, execution, telemetry, cost, attribution]
confidence: low
provenance:
  contributions:
    EXE-20261006T194308568Z-5a1b95e5:
      operations: [created]
      at: 2026-10-06T19:48:13.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-PLAN-09: record the repository owner's four accepted grouped-work decisions (the owner approved; this agent only recorded them)"
      evidence: [EV-ROS-2026-A064, EV-ROS-2026-A070]
derived_from: [DF-ROS-2026-A047]
---

# Context

`DF-ROS-2026-A047` made work groups advisory and evidence-based. It deferred
three things: the group mutation surface, any grouped execution, and
cross-repository groups (PRX-GRP-052, "eventually"). It deferred them until
`EX-ROS-2026-A021` had results.

That experiment has now run twice on one high-affinity cohort.

- **First run (`EV-ROS-2026-A064`).** The grouped arm cost $9.48 against
  $21.51 on the platform, used 61 against 112 minutes of session time and one
  cold start against four or more, and produced one consistent model where
  the independent arm produced several. The independent arm met individual
  criteria more faithfully and reused more existing code.
- **Replication (`EV-ROS-2026-A070`).** Grouped cost 58.7 % less and took
  61 % less elapsed time. It again had the more coherent architecture, and the
  confirmed acceptance-defect count was tied.

Both records rate the evidence low-confidence: one cohort, one repository,
and orchestration deviations in the control arms. `EV-ROS-2026-A064`
recommended keeping grouping advisory after one run. It also recommended
adding a reuse inventory and a per-criterion verification pass, and making
context overhead and cost observable.

Meanwhile, cross-repository work is already happening outside Praxis.
Portfolio sweeps, such as adopting a Praxis release across the Echelon
repositories (the 3.7.1 sweep's findings became `WI-0073`), are run from one
shared brief. Each repository gets its own pull request, and status is kept
in a consolidated table by hand.

The repository owner reviewed these results and, in the session that
produced `PRAXIS-PLAN-09`
(https://claude.ai/code/session_01CmPhre2cFonbHerxVSsmrL), explicitly
accepted the four decisions below. This record writes them down. The owner
approved; the recording agent approved nothing.

# Decision

This record **amends** `DF-ROS-2026-A047`. Points 1 to 5 and 7 of A047 stand.
Point 6 ("read-only first; grouped execution waits for `EX-ROS-2026-A021`")
and the title's "grouped execution waits for experimental evidence" are
superseded as follows.

1. **Cross-repository groups are first class** (PRX-GRP-100..109; replaces
   PRX-GRP-052, amends 051).
   - A cross-repository group is `GROUP-ECHELON-<AREA>-<SEQUENCE>`, and its
     members are `owner/repo:WORK-ID`.
   - One home repository holds the only group record.
   - Each member repository holds only an immutable reference on its own
     item.
   - Group status is derived from dated, read-only observations of each
     member repository and is never asserted.
   - Each member keeps its own branch, commits, validation, evidence, pull
     request and completion.
   - Cross-repository order uses `complete`, `merged` and `released`
     milestones, and partial completion is reported per repository.
   - The home's group plan and checkpoints cover every repository.
   - No checkout ever executes, writes or schedules another repository's
     change.
2. **The group mutation surface is completed** (PRX-GRP-110..117; amends
   073).
   - `work group create|show|add|remove|checkpoint` exist and are kept.
   - `work group list`, `work group link` and `plan execute-group` are added.
   - Validation, JSON contracts and append-only audit of membership changes
     are specified.
   - Repeated identical mutations become idempotent.
   - Membership never alters a member's lifecycle or attribution.
   - Groups have no completion transition; their status is derived.
   - `plan execute-group` is the one mutating `plan` verb. It acts only
     through existing transitions and never launches an agent.
3. **Grouped execution is the default for high-affinity groups**
   (PRX-GRP-130..138; amends 040, 045 and the Objective).
   - Qualification is configurable: high affinity with no `none` or
     `unknown` pair, 2 to 6 runnable members, all members in this checkout,
     no limiting context-pressure evidence, and no captured members.
   - The reuse inventory and the per-member, per-criterion verification
     pass become hard completion gates. Each is a machine-checkable evidence
     document checked through the `DF-ROS-2026-A052` completion-readiness
     mechanism. The gates are why the default can move despite the weaker
     per-criterion fidelity seen in the grouped arm.
   - A group or an item can opt out with a recorded reason.
   - Context-pressure signals cause a recorded fallback to independent
     execution.
   - Determinism (PRX-GRP-075) and per-member attribution (PRX-GRP-041..043)
     are unchanged.
   - The default takes effect only once the gates exist.
4. **Context reuse and cost are measured, and the planner prices them**
   (PRX-GRP-150..158; amends 061).
   - The context metrics become provider-neutral adapter capabilities, and
     the time to first productive change is measured against configured
     meaningful paths.
   - Platform cost is recorded as `cost.execution_total`.
   - A group execution's shared session is ingested once.
   - Shared cost is apportioned to members, directly where segments allow
     and otherwise as a labelled `allocated` share.
   - The planner prices grouped against independent execution only from
     measured samples. Unknown values stay unknown.

# Why the default moves on this evidence

`EV-ROS-2026-A064` advised against a default after one run. The owner's
decision rests on these points:

- The replication pointed the same way (`EV-ROS-2026-A070`).
- The size of the cost and coherence effect (more than half the cost, one
  model instead of several) is large compared with the risk the evidence
  identified. That risk is weaker individual-criterion fidelity and reuse,
  and decision 3 turns it into enforced gates.
- The scope is narrow: high-affinity, small and repository-local groups.
- The default is reversible by configuration, and its review and rollback
  triggers are fixed in advance (PRX-GRP-138).

It is a bounded bet, not a general claim. Nothing here says grouped
execution is better for loosely related work. That is still to be tested
(`EX-ROS-2026-A022`/`A023`).

# Review and rollback

- **Measurement.** Continued measurement (PRX-GRP-138, 150..158) compares
  grouped and independent executions. It covers cost per member, active time
  per member, first-pass verification, context-pressure fallbacks and
  30-day rework.
- **Review.** It is due after 10 grouped-mode executions or 90 days after
  the default takes effect, whichever comes first (`PRAXIS-PLAN-11`).
- **Rollback.** The default reverts to advisory, by configuration and a
  decision record, if any one of these holds:
  - grouped median cost per member is not lower, with at least 5 comparable
    samples each;
  - grouped first-pass verification failure exceeds the independent rate by
    more than 10 points;
  - more than a third of group executions fall back;
  - a confirmed attribution-laundering incident is caused by grouped mode.

# Consequences

- **Requirements only.** `requirements/PLANNING-WORK-GROUPS.md` gains
  PRX-GRP-100..158 and PRX-GRP-190 and amends 040, 045, 051, 052, 061, 073
  and the Objective in place, keeping the replaced text. Implementation is
  captured as `PRAXIS-GROUP-07` (cross-repository), `PRAXIS-GROUP-08`
  (command surface), `PRAXIS-GROUP-09` (execute-group and the default),
  `PRAXIS-GROUP-10` (gates), `PRAXIS-PLAN-10` (measurement) and
  `PRAXIS-PLAN-11` (review). None of them is started.
- **Exception to the read-only planner rule.** `plan execute-group` is the
  first `plan` verb that mutates. PRX-PLAN-001's shadow-mode rule continues
  to bind every other `plan` verb, and its non-goals (agent dispatch, branch
  creation, provider or model selection) bind `execute-group` too.
- **Behaviour change.** Idempotent repeats change the exit codes of
  `AlreadyMember`, `NotMember` and `DuplicateGroup` from `1` to `0` with
  `changed: false` (PRX-GRP-114). Scripts that relied on the refusal must
  check `changed` instead.
- **Cross-repository reads.** Cross-repository status depends on read
  access to member repositories. Without it, members are `unknown`, which is
  correct but less useful.
- **Shared-cost allocation is a convention.** The `equal-share` default for
  group-shared cost is a convention, not a measurement. It is labelled as
  such and replaceable.
