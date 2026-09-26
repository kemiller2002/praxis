---
id: RQ-ROS-2026-A018
title: Every Echelon system that carries provenance proves it against the shared contract, independently
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-26
updated: 2026-09-26
research_area: repository-operating-system
priority: high
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
      operations: [created]
      at: 2026-09-26T08:05:35.727Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-system Echelon provenance upgrade (work item FEAT-ECHELON-PROVENANCE)"
---

# Requirement

An Echelon system that produces, consumes, transports, or stores Praxis provenance MUST test its implementation against the shared conformance fixtures (`tests/fixtures/provenance-interchange/cases.json`) and, where it takes part in the end-to-end flow, the replayable scenario (`echelon-chain.json`). It SHOULD vendor the fixtures unchanged, recording the Praxis source commit and SHA-256, so it needs no runtime or build dependency on Praxis. A system MUST keep working, and MUST keep preserving provenance it already holds, when Praxis or any other Echelon system is absent.

## Rationale

Shared fixtures are what keep many independent implementations from drifting into a fork, and vendoring them keeps the systems loosely coupled.

## Acceptance criteria

- Praxis tests the fixtures from both its F# and JavaScript implementations.
- The end-to-end scenario reconstructs requirement -> change -> measurement -> finding -> follow-up -> resolution -> validation without attributing the chain to a single actor.

## Verification

- tests/provenance-interchange.test.mjs: end-to-end tests
- ProvenanceInterchangeTests: end-to-end chain: replayed with the Praxis domain, every record keeps its own originator and classifies as supported
