---
id: DF-ROS-2026-A054
title: Python automation is prohibited by the F#/.NET-only invariant; frozen research instruments, the runtime-free bootstrap and the pinned remote adapter are approved exceptions
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-06
updated: 2026-10-06
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A024]
related_documents:
  - RQ-ROS-2026-A024
  - DF-ROS-2026-A049
  - DF-ROS-2026-A041
  - DF-ROS-2026-A045
  - EX-ROS-2026-A021
  - EX-ROS-2026-A022
  - EX-ROS-2026-A023
tags: [architecture, fsharp, dotnet, python, invariant, exception, decision]
confidence: high
derived_from: [RQ-ROS-2026-A024]
provenance:
  contributions:
    EXE-20261006T213719240Z-8ddc5e25:
      operations: [created]
      at: 2026-10-06T21:37:44.731Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-MISC-08: prohibit Python automation and record the owner-approved exceptions"
---

# Context

`RQ-ROS-2026-A024` requires repository-owned build tooling and executable
automation to be F#/.NET. `DF-ROS-2026-A049` enforced that only for
Node/JavaScript/TypeScript. The 2026-10-06 requirements audit found tracked
Python files and inline `python3` in scripts, workflows and the remote
action (`PRAXIS-MISC-08`). The repository owner approved fixing every gap
the audit found and, where a use genuinely cannot be ported, recording an
approved exception through this repository's exception process (owner
approval given in the 2026-10-06 gap-closure session, relayed to the
executing agent in its brief; recorded here by that agent).

# Decision

1. `ros.json` `implementationPolicy.prohibitPythonAutomation: true` makes
   `./praxis architecture check` and `validate` fail on a tracked `*.py`
   file, and on a `python`/`python3` invocation (or `actions/setup-python`)
   in a tracked shell script, PowerShell script, workflow or action, unless
   an exact-path exception citing an accepted decision covers it. The flag
   is opt-in, as `prohibitNodeArtifacts` is.
2. Ported to F#/.NET now: the release bump script, the remote enablement
   script, and the native-release and ros-fs-assets workflows use
   `scripts/praxis-tooling.fsx` (run with `dotnet fsi`) and GNU `date`;
   `praxis-validation.yml` no longer sets up Python.
3. Approved exceptions, each by exact path:
   - **Frozen research instruments**:
     `research/experiments/EX-ROS-2026-A021-harness/session_metrics.py`,
     `research/experiments/EX-ROS-2026-A022-target-gate/inventory.py`,
     `research/experiments/EX-ROS-2026-A022-target-gate/signal/signal_structure.py`
     and `research/experiments/EX-ROS-2026-A021-evaluation-kit/prepare-blind-bundle.sh`.
     They are evidence instruments, not automation the repository runs:
     accepted evidence (`EV-ROS-2026-A064`, `EV-ROS-2026-A065`,
     `EV-ROS-2026-A072`) cites them as the procedure that produced its
     results, and the preregistered `EX-ROS-2026-A023` uses
     `session_metrics.py` "unchanged and pinned by hash". Rewriting them
     would change the instrument those records depend on. Expires when
     `EX-ROS-2026-A023` concludes; a later experiment uses an F# instrument
     (the `anthropic-claude-session` adapter already replaces
     `session_metrics.py` for telemetry).
   - **Runtime-free bootstrap**: `scripts/praxis-bootstrap.sh` installs the
     native Praxis release on a runner with no .NET, so it cannot run F#
     before the thing it installs exists. Permanent while the bootstrap
     must be runtime-free (`DF-ROS-2026-A041` section 9).
   - **Pinned remote adapter**: `scripts/praxis-remote-persist.sh`,
     `scripts/praxis-remote-inbox.sh`, `.github/actions/praxis-remote/action.yml`
     and `.github/workflows/praxis-remote.yml`. They run on runners without
     .NET, with only the pinned native Praxis release, so their JSON
     handling can move into Praxis commands only once a release carrying
     those commands is pinned. Follow-up: `PRAXIS-MISC-10` adds the
     commands, and switches the scripts after the release that carries them
     is pinned in `.echelon/toolchain.json`; the exception is removed then.

# Consequences

- New Python automation fails the build; an exception needs a decision.
- The four remote-adapter paths keep `python3` until `PRAXIS-MISC-10`.
- `RQ-ROS-2026-A024` is implemented with these recorded exceptions.
