# SDE 1.3.0 structural review obligations

Status: initial upgrade snapshot; triaged by WI-0062 and DF-ROS-2026-A035.

Generated during WI-0061 while upgrading ROS from SDE 1.1.1 to 1.3.0.

The current ROS 3.1 tree is captured separately in
`SDE-1.3.0-STRICT-CURRENT.json`; it contains 17 findings because
`src/Ros.Contracts/Ordo/ObservationJson.fs` entered the review band after
this initial snapshot. See `SDE-1.3.0-STRUCTURAL-TRIAGE.md` for the disposition
of every current finding.

The SDE installation itself verifies cleanly. Strict verification reports the review signals below.
They are recorded as follow-up work rather than hidden by changing SDE thresholds.

Finding count: **16**

- SDE-STRUCT-001 [justification-required] `src/Ros.Infrastructure/Work/FileTelemetryFinalizationRepository.fs`: 2849 physical lines
- SDE-STRUCT-001 [justification-required] `src/Ros.Cli/Program.fs`: 2539 physical lines
- SDE-STRUCT-001 [strong-review] `tests/work-start-fsharp-differential.test.mjs`: 1637 physical lines
- SDE-STRUCT-001 [strong-review] `tools/ros_cli.mjs`: 1563 physical lines
- SDE-STRUCT-001 [strong-review] `tools/ros_telemetry.mjs`: 1516 physical lines
- SDE-STRUCT-001 [strong-review] `tests/telemetry-show-fsharp-differential.test.mjs`: 1443 physical lines
- SDE-STRUCT-001 [strong-review] `setup_ros_layout.py`: 1095 physical lines
- SDE-STRUCT-001 [review] `src/Ros.Domain/Telemetry/TelemetryValidation.fs`: 722 physical lines
- SDE-STRUCT-001 [review] `src/Ros.Cli/Lifecycle.fs`: 619 physical lines
- SDE-STRUCT-001 [review] `tests/npm-bootstrap.test.mjs`: 610 physical lines
- SDE-STRUCT-001 [review] `tests/lifecycle-package.test.mjs`: 603 physical lines
- SDE-STRUCT-001 [review] `tests/work-fsharp-differential.test.mjs`: 565 physical lines
- SDE-STRUCT-001 [review] `web/app.ts`: 547 physical lines
- SDE-STRUCT-001 [review] `src/Ros.Infrastructure/Work/FileBacklogQueueRepository.fs`: 529 physical lines
- SDE-STRUCT-001 [review] `src/Ros.Infrastructure/Work/FileTelemetryExecutionRepository.fs`: 502 physical lines
- SDE-STRUCT-001 [review] `tests/Ros.Tests/TelemetryValidationTests.fs`: 502 physical lines

Each finding must be triaged under the installed SDE structural-locality rules. A finding may result in refactoring or an explicit justified exception; line count alone is not proof of semantic nonconformance.
