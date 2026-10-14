---
id: DF-ROS-2026-A055
title: An "origin unrecorded" provenance acknowledgement satisfies the creation rule for records whose creating process left no Praxis execution, without naming or inferring a creator
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-07
updated: 2026-10-07
research_area: repository-operating-system
decision_type: governance
supports: [RQ-ROS-2026-A004, RQ-ROS-2026-A007]
related_documents:
  - RQ-ROS-2026-A004
  - RQ-ROS-2026-A007
  - DF-ROS-2026-A054
  - EX-ROS-2026-A024
  - HY-ROS-2026-A030
  - EV-ROS-2026-A063
  - EV-ROS-2026-A067
tags: [governance, provenance, validation, decision]
confidence: high
derived_from: [RQ-ROS-2026-A007]
provenance:
  contributions:
    EXE-20261007T185521101Z-d60238bc:
      operations: [created]
      at: 2026-10-07T19:01:58.401Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-HYG-03: define the owner-approved origin-unrecorded acknowledgement"
---

# Context

`RQ-ROS-2026-A007` makes `validate` require, under the enforced provenance
policy, that every canonical artifact created on or after the policy date
records its contributions, and warns when no contribution records the
artifact's creation (`created`). `RQ-ROS-2026-A004` keeps contribution history
additive and truthful: Praxis resolves identity only from the current process,
never infers authorship from Git metadata, style or timestamps, and a
`created` contribution cannot follow existing contributions.

Some records were created by a process that left no Praxis execution:

- `EX-ROS-2026-A024` and `HY-ROS-2026-A030` were added by direct pushes to
  `main` (`cd97bdde..94258e4f`, 2026-10-07) with no Praxis execution, so
  `validate` fails on both (no provenance) and `main` has been red since.
- `EV-ROS-2026-A063` was authored outside a Praxis execution (commit
  `3aef2bd`); `EV-ROS-2026-A067` came from a blinded evaluator session that had
  no Praxis execution. `validate` warns that neither records its creation
  (`PRAXIS-HYG-03`).

Nobody but the true creator could record those creations truthfully, and for
the evaluator session nobody can. The requirement offered no way to state the
true condition, "the origin was never recorded", so the only options were a
permanent finding or a fabricated `created` entry.

The repository owner approved this decision on 2026-10-07: asked directly, in
session https://claude.ai/code/session_01CmPhre2cFonbHerxVSsmrL, whether the
merge agent may record DF-ROS-2026-A055 as accepted, build the command,
acknowledge the four records and land the change with integration PR #204,
the owner chose "Approve". The answer was relayed to the executing agent by
the coordinating agent; this record is written by the executing agent.

# Decision

1. A new contribution operation, `origin-unrecorded`, acknowledges that no
   Praxis execution recorded an artifact's creation. It is for records whose
   creating process is unknown or left no Praxis execution.
2. It never names or infers a creator. The contribution's actor and key are
   those of whoever acknowledges the gap (their own execution, or a `CTB-` key
   for a human), with the time of the acknowledgement. A reason is required
   and must say why the creation was never recorded.
3. It stands alone: an `origin-unrecorded` contribution carries no other
   operation. It is refused when the artifact already records `created`, and a
   `created` contribution cannot be added after it; an artifact holding both is
   a validation error.
4. `validate` treats an acknowledged artifact as satisfying the
   missing-creation rules (no provenance; no `created` contribution, including
   for originator-required kinds such as `RQ`). It reports the acknowledgement
   distinctly, never as a creation: a `NOTE origin unrecorded: ...` line naming
   the acknowledger, time and reason, and an informational `provenance.origin`
   finding in `provenance audit`.
5. The command is `praxis provenance acknowledge-unrecorded --path PATH
   --reason TEXT`. The rules live in the domain (`ArtifactProvenance`), so
   `provenance record --operation origin-unrecorded` is governed identically.

# Consequences

- The four records above are acknowledged with their true reasons, `validate`
  passes on `main` again, and `PRAXIS-HYG-03` closes.
- A gap in provenance stays visible: it is reported on every `validate` and in
  `audit`, attributed to the person who acknowledged it, rather than hidden as a
  creation.
- Acknowledging is not a substitute for recording: where the creator can
  record `created` truthfully, they should. The acknowledgement records only
  that the origin is unknown, never who the creator was.
