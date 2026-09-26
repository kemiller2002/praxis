---
id: RQ-ROS-2026-A015
title: Provenance crosses system boundaries as a versioned interchange block with deterministic receiving rules
status: implemented
version: 1.2.0
owners:
  - repository-governance
created: 2026-09-26
updated: 2026-09-26
research_area: repository-operating-system
priority: critical
related_documents:
  - DF-ROS-2026-A037
  - DF-ROS-2026-A036
  - docs/agent-provenance.md
  - docs/echelon-provenance-architecture.md
tags: [provenance, echelon, interchange]
derived_from: [RQ-ROS-2026-A009]
provenance:
  contributions:
    EXE-20260926T075049280Z-9919ff63:
      operations: [created, modified]
      at: 2026-09-26T08:05:34.115Z
      last: 2026-09-26T08:52:34.874Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-system Echelon provenance upgrade (work item FEAT-ECHELON-PROVENANCE)"
    EXE-20260926T094012488Z-15a65e61:
      operations: [modified]
      at: 2026-09-26T09:40:38.540Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Contract revision 1.2 after the second adversarial review (FEAT-ECHELON-PROVENANCE-R12)"
---

# Requirement

Praxis MUST define the portable interchange block `praxis.provenance/1` (`schemas/provenance-interchange.schema.json`): the same contributions and lineage an artifact carries, tagged with a major version. A receiving system MUST classify every received block as exactly one of: `supported` (understood; unknown fields preserved; an unknown operation code that matches the grammar is tolerated and reported), `unsupported` (a different major version; carried verbatim, never interpreted, merged into, or rewritten), or `malformed` (rejected at the boundary; never dropped or repaired silently). An appending system MUST follow the same append-only rules as `ros provenance record`. `./ros provenance export ID|PATH` MUST emit an artifact's provenance in this form.

## Rationale

No Echelon system may silently strip valid provenance, yet each is independently installable and upgraded on its own schedule. Consumers therefore need a version tag, a forward-compatible tolerance rule, and an unambiguous rejection rule, all owned by Praxis rather than re-invented per system.

## Acceptance criteria

- `schemas/provenance-interchange.schema.json` publishes the block.
- `tests/fixtures/provenance-interchange/cases.json` pins the verdict for every case, and both the F# codec and `lib/provenance-interchange.mjs` reach it.
- A block with no `schema` tag and a `contributions` map (the registry projection) is read as major 1.
- `provenance export` output classifies as `supported` and round-trips to the same history.
- Revision 1.1 (after adversarial review):
  - Matching is exact: no trailing newlines.
  - Timestamps are calendar-valid and compared at millisecond precision.
  - JSON `null` is never treated as absent.
  - Every append yields a supported block.
  - A merge keeps the incoming unknown fields and the later `last`.
  - An unknown actor cannot extend a known actor's entry.
  - The v1 envelope key escaping is injective.
  - Newer grammar-valid operations are warned about, not rejected, by Praxis's front-matter reader.
- Revision 1.2 (after the second adversarial review):
  - JSON text that repeats a member name within an object, holds an unpaired UTF-16 surrogate, or is not JSON is malformed, whatever its major version (`text-cases.json`, `classifyText`).
  - `classify` never throws.
  - Blankness is judged over ASCII whitespace only.
  - `addLineage` refuses what `classify` would reject (`lineage-cases.json`).
  - Key segments are escaped per code point, including `.` (`envelope-key-cases.json`).

## Verification

- ProvenanceInterchangeTests: interchange conformance: every shared fixture reaches the same verdict as the reference library
- ProvenanceInterchangeTests: interchange conformance (contract 1.2): raw JSON text reaches the reference verdicts
- ProvenanceInterchangeTests: interchange classify never throws on duplicate members or unpaired surrogates (contract 1.2)
- ProvenanceInterchangeTests: interchange export round-trips: toNode then classify yields the same history and lineage
- tests/provenance-interchange.test.mjs (conformance, appending, preservation, serialization)
