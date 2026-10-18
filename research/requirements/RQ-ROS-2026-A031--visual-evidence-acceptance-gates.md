---
id: RQ-ROS-2026-A031
title: Commit-bound visual decisions and rendered-evidence acceptance gates
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-18
updated: 2026-10-18
research_area: repository-operating-system
priority: critical
related_documents:
  - https://github.com/kemiller2002/praxis/issues/270
tags: [visual-intent, evidence, acceptance, construction]
derived_from: [INPUT-684a43561de78083]
provenance:
  contributions:
    EXE-20261018T124842618Z-ce3f5877:
      operations: [created]
      at: 2026-10-18T12:53:00.522Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Canonical ingestion of claimed requirement source"
---

# Commit-bound visual decisions and rendered-evidence acceptance gates

This draft canonically records Praxis issue #270. Ingestion is separate from
approval and implementation.

## Problem

Work can pass compilation, product-state tests, and foundation-adoption checks
while shipping unsupported CSS tokens or inconsistent colors. A worker's
assertion that visual review occurred is insufficient without independent,
reproducible evidence.

## Requirements

- **PRX-VIS-001.1:** UI-bearing work packages receive an immutable visual decision/requirement manifest from Ordo #92 and preserve source commit, package versions, and approved decision ID.
- **PRX-VIS-001.2:** Before accepting, integrating, or completing a package, require independent Dokimos token-contract evidence (Dokimos #41) and a Visual Engineering rendered-evidence manifest (VE #49). Validate artifact identity, expected states/viewports/themes, and actual results, not receipt presence alone.
- **PRX-VIS-001.3:** Reject absent, stale, or mismatched receipts; invalid tokens; missing required screenshots; unapproved palette changes; and critical contrast/state violations. Support explicit NOT_APPLICABLE, UNKNOWN, and governed exceptions. UNKNOWN is never auto-PASS.
- **PRX-VIS-001.4:** Permit normal intermediate compilation/testing for diagnostics. Gate acceptance and integration rather than every edit or compile, and batch visual checks at coherent package boundaries.
- **PRX-VIS-001.5:** Keep verification local-first and OS/worker-neutral with replaceable browser/adapters. Do not mandate Kubernetes or external orchestration. Reuse signed-receipt and checklist infrastructure where appropriate rather than duplicating policy.
- **PRX-VIS-001.6:** Emit a versioned evidence record referencing original requirement IDs, approved visual decisions, inspected commit, and failures/unknowns so cohort handoffs do not drift through natural-language summaries.

## Acceptance criteria

- A validly signed but stale or wrong-commit visual receipt is refused.
- An application compiling with Summa's unknown color references is refused for completion; correcting tokens enables acceptance when all other checks pass.
- High contrast alone never approves an inappropriate product palette.
- Runs complete locally on supported platforms using available browser facilities; untested platforms are UNKNOWN.
- Execution-time comparison demonstrates acceptable overhead and bounded, end-biased CI batching.

## Related authority

Related work is Praxis #234 and #96, Visual Engineering #49, and Forma #159
and #160. Implementation remains governed by Praxis
[#270](https://github.com/kemiller2002/praxis/issues/270).
