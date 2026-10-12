---
id: DF-ROS-2026-A071
title: Apply Ordo construction through host-built Git objects bound to a signed cohort grant
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-12
updated: 2026-10-12
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents: [DF-ROS-2026-A070, docs/construction.md, docs/local-runtime.md]
tags: [construction, ordo, authority, git, recovery]
confidence: medium
derived_from: [DF-ROS-2026-A070]
provenance:
  contributions:
    EXE-20261012T163651312Z-c1d5cf88:
      operations: [created]
      at: 2026-10-12T16:43:49.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record the host-controlled construction application decision (GH-242)"
---

# Context

Praxis #242 asks that construction agents be *unable* to make prohibited
repository changes, rather than having them caught afterwards. Ordo (#64, #65,
#69) supplies a typed plan vocabulary and a pure constructor whose output is a
set of artifact descriptions plus `planWrites`, a pure ownership check. A
host still has to decide whether to apply those writes and how.

# Decision

1. **The plan is the only agent input.** Authority comes from the existing
   operator grant in the protected ECIR host policy, re-verified at each
   invocation:
   - group membership;
   - signed scope over source commit, manifest and blueprint digests,
     requirement keys and decisions;
   - expiry and signer trust.

   Decisions passed to Ordo's `prepareCohort` come from the signed approval,
   never from the plan.
2. **Pinned engine in-process.** Ordo.Core and Ordo.Construction are consumed
   as vendored, checksummed release packages. Praxis neither copies the engine
   nor fetches Ordo source at run time.
3. **Git objects, not files.** Existing content is read from the target
   commit's tree. Candidates are built in a private index outside the
   repository, and the target moves only by `update-ref NEW OLD`. No
   filesystem path is followed, so symlink, hard-link, TOCTOU and traversal
   attacks on the writer have nothing to act on. Tree entries that are not
   regular files refuse.
4. **Journal before effect.** The existing local-integration journal store,
   in a separate host root, records each construction before the ref moves.
   The candidate commit date is the journalled preparation time, so a replay
   is byte-identical.
5. **Placement is host-owned.** `constructionMount` (host configuration)
   places Ordo's `generated/<Workflow>/` root. Configuration directories are
   refused.

# Alternatives

- **Writing into a worktree, then diffing.** Rejected. That is post-hoc
  classification, the pattern #242 rules out, and it exposes the writer to
  filesystem races.
- **Running the Ordo native binary.** Rejected because it adds a process
  boundary without adding authority. The local-delegation grant already pins
  the reviewed blobs by digest.
- **A new authorization store.** Rejected. The operator grant already signs
  exactly the scope construction needs.

# Consequences and limits

Construction runs entirely inside the trusted Praxis process, and no agent
process runs, so no OS containment is needed or claimed for this path. Custom
handwritten implementation (#243) is a different matter: it does need
qualified containment, which remains deferred. Ordo's structural checks are
not independent evidence, and generated structure leaves its implement,
resolve and verify obligations open.

# Validation

Real-CLI tests (`ConstructionCliTests`) run the real time-entry timer ECIR,
plan and pinned engine. They cover:
- a deterministic read-only preview;
- apply as one commit changing only planned files, with byte-exact digests;
- idempotent re-apply;
- refusal of edited, misbound and operation-injected plans;
- refusal of a handwritten file in the generated root;
- refusal of a linked ancestor;
- both interruption points resumed;
- a revoked signer;
- disabled configuration.
