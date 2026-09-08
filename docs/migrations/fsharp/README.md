# ROS F# application migration

This directory is the operational handoff for the staged migration authorized
by `DF-ROS-2026-A027`. The production `./ros` command remains the Node runtime
until a later accepted authority-switch decision. The F# executable is a shadow
surface used to establish typed semantic ownership and comparative evidence.

## Canonical migration records

| Requested artifact | Canonical record |
|---|---|
| SCRIPT-INVENTORY | `EV-ROS-2026-A018` |
| FSharp-MIGRATION-ARCHITECTURE | `ARCHITECTURE.md` and `DF-ROS-2026-A027` |
| FSharp-MIGRATION-ROADMAP | `ROADMAP.md` |
| FSharp-MIGRATION-DECISIONS | `DF-ROS-2026-A027` |
| FSharp-MIGRATION-TELEMETRY | `TELEMETRY.md` and execution `EXE-20260907T203141590Z-54f547f8` |
| FSharp-MIGRATION-JOURNAL | `JR-ROS-2026-A019` |
| FSharp-MIGRATION-TRACEABILITY | `TRACEABILITY.md` |
| FSharp-MIGRATION-STATUS | `STATUS.md` |
| Research execution package | final `RP-ROS-2026-A029` |

## Coexistence commands

The authoritative production gates and additive shadow gates are:

```bash
npm test
npm run build:fsharp
npm run test:fsharp
npm run test:all
npm run build:web
npm run build:hub
./ros registry check
./ros validate
```

The current shadow CLI is:

```bash
dotnet src/Ros.Cli/bin/Release/net10.0/ros-fs.dll --version
dotnet src/Ros.Cli/bin/Release/net10.0/ros-fs.dll artifacts validate --json
dotnet src/Ros.Cli/bin/Release/net10.0/ros-fs.dll registry build --dry-run
dotnet src/Ros.Cli/bin/Release/net10.0/ros-fs.dll registry check
dotnet src/Ros.Cli/bin/Release/net10.0/ros-fs.dll git status --json
dotnet src/Ros.Cli/bin/Release/net10.0/ros-fs.dll work decide --state active --action complete --required implementation --required tests --provided implementation --json
dotnet src/Ros.Cli/bin/Release/net10.0/ros-fs.dll work plan --id TASK-PLAN --type feature --state active --action complete --occurred-at 2026-09-08T18:30:00Z --required implementation --evidence implementation=src/example.fs
```

`npm run build:fsharp` disables persistent build servers and uses one build
worker. This keeps compilation deterministic in constrained agent environments;
the project graph remains small enough that the conservative setting is not a
material local cost.

No document in this directory changes the source-of-truth rules in accepted
ROS decisions. Canonical research records remain Markdown; registry JSON is
generated. Work and telemetry stores remain under `.ros/`.

`test:fsharp` runs typed unit/architecture tests and Node-driven differential
tests. They compare artifact registry bytes/findings and Git status paths,
two-character statuses, rename origins, clean/unavailable state, and all 16
live-work state/action pairs plus block/evidence rejection behavior. The work
plan differential also compares item and event projections for all five legal
transition edges.
These are compatibility gates, not an authority switch.
