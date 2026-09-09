# Feature Manifest — Work lifecycle

## Purpose

Route changes to the repository-local backlog, live work lifecycle, evidence
obligations, attachments, events, and the local HTTP presentation adapter.

## Ownership

- State, including presentation state: `.ros/work/queue.json` owns captured
  backlog state; `.ros/context/current.json` owns live work state after start;
  `.ros/events/events.jsonl` records semantic events; decisions
  `DF-ROS-2026-A006`, `DF-ROS-2026-A007`, and `DF-ROS-2026-A008`.
- Transitions / commands / messages: `tools/ros_cli.mjs` symbols
  `captureWork`, `backlogTransition`, `startWork`, `transition`, `blockWork`,
  `updateWork`, and `attachFile`.
- Invariants and guards: `BACKLOG_TRANSITIONS`, `TRANSITIONS`,
  `SEMANTIC_STATES`, `WORK_ID_RE`, completion-evidence checks, and
  `effectiveStatus` in `tools/ros_cli.mjs`; typed transition and orchestration
  planning in `Ros.Domain.Work`.
- Capabilities / authority: `contextView` returns allowed actions and required
  evidence; file-adapter scopes are checked by `validateAdapterRequest`.
- Important effects and effect contracts: atomic JSON/text and a local
  work-protocol lock through `tools/ros_persistence.mjs`; separate versioned,
  hash-preconditioned event/context and backlog queue/projection recovery
  journals, plus typed Git evidence through the contract-compatible
  `tools/ros_git.mjs` process adapter. The shadow F# application observes
  completion evidence through a typed filesystem port after semantic guards,
  and observes real candidate telemetry executions through
  `Ros.Infrastructure.Work.FileTelemetryStateRepository` when resolving
  execution IDs. It also observes real Git status and configured
  `ros.json` meaningful/ignored path patterns by default when planning a
  context, through `Ros.Domain.Work.PathFilter` and
  `Ros.Infrastructure.Work.FileWorkConfigRepository`.

## Interfaces

- Inbound: `./ros add`, `./ros work ...`, `./ros adapter ...`, and HTTP routes
  in `tools/ros_server.mjs`.
- Outbound: versioned work/context/event/adapter JSON, queue Markdown,
  attachments, CLI JSON/text, and HTTP JSON/bytes.

## Tests and verification

- Local behavior tests: `tests/work-protocol.test.mjs` and
  `tests/ros-server.test.mjs`; the typed Git seam is exercised by
  `tests/Ros.Tests/GitTests.fs` and `tests/git-fsharp-differential.test.mjs`.
- Shadow orchestration tests: `tests/Ros.Tests/WorkPlanTests.fs` and the legal
  item/event projection differential in `tests/work-fsharp-differential.test.mjs`.
- Telemetry execution-ID resolution tests: `tests/Ros.Tests/TelemetryResolutionTests.fs`
  and `tests/work-telemetry-fsharp-differential.test.mjs`.
- Git observed/meaningful-path composition tests: `tests/Ros.Tests/PathFilterTests.fs`,
  the base-comparison cases in `tests/Ros.Tests/GitTests.fs`, and
  `tests/work-git-paths-fsharp-differential.test.mjs`.
- Boundary/contract tests: `schemas/work-protocol.schema.json`,
  `schemas/work-adapter-*.schema.json`, and JSON CLI assertions in tests.
- Integration/live verification: `./ros status`, `./ros work context ID`, and
  `./ros validate`.

## Dependencies

- Allowed direct dependencies: execution telemetry lifecycle, filesystem
  persistence, Git evidence, clock/ID generation, and configured file adapters.
- Required composition context: `ros.json`, repository root, and
  `telemetry/metrics.json` when telemetry is enabled.

## Modification boundaries

- Normal: `tools/ros_cli.mjs`, `tools/ros_server.mjs`, `web/`, work schemas,
  work docs, and their tests.
- Escalation required: state mappings, legal transitions, evidence obligations,
  or authoritative-store changes because installed repositories depend on them.

## Local agent instructions

- `AGENTS.md`, `docs/work-protocol.md`, and `docs/work-backlog-guide.md`.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-09
- Known gaps: backlog and live work intentionally remain separate recovery
  units; telemetry effects occur before event/context journal preparation; the
  F# planner now owns pure whole-context/multi-item and backlog-promotion plans,
  post-plan evidence observation, and telemetry execution-ID result-feedback
  (recover/reject/bulk-link/finalize), but not new-execution creation,
  queue/context persistence effects, or state-changing execution. Evidence containment has no current
  authority; production behavior accepts absolute existing paths. Production
  remains Node-owned pending those slices and the distribution decision.
