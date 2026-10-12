---
id: DF-ROS-2026-A062
title: Serialize local ECIR authority and native dispatch through host-owned storage
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-11
updated: 2026-10-11
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents: [DF-ROS-2026-A059, DF-ROS-2026-A060, docs/local-ecir-host-authority.md]
tags: [agents, local, authority, persistence, recovery]
confidence: medium
derived_from: [DF-ROS-2026-A059, DF-ROS-2026-A060]
provenance:
  contributions:
    EXE-20261011T115701257Z-3ebd7686:
      operations: [created]
      at: 2026-10-11T12:11:00.507Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Record concrete serialized local authority and native dispatch boundaries with explicit isolation limits"
---

# Context

The merged foundation defined host authority and native staging/recovery ports,
but supplied no concrete local policy store or ordered host/repository dispatch
adapter. User authorization is to implement product engineering toward one
local coordinator delegating bounded work without external coordination services.

# Proposed decision

Add an isolated Unix file adapter outside the repository. Serialize current
policy and its complete audit chain as one strict, bounded versioned document;
publish it through exclusive pending-file creation, durable file flush and
atomic rename while holding an exclusive host lease. Preserve pending failures
for operator reconciliation. Compare revisions and fresh expiry before native
commit. Compose the existing native recovery/staging journals with the lock order
work-protocol, work-groups, host policy. Preserve native lifecycle identities.

The operator provisions policy, authenticated administrative identity and private
signing separately. The adapter stores only public keys. File permissions are
necessary observations, not proof of containment or authenticated provenance.
No default adapter or worker launch command is registered by this change.

# Alternatives and limits

Separate policy/audit files admit partial publication. An agent-writable repository
policy admits self-authorization. An in-memory monitor alone cannot coordinate
independent controller processes. A hosted authority service is unnecessary for
the local target. A generic Windows ACL abstraction without tested access control
would incorrectly claim portability; this adapter explicitly refuses Windows.

Atomic file replacement is a process-interruption boundary, not universal power-loss
durability. Directory fsync and filesystem qualification, ownership and ancestor
stability, sandbox enforcement and a trusted rollback witness remain required
for stronger claims. Policy history never silently prunes itself. Existing
accepted execution and checkpoint gates remain unchanged.

# Validation and follow-up

Track build and adversarial results in issue #231 and the PR. Before runtime
activation, bind exact local packet authority, authenticated operator provisioning,
protected immutable dependency closure, OS containment and independent acceptance.
Then qualify one worker through native integration/completion; efficacy experiments
are a separate comparison after the execution path works.
