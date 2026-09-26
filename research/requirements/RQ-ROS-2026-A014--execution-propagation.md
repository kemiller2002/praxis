---
id: RQ-ROS-2026-A014
title: The acting identity and execution propagate to downstream tools, and foreign runs are namespaced
status: implemented
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-26
updated: 2026-09-26
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A036
  - DF-ROS-2026-A037
  - RQ-ROS-2026-A002
  - RQ-ROS-2026-A006
  - docs/agent-provenance.md
  - docs/echelon-provenance-architecture.md
tags: [provenance, execution, integration, echelon, identity]
provenance:
  contributions:
    EXE-20260926T204846052Z-8cef0d1e:
      operations: [created]
      at: 2026-09-26T20:50:07.011Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Cross-system provenance upgrade (work item FEAT-ECHELON-PROVENANCE)"
derived_from: [RQ-ROS-2026-A009]
---

# Requirement

`./ros provenance identity --env` MUST print, as shell `export` lines, only these keys:

- the whitelisted, non-secret identity keys that Praxis itself reads: `ROS_ACTOR_KIND`, `ROS_ACTOR`, `ROS_TELEMETRY_PROVIDER`, `ROS_TELEMETRY_MODEL`, `ROS_TELEMETRY_RUNTIME`, and the session, conversation, and run IDs;
- `ROS_EXECUTION_ID`, only when exactly one active execution is evidently this process's own run.

It MUST NOT print an `unknown` value as if it were known. It MUST NOT print any other environment value.

A downstream Echelon system that records a contribution:

- MUST key it by `ROS_EXECUTION_ID` when that variable is present.
- Otherwise, MUST key it by its own run, namespaced as `EXE-<system>.<run>` (the agent or automation case) or by a `CTB-...` key (a human or automation acting outside any run).
- MUST NOT key a contribution by an execution it did not run.
- MUST NOT key a contribution by a Praxis execution ID that it minted itself.

A contribution records the actor performing it. The originating actor is never overwritten by the actor that transports or transforms the artifact.

## Rationale

- **Why a propagation mechanism is needed.** Without one, agent identity established in Praxis is lost as soon as the agent invokes another tool. The tool then either guesses the identity or records `unknown`.
- **Why foreign runs are namespaced.** A downstream system that runs without Praxis still needs execution identity. Namespacing its runs means they can never collide with, or impersonate, a Praxis execution.

## Acceptance criteria

- `provenance identity --env` exports `ROS_EXECUTION_ID` for the caller's own run and omits it when zero or several runs match.
- `ProvenanceRecord.foreignExecutionKey` and `isForeignExecution` implement the namespacing.
- Praxis validation keeps reporting an execution with no local record as a warning, which covers foreign and imported keys.

## Verification

- ProvenanceRecordTests: foreign execution keys are namespaced and never mistaken for Praxis executions
- ProvenanceRecordTests: a second run of the same agent is a new entry; the same run merges into its own
- Manual: `./ros provenance identity --env` inside an active execution exports that execution
