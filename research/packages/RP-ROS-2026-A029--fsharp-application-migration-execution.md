---
id: RP-ROS-2026-A029
title: ROS F# application migration execution and handoff
research_area: repository-operating-system
discipline:
  - software-architecture
  - software-engineering
  - developer-tooling
  - empirical-software-engineering
author_agent: openai-codex
version: 0.1.0
confidence: low
completion: draft
status: draft
priority: high
created: 2026-09-07
updated: 2026-09-07
related_projects:
  - repository-operating-system
related_documents:
  - RP-ROS-2026-A017
  - EV-ROS-2026-A018
  - EX-ROS-2026-A020
  - JR-ROS-2026-A019
  - DF-ROS-2026-A027
supersedes: []
superseded_by: []
tags: [fsharp, migration, sde, application, execution]
keywords: [typed-domain, vertical-slice, differential-testing, telemetry]
---

# Research State Snapshot

- **Theory version:** no ROS theory record is changed by this execution.
- **Knowledge-base version:** baseline
  `6a188073474f5088decf9617f4539625e4bdb451`; SDE release `1.1.1`.
- **Highest-confidence areas:** current executable inventory and sources of truth.
- **Lowest-confidence areas:** consumer F# distribution and longitudinal SDE
  outcome effects.
- **Largest remaining unknown:** whether typed stateful slices improve defects
  and maintenance after persistence/Git migration.
- **Active research streams:** EX-ROS-2026-A020.
- **Recently invalidated ideas:** none yet; pre-treatment architecture was
  narrowed.
- **Priority changes:** deterministic artifact management precedes stateful
  work/telemetry migration.

# Executive Summary

Draft until T6. Baseline, archaeology, semantic decomposition, preregistration,
and target architecture are complete; implementation and final evidence follow.

# Original Objective

Migrate ROS toward a coherent F# application governed by the installed SDE
method, preserving production operation and moving semantics by characterized
vertical slice rather than translating scripts.

# Scope

Complete inventory and architecture, then implement the highest-value safe
shadow slice that can satisfy comparative evidence within this mission. No
production authority switch or wholesale script removal is authorized without
the required evidence.

# Repository Context

See `EV-ROS-2026-A018` and `DF-ROS-2026-A027`.

# Current Understanding

See `docs/migrations/fsharp/ARCHITECTURE.md`.

# Key Discoveries

Pending final synthesis from A018, A028, and the journal.

# Evidence Registry

- `EV-ROS-2026-A015`: prior operational baseline.
- `EV-ROS-2026-A018`: immutable implementation-mission inventory.
- `EV-ROS-2026-A028`: pending implementation and verification evidence.

# Hypothesis Registry

`HY-ROS-2026-A021` through `HY-ROS-2026-A026`; conclusions pending.

# Failed Assumptions

Pending final synthesis.

# Open Questions

Consumer distribution, persistence/recovery, unified Git outcome semantics,
stateful authority switching, hub trust hardening, and absent Time Entry rules.

# Recommended Next Research

Pending final verification.

# Research Backlog

See `docs/migrations/fsharp/ROADMAP.md`.

# Suggested Specialized Research Agents

Distribution/packaging, filesystem crash consistency, Git porcelain semantics,
and independent adversarial architecture/compatibility review.

# Parallel Research Opportunities

Consumer platform distribution and hub security can proceed independently of
the next core persistence design if they do not change production authority.

# Risks

See A018, A027, and the final evidence record.

# Cross-Discipline Opportunities

Empirical software engineering can use the prospective checkpoint/defect/
verification data, subject to the single-slice limitations.

# Knowledge Relationships

This execution applies rather than modifies ROS governance and tests the draft
SDE construction/navigation/verification method under accepted SDE architecture
doctrines.

# Theory Impact Assessment

## Affected theory records

None.

## Affected engineering principles

Prospective findings may inform structural locality, boundary preservation, and
heterogeneous verification but one slice cannot establish universal claims.

## New principle candidates

Pending evidence.

## Deprecated principles

None.

## Confidence changes

Pending evidence.

## Predictions created

See hypotheses A021–A026.

## Predictions invalidated

Pending evidence.

## Required theory-registry updates

None at this stage.

# Research Quality Metrics

- **Primary sources:** repository source, contracts, state, Git, commands, and
  test output.
- **Independent sources:** Python implementation; independent review pending.
- **Counterexamples reviewed:** invalid fixtures and architecture rejection path
  pending execution.
- **Competing viewpoints reviewed:** Node retention, mechanical/full rewrite,
  database/event store, workflow/UI rewrite, and schema-only validation.
- **Hypotheses tested:** pending.
- **Failed hypotheses:** pending.
- **Research completeness:** draft.
- **Confidence gain:** pending.
- **Open questions reduced:** inventory and first-slice boundary; distribution
  remains unresolved.

# Research Debt

- **Missing evidence:** T3–T6 implementation/verification and consumer platforms.
- **Missing experiments:** stateful lifecycle, persistence recovery, distribution.
- **Missing disciplines:** security review of hub boundary.
- **Weak areas:** hosted workflow and external consumer observations.
- **Replication needed:** independent environment/provider.
- **Tool limitations:** runtime token/cost data unavailable or unknown; parallel
  reviewers exhausted separate execution quota.
- **Assumptions awaiting evidence:** hypotheses A021–A026.

# Repository Updates

Pending final inventory.

# Website Updates

Not applicable unless migration changes the web interface; no rewrite planned.

# AI Consumption Notes

Start at `SDE-MAP.md`, then this package, A018, A027, and the artifact feature
manifest. Do not infer that planned F# modules are production authority.

# Handoff Instructions

Pending final clean-state commands and next work.

# Research Journal

`JR-ROS-2026-A019` is the chronological and defect record.

# Appendix

Checkpoint and metric projection: `docs/migrations/fsharp/TELEMETRY.md`.

# Completion Checklist

- [x] Required metadata is present for a draft.
- [x] Important preregistration claims reference evidence and hypothesis IDs.
- [x] Competing alternatives were considered.
- [ ] Failed assumptions and treatment results are finalized.
- [ ] Theory impacts are assessed from actual evidence.
- [x] Research debt is explicit.
- [ ] Registries are updated at T6.
- [ ] The next agent can continue from a clean, verified handoff.
