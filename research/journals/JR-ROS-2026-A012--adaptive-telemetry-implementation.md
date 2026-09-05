---
id: JR-ROS-2026-A012
title: Adaptive execution telemetry archaeology and implementation journal
status: active
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-05
updated: 2026-09-05
supersedes: []
superseded_by: []
related_documents: [DF-ROS-2026-A010, EV-ROS-2026-A011, docs/development-telemetry.md]
tags: [telemetry, implementation, journal]
confidence: high
---

# Objective

Make trustworthy, adaptive, provider-neutral development telemetry part of normal ROS execution while preserving history and the existing work/external-system boundary.

# Chronology

1. Read root governance, the operating manual, engineering standard, constitution, work protocol, repository tree, schemas, registries, bootstrap manifests, CLI, tests, prompts, context, and current dirty state.
2. Registered and began `WI-0003`; baseline was `main` at `aa06e6be9da363db2da33308634bbf5268b759e1`. Existing Project Administration Hub changes were left in place.
3. Ran the baseline suite. The sandboxed run failed 14 localhost server cases with `listen EPERM`; the same suite with localhost permission passed 60 Node and 7 Python tests.
4. Surveyed official Codex, Claude Code, Gemini CLI, and Copilot runtime surfaces. Confirming evidence favored an adapter edge plus normalized/raw layers. Falsifying checks rejected a universal token shape, automatic hook installation, dirty-tree attribution, and cumulative-counter summation.
5. Implemented the execution schema, normalized registry, segmented records, runtime discovery, adapters, privacy filter, classification/scope/quality fields, Git/clock collection, aggregation, validation, CLI commands, and automatic lifecycle integration.
6. Started this mission's own telemetry execution after the mechanism became runnable. Earlier elapsed time and edits are consequently not represented as mechanically observed execution metrics; the dirty baseline makes execution-attributed Git deltas unavailable by design.
7. Added focused tests and provider-neutral documentation. Initial focused telemetry testing found and corrected capability-source initialization, portable execution-ID formatting, first-path whitespace trimming, and cumulative-session test timestamp ambiguity.

# Live findings and debt

- Unknown raw fields can survive immediately, but promotion to normalized semantics still requires human/provider documentation review.
- Runtime hooks/OTel provide substantially better tool and timing telemetry than agent self-report, but enabling them is provider/project policy and cannot safely be assumed by ROS.
- Test/build outcome parsing remains an adapter/tool integration responsibility; ROS supplies normalized fields and explicit CLI recording rather than heuristically scraping arbitrary command output.
- Current local records are not signed, remotely published, centrally retained, or reconciled.
- An adversarial review and final broad validation remain required before this journal can close.
