---
id: EV-ROS-2026-A053
title: First-class step-level execution telemetry implementation and validation
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-27
updated: 2026-09-27
research_area: repository-operating-system
evidence_type: primary
supports:
  - DF-ROS-2026-A037
  - RQ-ROS-2026-A013
  - RQ-ROS-2026-A014
  - RQ-ROS-2026-A015
  - RQ-ROS-2026-A016
  - RQ-ROS-2026-A017
  - RQ-ROS-2026-A018
  - RQ-ROS-2026-A019
  - RQ-ROS-2026-A020
related_documents:
  - docs/development-telemetry.md
  - docs/cli.md
  - docs/work-protocol.md
  - schemas/execution-telemetry.schema.json
  - protocol/praxis-envelope-v1.schema.json
tags: [telemetry, execution, steps, validation, provenance]
confidence: high
provenance:
  contributions:
    EXE-20260927T013711437Z-b3dfcbfc:
      operations: [created]
      at: 2026-09-27T07:23:55Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Implementation and validation evidence for first-class step telemetry"
---

# Evidence summary

Praxis now records an optional ordered step ledger inside an execution. The
F# CLI can plan, begin, resume, complete, block, abandon, annotate, list, and
show steps while enforcing execution/session ownership, explicit lifecycle
transitions, chronological timestamps, and a single active ancestor path.
Historical executions with no step ledger remain valid.

Step measurements remain canonical execution metrics referenced by stable
identifiers, so execution and work-item aggregation does not duplicate the
same observation. Nested steps use leaf-only direct telemetry: a parent with
children is a grouping step and cannot also carry measurements or
checkpoints. Cumulative checkpoint deltas require compatible begin/end source
observations and preserve their identifiers and derivation; insufficient,
decreasing, or mismatched observations do not produce exact-looking deltas.

Token and cost records preserve value separately from availability and
quality. Zero is a value, not a synonym for unavailable. Calculated cost
requires versioned pricing provenance, while estimated cost requires an
explicit confidence. Provider-specific observations remain references to
sanitized raw telemetry rather than being promoted into invented normalized
measurements.

# Method

The implementation was exercised at four layers:

- 502 F# domain, infrastructure, serialization, CLI, provenance, and
  reconciliation tests passed, including new step lifecycle, identity,
  timestamps, token availability, zero, checkpoint delta, cost provenance,
  raw telemetry, evidence, nesting, aggregation, compatibility, validation,
  and deterministic JSON cases.
- 204 F#/Node differential tests passed after updating the frozen execution
  capability fixtures for the seven additive metric-registry entries.
- 94 Node packaging, bootstrap, server, hub, and compatibility tests passed.
- 7 Python schema and registry tests passed.

The combined command was
`TMPDIR=/private/tmp FSharpCoreImplicitPackageVersion=10.1.400 npm run test:all`.
The explicit FSharp.Core version is the repository's current build constraint.
An earlier unrestricted `npm test` attempt failed because the execution
sandbox denied loopback listeners and the existing npm cache. Once run with
the required permissions, one macOS fixture exposed `/var` versus
`/private/var` spelling; the canonical `TMPDIR=/private/tmp` rerun passed all
tests without a production-code workaround.

# Dogfood evidence

The capability was not used to invent history. After it became safe to use,
execution `EXE-20260927T013711437Z-b3dfcbfc` recorded the first genuine Praxis
steps for documentation/adversarial review and final verification. Their
identity, ordering, lifecycle, availability, and evidence references live in
that execution's canonical telemetry record.

# Limits and follow-up

Provider adapters were not changed to fabricate measurements that the current
runtime did not expose. Exact token and cost attribution therefore remains
unavailable for this execution. The additive fallback envelope and
reconciliation validator preserve step boundaries, telemetry, raw references,
evidence, actor identity, and instance identity, but the pre-existing
fallback request dispatcher still fails closed with
`request-dispatch-not-configured`; a native canonical-ingestion dispatcher is
the immediately following reconciliation workload, not a second format hidden
inside this feature.

No executable rename was started. The resulting CLI and storage model are
ready to measure the separate `ros` to `praxis` executable migration.
