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
version: 0.2.0
confidence: medium
completion: complete
status: accepted
priority: high
created: 2026-09-07
updated: 2026-09-08
related_projects:
  - repository-operating-system
related_documents:
  - RP-ROS-2026-A017
  - EV-ROS-2026-A018
  - EV-ROS-2026-A028
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
  `6a188073474f5088decf9617f4539625e4bdb451`; implementation slice commit
  `30fcc6e`; SDE release `1.1.1`.
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

ROS now has a repository-local .NET 10 F# shadow application for the first
safe vertical slice: typed artifact validation and deterministic registry
projection. Node remains production authority. The slice was selected because
its writes are disposable projections and because Node/Python behavior could be
characterized before treatment. It passed byte-level Node/F# comparison,
positive/negative/adversarial tests, current-repository smoke checks, and the
legacy full suite. This supports a per-slice compatibility conclusion, not a
claim that production authority or consumer distribution is ready.

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

- ROS has six evidenced semantic areas—artifacts, work, execution telemetry,
  bootstrap/distribution, project administration, and automation/release—that
  cut across its physical scripts.
- A feature-local F# split can own artifact semantics without copying script
  boundaries or switching the Node launcher.
- Explicit `Failed` and `Indeterminate` effect outcomes made a partial
  multi-file registry write visible in tests; atomic replacement of one file
  does not supply a transaction for the set.
- Baseline navigation lacked a project SDE map/manifests, so discovery metrics
  begin at T2 and cannot support a before/after causal result.

# Evidence Registry

- `EV-ROS-2026-A015`: prior operational baseline.
- `EV-ROS-2026-A018`: immutable implementation-mission inventory.
- `EV-ROS-2026-A028`: implementation and heterogeneous verification results.

# Hypothesis Registry

`HY-ROS-2026-A021` through `HY-ROS-2026-A026`. A021–A023 receive limited
per-slice support; A024 is instrumented only; A025/A026 were not treated.

# Failed Assumptions

- A locally installed .NET SDK does not demonstrate portable consumer
  distribution.
- A one-file atomic writer does not imply atomicity for the complete registry
  projection.
- A successful sandbox test command does not prove HTTP integration when the
  sandbox forbids loopback listeners; the unrestricted rerun was necessary.

# Open Questions

Consumer distribution, persistence/recovery, unified Git outcome semantics,
stateful authority switching, hub trust hardening, and absent Time Entry rules.

# Recommended Next Research

1. Design and characterize MIG-05 transactional filesystem recovery before
   moving a stateful work or telemetry writer.
2. Establish a typed Git boundary that distinguishes unavailable from clean.
3. Migrate work lifecycle as a shadow state/evidence slice with controlled
   repository fixtures and transition comparison.
4. Test .NET distribution on declared macOS/Linux/Windows consumer targets
   before considering a `./ros` authority switch.
5. Obtain a genuinely separate adversarial review and hosted workflow evidence.

# Research Backlog

See `docs/migrations/fsharp/ROADMAP.md`.

# Suggested Specialized Research Agents

Distribution/packaging, filesystem crash consistency, Git porcelain semantics,
and independent adversarial architecture/compatibility review.

# Parallel Research Opportunities

Consumer platform distribution and hub security can proceed independently of
the next core persistence design if they do not change production authority.

# Risks

See A018, A027, and A028. The immediate technical risk is cross-file persistence
and Git outcome ambiguity, not the lack of additional F# conversion volume.

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

For a bounded deterministic projection, a typed shadow plus frozen fixtures
and a legacy differential can establish a useful compatibility gate without an
authority switch. This is a candidate only; it has not been tested for stateful
or externally observable operations.

## Deprecated principles

None.

## Confidence changes

Confidence in the artifact-slice boundary increased from low to medium.
Confidence in consumer distribution and migration-wide defect reduction remains
low/very low because no matching evidence was collected.

## Predictions created

See hypotheses A021–A026.

## Predictions invalidated

None; unavailable or untested predictions remain open rather than treated as
validated.

## Required theory-registry updates

None at this stage.

# Research Quality Metrics

- **Primary sources:** repository source, contracts, state, Git, commands, and
  test output.
- **Independent sources:** Python implementation for overlapping artifact
  behavior; external hosted-action documentation; independent review unavailable.
- **Counterexamples reviewed:** invalid fixture, malformed front matter,
  architecture rejection, unknown command, stale registry, and partial-write
  outcomes.
- **Competing viewpoints reviewed:** Node retention, mechanical/full rewrite,
  database/event store, workflow/UI rewrite, and schema-only validation.
- **Hypotheses tested:** A021–A024, with A024 instrumentation only.
- **Failed hypotheses:** none established; A025/A026 not treated.
- **Research completeness:** complete for the authorized artifact slice.
- **Confidence gain:** bounded artifact compatibility and typed-effect evidence.
- **Open questions reduced:** inventory, first-slice boundary, and F# artifact
  behavior; distribution remains unresolved.

# Research Debt

- **Missing evidence:** hosted workflow, consumer platforms, stateful writers,
  and independent adversarial review.
- **Missing experiments:** stateful lifecycle, persistence recovery, distribution.
- **Missing disciplines:** security review of hub boundary.
- **Weak areas:** hosted workflow and external consumer observations.
- **Replication needed:** independent environment/provider.
- **Tool limitations:** runtime token/cost data unavailable or unknown; parallel
  reviewers exhausted separate execution quota.
- **Assumptions awaiting evidence:** hypotheses A021–A026.

# Repository Updates

See `git show 30fcc6e` for the implementation slice: five F# projects, eight
F# tests, three differential/smoke tests, additive CI provisioning, and
migration documentation. No legacy script was removed or rerouted.

# Website Updates

Not applicable unless migration changes the web interface; no rewrite planned.

# AI Consumption Notes

Start at `SDE-MAP.md`, then this package, A018, A027, and the artifact feature
manifest. Do not infer that planned F# modules are production authority.

# Handoff Instructions

Start from `SDE-MAP.md`, then A018, A028, A027, this package, and the
artifact feature manifest. Run `npm run test:all`, the TypeScript builds,
`./ros registry check`, and `./ros validate`. Do not claim the shadow is
production authority. Begin MIG-05 only after recording a new work item and
characterizing crash/retry/version behavior.

# Research Journal

`JR-ROS-2026-A019` is the chronological and defect record.

# Appendix

Checkpoint and metric projection: `docs/migrations/fsharp/TELEMETRY.md`.

# Completion Checklist

- [x] Required metadata is present for a draft.
- [x] Important preregistration claims reference evidence and hypothesis IDs.
- [x] Competing alternatives were considered.
- [x] Failed assumptions and treatment results are finalized.
- [x] Theory impacts are assessed from actual evidence.
- [x] Research debt is explicit.
- [x] Registries are updated at T6.
- [x] The next agent can continue from a clean, verified handoff.
