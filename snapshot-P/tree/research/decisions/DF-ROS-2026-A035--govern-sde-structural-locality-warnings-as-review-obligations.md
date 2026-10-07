---
id: DF-ROS-2026-A035
title: Govern SDE structural-locality warnings as review obligations, not LOC targets
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-21
updated: 2026-09-21
research_area: repository-operating-system
decision_type: architecture
supports: []
related_documents:
  - docs/upgrades/SDE-1.3.0-STRUCTURAL-REVIEW.md
  - docs/upgrades/SDE-1.3.0-STRUCTURAL-TRIAGE.md
  - .sde/architecture/STRUCTURAL-LOCALITY.md
  - .sde/method/VERIFICATION-METHOD.md
supersedes: []
superseded_by: []
tags: [sde, structural-locality, bounded-reasoning, ros, exceptions]
confidence: high
---

# Decision

ROS treats `SDE-STRUCT-001` as the review signal SDE defines it to be.

A file is not split merely to move below a line-count band. Every current
finding is reviewed for semantic area, semantic authority, responsibility
clusters, navigation cost, and change coupling.

The repository will not:

- raise SDE thresholds to make warnings disappear;
- add ignore patterns for reviewed source files;
- split frozen golden-master data into arbitrary files solely to lower LOC;
- claim that a clean LOC report proves semantic locality.

Two current source units receive explicit bounded structural exceptions:

1. `src/Ros.Infrastructure/Work/FileTelemetryFinalizationRepository.fs`
2. `src/Ros.Cli/Program.fs`

The exceptions are not permission for unlimited growth.

## FileTelemetryFinalizationRepository

This file is the F# telemetry mutation boundary migrated under parity with the
former Node telemetry implementation. It contains multiple responsibility
clusters inside one telemetry execution-lifecycle semantic area: finalization,
ingestion/adaptation, normalized metric/capability mutation, classification,
and execution lifecycle coordination.

Splitting it now would create a high-risk mechanical migration across
JSON-mutation and differential-parity behavior without evidence that the split
would reduce defects or context for the work currently being performed.

The exception therefore has a trigger:

> The next semantic change that adds a new telemetry mutation family or
> materially changes one of these clusters must first evaluate extracting that
> cluster behind an explicit typed boundary. New unrelated behavior must not be
> added to this file.

The file remains a structural warning. The exception explains it; it does not
silence it.

## Program.fs

`Ros.Cli.Program` is the F# CLI composition root. Its size is high because it
parses and renders many command families, but domain legality remains in
Domain/Application modules and persistence remains in Infrastructure modules.

The composition root may route, parse CLI syntax, translate errors, and render
results. It must not become a second authority for domain rules.

The exception has a trigger:

> A new command family with more than a small routing surface, or a semantic
> change that would add domain decision logic to Program.fs, must extract a
> command module instead of growing the composition root.

## Other findings

The remaining findings are reviewed individually in
`docs/upgrades/SDE-1.3.0-STRUCTURAL-TRIAGE.md`. They are either cohesive
single-authority modules, frozen compatibility surfaces, data-heavy bootstrap
material, or scenario/golden-master-heavy tests. None currently provides
evidence of duplicated semantic authority or unrelated production rules that
would justify churn-only decomposition.

# Why

SDE doctrine explicitly separates semantic area, semantic authority,
responsibility cluster, and physical file. Its LOC bands are review signals,
not universal limits.

A mechanical split can make the metric greener while making navigation,
authority, or verification worse. The useful outcome is a bounded context
surface with explicit authority and review triggers.

# Consequences

- Strict SDE verification is expected to continue reporting reviewed LOC
  warnings until source structure changes naturally.
- Every current warning has a recorded disposition.
- The two >2,000-line source files have explicit bounded exceptions and
  extraction triggers.
- Future reviewers can distinguish an acknowledged warning from an unreviewed
  one without weakening SDE configuration.
- SDE-STRUCT-002/003/004 are not claimed to have been mechanically checked;
  this decision governs the currently automated SDE-STRUCT-001 findings only.
