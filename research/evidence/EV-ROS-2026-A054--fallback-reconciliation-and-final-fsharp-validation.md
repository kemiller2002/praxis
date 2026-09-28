---
id: EV-ROS-2026-A054
title: Runtime-free step reconciliation and final F# validation
status: accepted
version: 1.0.0
owners: [repository-governance]
created: 2026-09-28
updated: 2026-09-28
research_area: repository-operating-system
evidence_type: primary
supports:
  - DF-ROS-2026-A037
  - DF-ROS-2026-A042
  - RQ-ROS-2026-A019
  - RQ-ROS-2026-A022
related_documents:
  - docs/fallback-reconciliation.md
  - protocol/praxis-envelope-v1.schema.json
  - research/evidence/EV-ROS-2026-A055--step-level-execution-telemetry-validation.md
tags: [telemetry, steps, fallback, reconciliation, fsharp, validation]
confidence: high
provenance:
  contributions:
    EXE-20260927T145842499Z-f1b40c22:
      operations: [created, modified]
      at: 2026-09-28T07:03:10Z
      last: 2026-09-28T07:03:52Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Final F# validation and adversarial evidence for journaled fallback step reconciliation"
      evidence: [DF-ROS-2026-A042]
---

# Evidence summary

The runtime-free envelope is now a second entry path into the canonical F#
work and execution model, not a parallel store. Ordered work requests are
planned in an isolated root with native transition and evidence rules. An
optional step-aware execution preserves stable identities, lifecycle,
nesting, measurement availability, registry aggregation, evidence references,
sanitized raw provider data, actor identity, and instance identity.

Accepted state is applied through an exact-path journal, committed with a
transaction trailer, and bound to a deterministic Git checkpoint tag. Retry
recovers the prepared content rather than dispatching transitions again.
Rejected input preserves its original bytes. Input removal occurs only after
the checkpoint exists, and tracked input uses a later cleanup commit.

# Adversarial review results

Three review passes fixed material defects before acceptance:

- normalized metrics now require a registry definition and preserve its
  aggregation, so cumulative counters cannot become additive deltas;
- completion fails closed for unresolved supplied steps or a linked active
  native execution, and raw telemetry is rejected before mutation when it
  exceeds receiving-repository policy;
- transaction recovery verifies committed path content, receipt/tag/hash
  binding, trusted journal location, post-checkpoint input deletion, and
  rejects symbolic-link path redirection.

Nested grouping parents cannot carry direct telemetry, which prevents parent
and child double counting. Calculated cost requires versioned pricing, while
estimated measurements require explicit confidence. A malformed event-log
line now becomes a deterministic validation finding rather than an unhandled
operational failure.

# Validation

- `dotnet build Ros.slnx --configuration Release` with the repository's
  required FSharp.Core version completed with 0 warnings and 0 errors.
- The F# test executable reported `571 test(s); 571 passed; 0 failed`.
- `./ros registry check` reported `registries are current`.
- `./ros provenance audit --json` reported 0 errors and 0 warnings.
- `./ros validate --json` returned `valid: true` with no findings.
- The merged event log preserves every event from both merge parents and has
  no duplicate event identifiers.

Legacy Node implementation and differential tests were intentionally not
changed or used for acceptance, following the repository owner's direction
that F# is the sole implementation target. This work did not start the
separate `ros` to `praxis` executable rename.
