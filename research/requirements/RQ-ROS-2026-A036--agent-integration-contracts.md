---
id: RQ-ROS-2026-A036
title: Integration contracts for parallel agent workstreams
status: proposed
version: 1.0.0
owners: [repository-governance]
created: 2026-10-28
updated: 2026-10-28
research_area: repository-operating-system
priority: high
related_documents:
  - docs/work-protocol.md
  - docs/cli.md
  - schemas/praxis-integration-contract.schema.json
  - EX-ROS-2026-A025
tags: [integration, workstreams, ownership, merge-readiness, planning]
derived_from: [INPUT-28bdc22a92aeab92]
provenance:
  contributions:
    EXE-20261028T005916741Z-aae5a0a4:
      operations: [created]
      at: 2026-10-28T01:28:45.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Canonical ingestion of EII-B (agent integration contracts); R1-R5 implemented under EII-B-001"
---

# Integration contracts for parallel agent workstreams

This record is the canonical ingestion of intake EII-B, Workstream B
("Agent integration contracts") of the Echelon Engineering Improvement
Initiative. The source is `.praxis/processed/INPUT-28bdc22a92aeab92/`.

The evidence comes from Rehearsal 5 (`kemiller2002/indy-init-rehearsal-005`,
main up to `e086147`):

- 55 merges, 7 of which conflicted;
- 19 "Praxis: reconcile" commits;
- file ownership existed only as a commit-message convention and as prose;
- nothing checked ownership before merging.

## Requirements

| ID | Requirement | State (work item EII-B-001) |
|---|---|---|
| R1 | A minimal, versioned integration contract, read from the repository's Praxis configuration. It declares each stream's identity, bound to a work item; owned paths, or a reference to `planner.areas`; shared interfaces whose change needs an approval recorded in the contract or a Decision Record; dependencies; required tests; merge-readiness conditions; and expected artifacts. | Implemented. `praxis.integration-contract/1` is the `integration` member of `ros.json`, or `--contract FILE`. Schema: `schemas/praxis-integration-contract.schema.json`. `ownsPlannerArea` reuses `planner.areas`. |
| R2a | The check flags changed paths outside a stream's ownership, unless a recorded approval for a shared interface covers them. | Implemented: `outside-ownership`, `interface-unapproved` and `approval-decision-not-found`. |
| R2b | The check detects overlap between sibling stream refs before merge, extending the overlap computation of `sync check`. | Implemented. `UpstreamSync.overlap` is now shared by `sync check` and `streams check`. Overlap is computed with the target and with every two unintegrated siblings. |
| R2c | The check reports a dependency-ordered integration sequence; cycles are errors. | Implemented. `Grouping.topologicalOrder` and `Grouping.stronglyConnected` are now shared with work-group sequencing. Contract dependencies are combined with hard planner dependencies. |
| R2d | Merge readiness counts required-test evidence only through existing verified-completion mechanisms. Otherwise it is reported as `unverified`, never assumed. | Implemented. The evidence is a `work.completion.verified` event at the stream tip, valid only if the tip differs from the verified commit under `.ros/` alone. The event binds the validations Dokimos verified, but does not name individual tests, so a required test is reported `covered-by-verified-completion` rather than matched by name. |
| R3 | Unit tests cover each rule, plus negative controls. | Implemented in `tests/Praxis.Tests/IntegrationStreamsTests.fs` (19 cases). |
| R4 | A retrospective, non-causal replay on Rehearsal 5. | Done: `EX-ROS-2026-A025`. |
| R5 | The integration policy, and how agents consume the contract, are documented. | Done: `docs/work-protocol.md`, "Integrating parallel workstreams". |

## Not claimed

- Enforcement at merge time. The check is advisory: nothing refuses a
  merge or a push.
- Hunk-level conflict prediction. Overlap is per path.
- Automatic attribution of stream commits. The replay measured how often
  the contract alone would determine the work item, but Praxis does not yet
  record such attributions.
- Authentication of approvals. A contract approval is as trustworthy as
  write access to `ros.json`.
