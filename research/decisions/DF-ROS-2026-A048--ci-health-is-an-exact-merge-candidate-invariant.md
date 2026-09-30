---
id: DF-ROS-2026-A048
title: CI health is a merge-candidate invariant, not an in-progress branch invariant
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-30
updated: 2026-09-30
research_area: repository-operating-system
decision_type: architecture
supporting_evidence: []
related_documents:
  - requirements/MERGE-READINESS.md
  - docs/work-protocol.md
  - DF-ROS-2026-A042
supersedes: []
superseded_by: []
tags: [ci, merge-readiness, continuity, integration]
confidence: high
---

# Context

The A021 grouped-execution experiment produced a useful intermediate branch
whose product tests passed but whose CI remained red because an
experiment-mandated artifact intentionally conflicted with the repository's
artifact validator. That exposed an ambiguity: Praxis had strong durable
checkpoint semantics, but no first-class distinction between an in-progress
branch and an exact integration candidate.

Requiring every incremental commit to be green discourages coherent
checkpointing, creates unnecessary polling and rebuild work, and conflates
recoverability with integration safety. Conversely, treating a checkpoint,
an explanation, or work-item completion as permission to merge weakens the
integration boundary.

The repository owner explicitly approved the lifecycle-dependent rule in
GH-127 on 2026-09-30.

# Decision

1. **Intermediate red CI is legal.** Active development branches may have
   failing CI. Failures remain visible and must never be relabelled success.
2. **Continuity stays orthogonal.** A durable checkpoint establishes that
   another executor can recover the work. It says nothing about CI or merge
   readiness.
3. **Work completion stays orthogonal.** Completing a work item establishes
   that item's acceptance contract. It does not assert that every other
   change on the branch is integration-ready.
4. **Readiness belongs to an exact commit.** Merge readiness is evaluated for
   one exact candidate SHA. Any later meaningful commit invalidates prior
   readiness evidence.
5. **Required checks fail closed.** Every required semantic check must be
   observed as successful for that candidate. Failed, pending, cancelled,
   skipped, missing, unknown, duplicated or commit-unbound required evidence
   blocks readiness.
6. **Optionality is policy.** A check is optional only because repository
   configuration says so, never because an agent narratively waives it.
7. **Provider-neutral core.** Praxis Domain knows semantic checks and states.
   GitHub Actions, GitLab, Azure DevOps or another host may normalize its own
   statuses into that contract.
8. **One aggregate integration gate.** CI should expose a final aggregate
   gate that runs even after prerequisite failure and represents the exact
   candidate's readiness. Hosting-platform protection should require that
   gate where supported.
9. **Productive iteration remains preferred.** Agents checkpoint coherent
   work and continue useful work while CI batches. They inspect and repair
   required failures before presenting the final candidate for integration.

# Consequences

- A branch can truthfully be both durably recoverable and not merge-ready.
- A red experimental or development branch is not itself a governance
  violation.
- The exact final candidate cannot merge merely because its failures are
  known or explained.
- The merge-readiness surface can be used outside GitHub because the core
  contract contains no GitHub job, run or API type.
- Existing repositories remain compatible until they explicitly enable
  `mergeReadiness`.
- New greenfield repositories inherit an aggregate readiness boundary.
- Repository-host protection remains an external enforcement layer in
  addition to Praxis's read-only decision; an environment without permission
  to configure that protection must report the gap rather than claiming it
  was enforced.

# Provenance completion

This decision was authored during GH-127 after the repository owner's
explicit approval. The supported remote Praxis 3.6.0 path currently rejects
the repository's valid `abandoned` backlog state (GH-128), so the required
Praxis provenance event cannot yet be recorded. This is an in-progress
branch condition, not a waiver. The record must not be presented as
merge-ready until provenance is recorded through Praxis and final validation
is green.
