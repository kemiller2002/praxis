---
id: DF-ROS-2026-A061
title: Worker preparation requires typed packet admission before effects
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-11
updated: 2026-10-11
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents:
  - DF-ROS-2026-A060
  - docs/agent-mistake-prevention.md
  - docs/local-journaled-dispatch.md
tags: [agents, local, prevention, admission]
confidence: medium
derived_from: [DF-ROS-2026-A060]
provenance:
  contributions:
    EXE-20261011T072344502Z-ce2be555:
      operations: [created]
      at: 2026-10-11T07:27:22.167Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Record typed admission boundary and prevention roadmap"
---

# Context and evidence

Praxis #228 follows the user's direction to prevent agent mistakes by
construction. The inspected `LocalJournaledDispatch.run` selected a worker
specification from a raw `LocalWorkerPacket` before checking delegated identity,
packet rules, prerequisites and current authority. No worker was launched
without subsequent checks, but preparation itself accepted unchecked input.

# Proposed decision

Require an opaque `CheckedLocalWorkerPacket` for `SelectSpec`. Only the existing
exact validator can construct the private union case; dispatch performs
read-only admission before invoking selection. Preserve independent current
checks at reservation and launch intent. Use canonical immutable transport
inside the wrapper without changing packet JSON or inventing a second validator.

This is isolated application API hardening, not runtime activation or adoption
of a new authority policy. Supplied pure observations are not authenticated by
the wrapper; the qualified host must still supply protected policy/locks and
containment. The value is not serializable authority or a reusable permission.

# Alternatives and consequences

Moving the existing check earlier without changing types fixes order but
leaves raw transport acceptable to every selector. A boolean validation flag
can be forged and does not carry the checked assignment. A full capability-based
editing API is the next larger boundary, not required to fix this one.

The selector API changes for adapters inspecting their input: unwrap with
`LocalAgentHandoff.checkedPacket`. In-memory prototype adapters are affected;
packet/result wire versions are unchanged. Reversal is a local API change.
Preparation now incurs a fresh read of recovery/authority/prerequisites before
the existing reservation and intent checks. An invalid assignment causes no
selector, journal reservation or process effects.

# Validation and follow-up

Release build: zero warnings/errors. 19 journaled-dispatch, 29 handoff, 10 dispatch
journal, 17 output-spool and 20 submission-intake tests pass. Compiler probes
reject both private-constructor use (FS1093) and raw transport passed to the
selector (FS0001). This is targeted validation, not a full-suite claim.

Track remaining semantic readiness, blueprint construction, operation
capabilities and deterministic generation in docs/agent-mistake-prevention.md.
Runtime host qualification and the offline coordinator/two-worker pilot remain
unresolved under #226; execution gates remain closed.
