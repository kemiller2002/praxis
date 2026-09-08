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
```

`npm run build:fsharp` disables persistent build servers and uses one build
worker. This keeps compilation deterministic in constrained agent environments;
the project graph remains small enough that the conservative setting is not a
material local cost.

No document in this directory changes the source-of-truth rules in accepted
ROS decisions. Canonical research records remain Markdown; registry JSON is
generated. Work and telemetry stores remain under `.ros/`.
