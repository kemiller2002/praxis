---
id: RQ-ROS-2026-A029
title: Worker checklists require independently verified current evidence before accepted handoff
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-11
updated: 2026-10-11
research_area: repository-operating-system
priority: high
related_documents:
  - RQ-ROS-2026-A028
  - DF-ROS-2026-A067
  - docs/worker-checklists.md
tags: [agents, checklist, quality, evidence, prevention]
provenance:
  contributions:
    EXE-20261011T204811145Z-23f864b2:
      operations: [created]
      at: 2026-10-11T20:57:54.792Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "User-authorized enforced worker checklists; record acceptance and remaining host qualification"
derived_from: [RQ-ROS-2026-A028]
---

# Requirement

Praxis MUST generate mandatory checks for each delegated member from its
requirements and controller policy. An agent's completion claim MUST NOT
satisfy a check. Compilation remains available for diagnostics; accepted
handoff, integration and completion require independently verified current
evidence. User direction: 2026-10-11, implement enforced agent checklists.
Work: GH-234.

# Acceptance criteria

| ID | Required behavior |
| --- | --- |
| PRX-CHK-001 | Generate build, tests, architecture and scope checks for every delegated member plus every requirement-linked acceptance obligation. Refuse missing requirement coverage and incomplete system-validator policy. |
| PRX-CHK-002 | Evidence binds exact assignment, member/execution identity, candidate commit, input closure, decisions, permitted paths, policy revision and validator inputs. Changes invalidate earlier passes. |
| PRX-CHK-003 | Only protected independent observers may supply check results. No worker transport approval field, supplied checklist or evidence filename can establish success. Concrete runners and evidence custody must be qualified separately. |
| PRX-CHK-004 | Missing, ambiguous, unexpected, failed, skipped, unavailable, stale or mismatched checks refuse. A test command that ran no tests cannot satisfy the tests row. |
| PRX-CHK-005 | Enforce before result intake authorizes integration, on the staged candidate before a target update (including prepared recovery), and on the applied candidate before native completion planning. Checklist refusal leaves work/target effects unapplied. |
| PRX-CHK-006 | Reobserve input/policy/authority/prerequisite state after slow checks. Preserve expiry/revocation, native recovery and takeover guards. Cooperative lifecycle locking is required before claiming atomic enforcement against concurrent writers. |
| PRX-CHK-007 | Read-only inspection shows per-member required checks and unresolved status without starting workers, mutating work state or granting approval. |
| PRX-CHK-008 | Provider/OS-neutral typed ports allow portable workers and validators; equivalent OS isolation remains deferred. Existing standalone CLI policy migration is explicit, not silently changed. |

# Verification and remaining scope

The first slice implements PRX-CHK-001, 002, 004, 005 and 007 in the local
library path, with adversarial tests. PRX-CHK-003 still requires concrete
trusted runner/evidence adapters; PRX-CHK-006 still requires qualification of
the complete lifecycle lock protocol. PRX-CHK-008 requires host/OS validation
and a later explicit native CLI adoption decision. Code and tests are not
evidence of a qualified live model-worker pilot.

Exercise member omission/substitution, no tests, forged/stale results, invalid
times, expiry/revocation during checks, policy/input changes during verification,
source-versus-staged-candidate confusion, prepared-journal recovery and
completion after prior integration. Retain exact run results in the checkpoint.
