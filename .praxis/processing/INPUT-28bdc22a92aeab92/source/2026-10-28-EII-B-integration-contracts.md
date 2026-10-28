# EII-B: Agent integration contracts

Source: Echelon Engineering Improvement Initiative, Workstream B ("Agent
integration contracts"). Intake date 2026-10-28.

## Problem (evidence)

Rehearsal 5 (kemiller2002/indy-init-rehearsal-005), up to main `e086147`:
193 commits, 55 merges, 19 "Praxis: reconcile" commits and 37 post-hoc
`work.attribution.reconciled` events. Seven merges actually conflicted
(merge-tree replay), mostly in `src/app/Adapter.fs`, `src/app/EnginePort.fs`
and `src/app/View.fs`. File ownership existed only as a commit-message
convention ("Shell files: ... (coordinator-approved, stream core)") and as
prose in `docs/architecture/BOUNDARIES.md`. Streams were work items
WI-0003..WI-0007 on `r5/<stream>` branches. Nothing checked ownership before
merging.

In Praxis today, `praxis sync check` reports overlap only between the local
checkout and upstream; `praxis plan analyze` gives advisory collision
signals from `planner.areas` and `planner.conflicts`; work groups carry
`MemberDependency` ordering; `praxis local` enforces `AllowedPaths` for one
packet. Workstream identity and shared interface contracts are absent.

## Requirements

1. A minimal, versioned integration contract (JSON schema under `schemas/`,
   read from the repository's Praxis configuration location) with:
   workstream identity bound to a work item id; owned paths (globs), or a
   reference to `planner.areas` instead of re-declaring them; shared
   interfaces whose change needs an approval recorded in the contract or a
   Decision Record; dependencies on other workstreams; required tests;
   merge-readiness conditions; expected output artifacts.
2. A CLI check in Praxis's command style
   (`praxis streams check [--stream ID] [--against REF] [--json]`) that, from
   git facts only:
   a. flags changed paths outside the stream's ownership unless a recorded
      shared-interface approval covers them;
   b. detects overlap between sibling stream refs before merge, extending
      the sync-check overlap computation rather than duplicating it;
   c. reports a dependency-ordered integration sequence (topological;
      cycles are errors), reusing existing ordering concepts;
   d. evaluates merge readiness: required tests' evidence via existing
      Dokimos receipt / verified-completion mechanisms where available,
      otherwise "unverified" (never assumed).
   Pure functional core over a facts record; thin git I/O shell.
3. Unit tests for each rule plus negative controls: a path outside
   ownership, an unapproved shared-interface edit, overlap between two
   streams, a dependency cycle, missing required-test evidence.
4. A retrospective replay of Rehearsal 5's merge history up to `e086147`
   against an ownership contract reconstructed from `BOUNDARIES.md` and the
   "coordinator-approved" commits as they existed, reported as non-causal
   with its derivation and biases recorded. Measure: conflicting merges
   flagged before merge (of 7), ownership violations, false positives, and
   reconcile commits the contract would have attributed automatically.
   Compare with 55 merges, 7 conflicts, 19 reconcile commits. Publish the
   inputs and outputs.
5. Documentation of the integration policy in the work protocol: define
   interfaces, assign ownership, implement independently, verify locally,
   integrate in dependency order, run targeted integration tests, broaden
   verification at checkpoints; and how agents consume the contract.

## Constraints

Smallest change; reuse planner areas, work groups and sync check rather
than duplicating them. F# only, no dependency upgrades. Never fabricate
replay numbers.
