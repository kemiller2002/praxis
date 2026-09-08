# ROS F# migration architecture

`DF-ROS-2026-A027` is the decision authority. This document is the concise
implementation map.

## Current semantic system

```text
npm/bootstrap + launchers
        |
Node CLI kernel -------- Node telemetry kernel
  |       |                        |
  |       +-- work/context/events  +-- execution records/metrics/providers
  +-- artifacts/registries/adapters/Git
        |
Node HTTP + TypeScript UI     project-administration hub
        |
GitHub workflow declarations and npm publication
```

Semantic decisions are concentrated but not isolated: Node/Python/schema
artifact rules duplicate; work and telemetry duplicate Git discovery; bootstrap
constructs domain state; UI duplicates vocabularies; release YAML mixes policy
and platform mechanics.

## Target composition

```text
platform adapters / Ros.Cli
            |
       Ros.Application  <----- Ros.Infrastructure
            |
         Ros.Domain

Ros.Contracts is the explicit versioned boundary vocabulary used at edges.
```

- Domain contains pure stable types/decisions and no host dependencies.
- Application contains capability handlers, projections, validation, and small
  effect-port records/functions.
- Infrastructure implements filesystem/front-matter/Git/process effects.
- CLI composes dependencies and maps typed outcomes to public output/exit.
- Contracts preserves deliberate V1 shapes and unknown extensions; default F#
  serialization never becomes an accidental external contract.
- Code is feature-local inside those boundaries (`Artifacts` first), rather
  than one file per former script or one giant program.

## State architecture

| Lifecycle | Closed state now | Authority | Migration treatment |
|---|---|---|---|
| Backlog | captured, ready, blocked, abandoned | queue + work kernel | later typed transition slice |
| Live work | ready, active, blocked, complete; configured local mapping | context + work kernel | later typed transition/evidence slice |
| Execution | active, finalized | telemetry kernel | later typed lifecycle slice |
| Metric capability | supported-observed, supported-unavailable, unsupported, unknown, derived, estimated | telemetry kernel/catalog | typed stable cases plus open provider fields later |
| Artifact lifecycle | kind-specific status vocabularies | artifact policy | first slice validates without inventing a generic state machine |
| Publication | pending plus success/failure/unknown adapter outcome | events/receipts/adapters | defer until retry/acknowledgement contract exists |

For every state-changing future slice, model legal transitions, guards,
required evidence/capability, requested effects, rejection/unknown outcomes,
retry semantics, and version checks before moving the writer.

## Effect boundaries

Filesystem, Git, process execution, clock, environment, HTTP, GitHub, npm,
provider input, secrets, and persistence are host effects. Domain decisions
receive typed observations and return typed requests/outcomes. External process
exit is not automatically semantic success.

## Sources of truth

- canonical research/governance: Markdown records;
- registries: deterministic disposable projection;
- captured backlog: `.ros/work/queue.json`;
- live/completed work: `.ros/context/current.json`, with events as chronology;
- execution facts: per-execution versioned JSON;
- metric meanings: `telemetry/metrics.json`;
- configuration: versioned `ros.json`;
- project membership: current hub registry, in its separate bounded context;
- release version: committed package metadata plus explicit registry observation.

No database, event store, Project Administration authority, or Time Entry model
is introduced by this migration.

## Retained adapters

Node/npm bootstrap, tiny launchers, Node HTTP servers, TypeScript clients,
provider adapters, GitHub Actions YAML, npm publication, profile manifests, and
external tools remain where they are genuinely platform-specific. Retention is
not permanent approval for domain logic inside them; the inventory identifies
which decisions move in later slices.

## Distribution boundary

The first F# surface is framework-dependent `.NET 10` and repository-local.
It is not added to starter profiles or selected by `./ros`. A production
distribution decision requires explicit macOS/Linux/Windows installation,
startup, size, offline/update, checksum, version-selection, and rollback data.

## Architecture fitness rules

1. `Ros.Domain` may reference only FSharp.Core/runtime primitives.
2. `Ros.Application` may reference Domain/Contracts, never CLI or
   Infrastructure.
3. Infrastructure and CLI point inward; composition happens only at the edge.
4. Public bytes and diagnostics are produced by explicit codecs/renderers.
5. A feature slice includes semantics, handler, effect boundary, CLI, tests,
   telemetry/traceability, compatibility, and rollback.
6. Architecture verification must include a demonstrated rejection path.
