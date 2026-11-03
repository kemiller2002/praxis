---
id: RQ-ROS-2026-A037
title: Stable requirement IDs and a deterministic verification manifest
status: implemented
version: 1.0.0
owners: [repository-governance]
created: 2026-11-03
updated: 2026-11-03
research_area: repository-operating-system
priority: critical
related_documents:
  - RQ-ROS-2026-A034
  - RQ-ROS-2026-A035
tags: [requirements, verification, traceability, determinism]
provenance:
  contributions:
    EXE-20261103T141251588Z-d34e0674:
      operations: [created]
      at: 2026-11-03T14:13:03.730Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Operator assignment: requirement-level implementation and verification (stable requirement IDs, deterministic verification manifest)"
---

# Stable requirement IDs and a deterministic verification manifest

Operator assignment "Requirement-Level Implementation and Verification"
(2026-11-03): every required application field, control, action, validation
rule and workflow must be individually identifiable, locatable in the
implementation and verifiable against its requirement. The engineering system
must detect missing functionality even when the application compiles, its own
tests pass and the agent claims completion. Implementation and contract:
[`docs/requirement-verification-manifest.md`](../../docs/requirement-verification-manifest.md).

## Requirements

1. **Catalog.** Praxis MUST accept a requirement catalog
   (`praxis.requirement-catalog/1`). Each requirement has:
   - a stable, authored ID;
   - a type: ui-field, ui-control, action, navigation, validation-rule,
     data-contract, state-transition, workflow-outcome or accessibility;
   - a screen, name, component and required/visible/editable flags;
   - validation rules;
   - verification checks;
   - at least one authoritative source requirement.
2. **Levels.** Every check MUST belong to exactly one verification level, V1
   structural to V5 lifecycle. Each requirement type MUST admit only the
   checks meaningful for it.
3. **Compiler.** `praxis requirements manifest compile` MUST compile a catalog
   into `praxis.verification-manifest/1`:
   - It is built from the catalog alone. Implementation mappings may annotate
     requirements but never remove one or its obligations.
   - It records the source commit, the catalog SHA-256 and its own content
     hash.
   - It is byte-identical for identical inputs: no timestamp, ordinal
     ordering, LF normalisation.
4. **Rejection.** The compiler MUST reject the catalog and write no manifest
   when it finds any of these:
   - duplicate or missing IDs, or IDs outside the catalog pattern;
   - invalid references;
   - missing verification, or unsupported verification types;
   - conflicting requirements;
   - orphaned implementation mappings;
   - reused or unauthorised retirements;
   - requirements with no authoritative source;
   - a non-SHA source commit.
5. **Stability.** Renaming a label MUST NOT change any requirement or
   obligation ID. An ID MUST leave the catalog only through a retirement
   record naming its authority. A previous manifest's ID that is neither kept
   nor retired MUST be rejected.
6. **Staleness.** `praxis requirements manifest check` MUST report a manifest
   as stale when the catalog changed, when recompiling gives different
   content, or when the manifest was edited after compilation.
7. **Applicability.** A requirement MUST be applicable always or only under
   declared, named facts. Nothing else may make it inapplicable.

## Acceptance criteria

The `RequirementManifestTests` cases in `tests/Praxis.Tests` cover each item
above.

## Not claimed

The compiler does not decide whether an application meets its obligations,
and it never ratifies a catalog.
