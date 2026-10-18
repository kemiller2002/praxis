---
id: RQ-ROS-2026-A033
title: Agent governance for Design by Construction
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-18
updated: 2026-10-18
research_area: repository-operating-system
priority: critical
related_documents:
  - RQ-ROS-2026-A031
tags: [design-by-construction, governance, contracts, evidence]
derived_from: [INPUT-75d557dd4372a265]
provenance:
  contributions:
    EXE-20261018T124842618Z-ce3f5877:
      operations: [created]
      at: 2026-10-18T12:53:01.155Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Canonical ingestion of claimed requirement source"
---

# Agent governance for Design by Construction

This draft canonically ingests the Design by Construction source for Praxis.
Ingestion is not implementation or approval.

## Objective and repository scope

Prevent known invalid UI and print designs structurally rather than relying
primarily on post-hoc inspection. Praxis MUST govern contract-conforming UI
generation, discover the design-contract version, ingest repository-local
requirements, require diagnostics and evidence for custom escape hatches, and
preserve work-item lineage.

## Shared requirements

1. Use a versioned machine-readable design contract with explicit compatibility rules.
2. Standard mode constrains semantic layout, alignment, overflow, typography, color, accessibility, motion, and action semantics.
3. Extended mode permits documented semantic overrides; custom mode requires a reason and additional validation evidence.
4. Preserve ordinary HTML/CSS and accessible web semantics; a proprietary designer format MUST NOT be mandatory for consumers.
5. Reject structurally invalid compositions before code generation with actionable diagnostics.
6. Preserve backward compatibility or provide a documented, versioned migration path.
7. Require keyboard- and Playwright-operable controls whose commands have observable meaningful behavior.
8. Treat runtime and visual testing as evidence for properties contracts cannot guarantee.

## First end-to-end slice

Govern a customer form with two fields, email validation, a real save command,
and responsive layout. The contract MUST prevent misaligned standard fields,
unsupported layout overrides, missing action bindings, and invalid parent-child
compositions. Verification MUST cover narrow/mobile and desktop viewports and
reduced motion.

## Expressiveness and flexibility

- Support stack, grid, split, dock, flow, canvas, overlay, page, and repeater models where applicable without forcing irrelevant models on every renderer.
- Apply context-specific invariants for aligned forms, deliberately positioned canvases, and print pagination/safe areas.
- Allow nested composition, container-responsive adaptation, density, themes, and white labeling without mandating a universal arrangement, navigation, or column count.
- Support declarative binding, computed values, validation, conditional visibility, state-dependent commands, asynchronous feedback, appropriate direct manipulation, keyboard navigation, and machine operation.
- Provide documented extension points for specialized content without bypassing semantic or safety contracts.
- Distinguish structural constraints from subjective guidance and require an auditable extension path for legitimate unsupported designs.

## Reference-layout certification

- Track a cross-repository suite of at least 20 references, including dashboard, complex form, mobile wizard, ledger, calendar, kanban, workflow editor, marketing page, article, resume, menu, business card, and journal publication.
- For each reference, preserve capabilities, rendering modes, accessibility/machine-operability evidence, and custom overrides.
- Treat 100% documented representability, 100% rejection of defined invalid fixtures, and fewer than 10% custom exceptions as proposed targets requiring evidence.
- Measure agent effort, escape use, regressions, and visual quality separately. Visual taste remains reviewable guidance.

## Acceptance evidence

- Versioned schema and supported/unsupported examples.
- Deterministic diagnostics for invalid fixtures.
- Correct desktop and mobile rendering for valid fixtures.
- Keyboard, screen-reader, and Playwright evidence.
- Regression/migration evidence with escape defects and agent correction effort.
- Repository-local work and provenance linked to this input.

## Dependencies and execution

Contract owners MUST agree on layout taxonomy and compatibility before
consumer enforcement. Coordinate Forma first, then Forma Studio and Ordo,
Praxis guidance, Visual Engineering evidence, and Folio. Visual Engineering
owns certification methodology. Praxis MUST preserve the contract in generated
work, use narrow validated slices, and never claim completion before evidence
is committed.
