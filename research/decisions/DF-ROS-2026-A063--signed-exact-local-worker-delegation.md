---
id: DF-ROS-2026-A063
title: Bind local worker delegation to a signed complete packet and current ECIR policy
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-11
updated: 2026-10-11
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents: [DF-ROS-2026-A060, DF-ROS-2026-A062, docs/local-worker-delegation.md]
tags: [agents, local, delegation, signatures]
confidence: medium
derived_from: [DF-ROS-2026-A060, DF-ROS-2026-A062]
provenance:
  contributions:
    EXE-20261011T115701257Z-3ebd7686:
      operations: [created]
      at: 2026-10-11T12:54:32.424Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Record signed exact worker delegation and current ECIR binding with explicit host qualification limits"
---

# Context

The local dispatch application accepts host-observed `LocalHandoffAuthority`,
but no production binding connects its exact expected packet to an operator
approval. The ECIR receipt covers source, cohort, requirements and decisions;
it does not cover worker identity, execution identities, allowed files, budgets,
prerequisite evidence or independent acceptance assignments.

# Proposed decision

Require a separate versioned ECDSA P-256 signature over the existing canonical
complete-packet digest, with a domain-separated UTF-8 byte-length-framed payload
including envelope schema, key identity and approver. Reuse the current host
public-key allowlist. Require the same approver key as the current ECIR receipt;
cross-key delegation and independent delegate-key administration are deferred.
The packet must match the current policy revision, repository binding, exact
member/source mapping and receipt bytes. Its lifetime must lie within the
verified ECIR approval lifetime. Fresh verification precedes each application
boundary and constructs only ephemeral handoff authority.

No private key is stored, no operator identity is inferred from agent flags,
and no worker command or default authority is enabled. Private-key custody,
authenticated signing requests and operator review of the complete packet remain
host responsibilities. The envelope is untrusted transport, not policy storage.
Existing immutable dispatch reservations prevent automatic reuse or relaunch;
a signature itself is not a one-time-use store.

# Alternatives and consequences

Reusing the ECIR signature would permit unsupported mutation of worker limits.
A repository-authored enabled flag provides no authenticated delegation.
Signing canonical packet JSON would tie authority to transport serialization;
the established complete-packet digest already has versioned length framing and
set normalization. A new signer service would contradict the local-first target
and is unnecessary for this verifier. Separately provisioned delegated signer
keys could be useful later, but need explicit capabilities and revocation policy.

Public-key verification establishes approval by a key the protected host trusts.
It does not establish OS ownership, process identity, worktree containment,
fresh native execution observations, independent validator evidence, or semantic
correctness of operator-selected assignments. The controller must hold protected
locks through boundary checks and launch. No callback or positive snapshot
substitutes for that lock or qualification.

# Validation and follow-up

Exercise packet mutations, ECIR/worker-signature separation, revocation, source
mapping, nested identity and budget binding, contained lifetime, malformed JSON,
wrong keys/curves and fresh host reads. Connect authenticated local signing,
native execution observations, an immutable adapter closure, qualified OS
containment, and actual launch/intake/integration/completion before activation.
