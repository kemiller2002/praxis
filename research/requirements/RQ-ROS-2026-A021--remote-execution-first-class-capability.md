---
id: RQ-ROS-2026-A021
title: Praxis governance is independent of the execution environment; remote/cloud-agent execution is a first-class capability
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-28
updated: 2026-09-28
research_area: repository-operating-system
priority: high
depends_on: [RQ-ROS-2026-A020]
evidence_ids: [EV-ROS-2026-A053]
related_documents:
  - DF-ROS-2026-A041
  - EV-ROS-2026-A053
  - RQ-ROS-2026-A020
  - DF-ROS-2026-A007
  - DF-ROS-2026-A036
  - DF-ROS-2026-A040
  - https://github.com/kemiller2002/praxis/issues/90
  - https://github.com/kemiller2002/praxis/issues/80
tags: [remote-execution, protocol, provenance, idempotency, security, gh-90]
provenance:
  contributions:
    EXE-20260928T073932249Z-d48161b9:
      operations: [created]
      at: 2026-09-28T07:44:28.080Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Captured from GitHub issue #90 (work item GH-90): remote/cloud-agent execution as a first-class capability"
      evidence: [https://github.com/kemiller2002/praxis/issues/90, EV-ROS-2026-A053]
derived_from: [RQ-ROS-2026-A020]
---

# Requirement

Praxis MUST govern an agent that has GitHub repository/API access but no
.NET, F#, or local Praxis runtime as rigorously as an agent running Praxis
locally. Remote execution MUST reach the **same** Praxis domain rules,
legal transitions, validation, provenance, attribution, and telemetry
semantics as the local CLI through a typed, versioned, transport-independent
request/response protocol; there MUST NOT be a second governance
implementation. GitHub Actions is the first execution adapter and MUST stay
a thin host: it bootstraps a pinned, verified Praxis release, forwards the
request, persists only Praxis-owned state, and returns the structured
result. Praxis remains the authority.

The detailed, normative requirement list is issue #90, `PRX-REMOTE-001`
through `PRX-REMOTE-044`, which this record adopts by reference rather than
copying. Their reconciliation against the implementation, with the gap for
each, is `EV-ROS-2026-A053`; the design and decomposition is
`DF-ROS-2026-A041`.

## Invariants every implementation increment preserves

1. **One authority.** Local and remote requests execute the same command
   implementation; remote validation can never be weaker than local.
2. **Explicit operations, never a shell.** The remote surface is an
   allow-listed, typed operation catalog. Request values are untrusted data
   and never become shell text.
3. **Identity is not inferred from the host.** The requesting actor is
   *asserted* by the request, the runner/executor is *observed* by the
   adapter, and the authenticated transport principal is a third fact.
   None is collapsed into another; unknown stays `unknown`; a runner never
   becomes the author of an agent's work.
4. **Idempotent mutation.** Every mutating request carries a stable request
   ID; a replay with the same semantic payload recovers the recorded
   outcome; the same ID with a different payload fails closed.
5. **Repository-state binding.** A mutating request names the ref and the
   commit SHA it was formed against; Praxis refuses to apply it to any other
   commit.
6. **Honest outcomes.** Praxis-decided failures (invalid, unsupported,
   unauthorized, stale, conflicting, domain-rejected, validation-failed) are
   distinct from transport/executor failures (bootstrap, write, network,
   rate limit, timeout, cancellation), and an unconfirmed effect is
   `unknown`, never success or failure.
7. **Durable record.** Every accepted mutating request leaves a
   repository-persisted journal entry linking request -> execution ->
   events -> resulting state; logs and workflow artifacts are supporting
   evidence only.
8. **Evidence quality survives.** Telemetry keeps its quality/source and
   missing values stay unavailable, never zero.
9. **Compatibility with #80.** Post-hoc reconciliation remains the path for
   already-committed work, preserving the Git author/committer and
   recording the reconciliation actor separately; nothing is touched or
   recommitted to manufacture attribution.

## Acceptance criteria

The capability is complete only when the `PRX-REMOTE-042` end-to-end proof
passes: a caller with no local .NET/Praxis runtime, using GitHub access
only, discovers the remote capability, starts or resumes governed work
under its own execution identity, records step evidence and telemetry,
has a pinned and verified Praxis release validate and persist the state,
retrieves the structured result, and a later agent can inspect and
continue without impersonating the first.

## Verification

Tracked per increment by the `PRAXIS-REMOTE-NN` work items under `GH-90`
(see `DF-ROS-2026-A041`, "Decomposition"). Each increment names its own
tests.
