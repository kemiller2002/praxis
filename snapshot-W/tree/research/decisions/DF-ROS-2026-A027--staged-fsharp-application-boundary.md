---
id: DF-ROS-2026-A027
title: Staged F# application boundary for ROS
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-07
updated: 2026-09-07
author_agent: openai-codex
supporting_evidence:
  - EV-ROS-2026-A015
  - EV-ROS-2026-A018
related_documents:
  - RP-ROS-2026-A017
  - EX-ROS-2026-A020
  - docs/migrations/fsharp/ARCHITECTURE.md
supersedes: []
superseded_by: []
tags: [architecture, fsharp, migration, sde, compatibility]
confidence: medium-high
---

# Context

ROS currently has one accidental application kernel spread mainly across
`tools/ros_cli.mjs` and `tools/ros_telemetry.mjs`, with additional semantic
decisions in configuration, schemas, bootstrap, workflow shell, and UI
vocabularies. The immutable inventory `EV-ROS-2026-A018` confirms that file and
language boundaries do not match semantic boundaries. Node/npm is also the
current installed distribution contract, while no consumer-platform evidence
yet supports replacing it with a .NET-only package.

The current SDE inputs are release `1.1.1`. The governing documents and exact
document versions for this decision are:

| Authority | ID/version/status |
|---|---|
| `.sde/architecture/FOUR-TIER-ARCHITECTURE.md` | `SDE-DOCTRINE-003` / `0.2.0` / accepted |
| `.sde/architecture/BOUNDARY-PRESERVATION.md` | `SDE-DOCTRINE-004` / `0.2.0` / accepted |
| `.sde/architecture/STRUCTURAL-LOCALITY.md` | `SDE-DOCTRINE-008` / `0.2.0` / accepted |
| `.sde/method/CONSTRUCTION-METHOD.md` | `SDE-METHOD-001` / `0.2.0` / draft |
| `.sde/method/CHANGE-CLASSIFICATION.md` | `SDE-METHOD-002` / `0.2.0` / draft |
| `.sde/method/VERIFICATION-METHOD.md` | `SDE-METHOD-003` / `0.2.0` / draft |
| `.sde/method/AGENT-EXECUTION-RULES.md` | `SDE-METHOD-004` / `0.2.0` / draft |
| `.sde/method/NAVIGATION-AND-CONTEXT.md` | `SDE-METHOD-007` / `0.2.0` / draft |
| `.sde/method/FEATURE-MANIFESTS.md` | `SDE-METHOD-008` / `0.2.0` / draft |
| `.sde/reference/ENGINEERING-METRICS.md` | `SDE-REFERENCE-005` / `0.2.0` / draft |
| `.sde/reference/GLOSSARY.md` | `SDE-DOCTRINE-005` / `0.2.0` / accepted |

Accepted SDE doctrines govern architecture. Draft methods govern this mission
as an explicitly measured experiment, not as proven universal methodology.
ROS governance, especially `DF-ROS-2026-A001`, A002, A003, A006, A007, A008,
A009, and A010, remains semantic authority over current behavior.

# Decision

ROS will migrate by vertical semantic capability into one coherent F#
application surface. The target dependency direction is:

```text
Ros.Cli / platform adapters
        -> Ros.Application
        -> Ros.Domain

Ros.Infrastructure -> Ros.Application ports + Ros.Domain
Ros.Contracts      -> explicit public boundary shapes
composition root   -> all required projects
```

The physical solution starts with five production projects and one verification
project:

- `Ros.Domain`: pure stable semantic types, decisions, invariants, and outcomes;
- `Ros.Contracts`: explicit versioned wire/CLI shapes and boundary mapping;
- `Ros.Application`: capability handlers, projections, cross-entity validation,
  and effect ports;
- `Ros.Infrastructure`: filesystem/front-matter/Git/process implementations;
- `Ros.Cli`: argument parsing, composition, rendering, and exit mapping; and
- `Ros.Tests`: dependency-free behavior, contract, architecture, differential,
  and adversarial verification.

Projects are boundaries, not layers for their own sake. Code remains grouped
by semantic area within them, beginning with `Artifacts`. Dependency checks
must reject outward references from Domain and reject Infrastructure/CLI
references from Application. External effects are requested through explicit
function/record ports; a framework DI container is not introduced.

The first production-quality slice is artifact validation and registry
projection. It includes a typed artifact model, explicit front-matter boundary,
pure policy and projection functions, filesystem application port/adapter,
shadow CLI, characterization fixtures, Node/Python/F# differential execution,
and rollback. It does not include work/telemetry writes.

The shadow executable targets framework-dependent `.NET 10` for this repository
and CI only. This is a reversible experiment surface, not the consumer
distribution decision. Node/npm launchers, profile manifests, HTTP/UI edges,
provider adapters, and GitHub YAML remain unchanged production authorities.
No starter profile includes the F# binary and `./ros` continues to select Node.
Consumer packaging remains blocked on explicit macOS/Linux/Windows acquisition,
size, startup, update, offline, integrity, and rollback evidence.

Canonical Markdown remains the artifact source of truth. Registry JSON remains
a deterministic disposable projection. The F# shadow may write registries only
when invoked explicitly; differential tests use isolated copies. Public
wire forms use explicit boundary code and deterministic property ordering,
never default union/record serialization. Domain types do not absorb JSON,
filesystem, Git, process, npm, GitHub, provider, project-administration, or Time
Entry concerns.

# Architecture challenge before treatment

The planned structure was challenged against the required questions before F#
source was added:

- **Existing-file translation:** rejected. Projects and modules follow artifact,
  work, execution, evidence, and metric responsibilities, not JavaScript files.
- **Renamed scripts/services:** each planned handler must expose a typed request,
  typed outcome, and effect port. A function that only shells to the old script
  is not a migrated slice.
- **Over-centralization:** project administration stays a separate bounded
  context; Time Entry remains an external future projection; browser and npm
  edges remain separate.
- **Premature abstractions:** no generic repository, state-machine framework,
  workflow engine, serializer framework, reflection, or DI framework is allowed.
- **State value:** only stable, evidenced choices become closed unions. Evolving
  provider/SDE extensions remain explicit open data at their boundary.
- **Existing tools:** Git, dotnet, npm, GitHub, TypeScript, and OS primitives are
  invoked through boundaries rather than reimplemented.
- **Effects and retry:** the first slice is deterministic/read-mostly; registry
  writes are declared projections. Stateful transaction/recovery design is a
  later slice and is not faked by this skeleton.
- **CLI/platform stability:** the new surface is shadow-only. Production callers
  cannot observe it until an explicit acceptance decision.
- **Cheap architecture proof:** artifact management exercises parsing, policy,
  deterministic output, filesystem effects, commands, and differential tests
  without risking work/telemetry authoritative state.
- **Deterministic discovery:** `SDE-MAP.md` and feature manifests now route to
  current and migration authorities; they contain navigation, not duplicate
  semantics.

Two revisions resulted. First, the broad distribution decision in A017 was
narrowed to framework-dependent repository-local shadow execution; consumer
distribution remains unresolved rather than inferred from one installed SDK.
Second, full V1 work/execution/provider codecs were removed from the first
slice: only artifact metadata and registry contracts needed by that capability
will be implemented now. This prevents speculative types and a horizontal
“contracts first” rewrite.

The user's fallback checkpoint labels conflict with the installed SDE convention
at T2/T3. Per the instruction to use the repository convention, T2 means
semantic foundation and T3 means first vertical slice complete. This
architecture challenge is a pre-treatment/T2 gate; T3 will be recorded only
after the slice runs.

# Alternatives

- **Mechanical script-to-F# translation:** rejected because it preserves
  accidental coupling and duplicates semantic authority.
- **Immediate full rewrite/switch:** rejected because stateful and distribution
  contracts lack sufficient comparative evidence.
- **Keep Node permanently without a typed experiment:** rejected for this
  mission because duplicated stable semantics and hidden state decisions are
  evidenced and the user authorized staged implementation.
- **One giant F# project or `Program.fs`:** rejected because dependencies and
  semantic ownership would remain hard to enforce and navigate.
- **Database/event store now:** rejected because current file scale does not
  justify a second source of truth; transaction/recovery is a later explicit
  persistence problem.
- **Rewrite HTTP/UI/workflows in F#:** rejected because those are valid platform
  adapters/declarations and do not prove semantic migration.
- **Make JSON Schema the sole runtime engine:** rejected as a full solution;
  schemas remain boundary declarations but cross-artifact rules and projections
  still require typed semantic logic.

# Consequences

The repository temporarily has two executable implementations for the artifact
slice, but only Node is authoritative and the F# surface is visibly shadowed.
That duplication is measured and time-bounded by the roadmap. The structure
makes later work/telemetry slices easier by establishing build, command,
boundary, filesystem, architecture, fixture, and differential conventions.

Framework-dependent .NET 10 reduces experiment setup and avoids committing
large runtime binaries, but it proves nothing about installed consumer
availability. Five production projects add structure; if later slices do not
reuse their boundaries, consolidation should be considered rather than
preserving ceremonial layering.

# Reversibility and validation

Rollback before an authority switch is to stop invoking or remove the shadow
projects; Node, Python oracle, npm package, profiles, workflows, and canonical
data remain intact. Every migrated capability requires compile, architecture,
boundary/contract, behavior, differential, integration/smoke, adversarial or
mutation-style rejection, and review evidence. `EX-ROS-2026-A020` freezes the
first slice's acceptance and falsification criteria. A later accepted decision,
not this record alone, is required to redirect `./ros` or remove a legacy path.
