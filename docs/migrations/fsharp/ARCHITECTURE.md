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

## Implemented artifact slice

`Ros.Domain.Artifacts` owns the artifact vocabulary, identifier/kind policy,
reference checks, and registry projection. `Ros.Application.Artifacts` owns
the validation/check/build use cases and explicit `Failed` versus
`Indeterminate` effect outcomes. `Ros.Infrastructure.Artifacts` owns confined
filesystem discovery, the compatibility front-matter parser, and one-file
atomic registry replacement. `Ros.Contracts` owns deliberate JSON renderers;
the CLI only maps typed outcomes to command output and exits.

The slice's commands are `ros-fs artifacts validate [--json]`, `ros-fs
registry build [--dry-run]`, and `ros-fs registry check`. It intentionally
does not claim the broader Node `validate` contract, which additionally owns
work, telemetry, and stale-registry checks. F# registry check is artifact-only
until those semantic areas have their own slices.

## Artifact projection persistence seam

MIG-05 begins with a deliberately narrow cross-runtime lease for the generated
artifact registries. Node and F# use the same SHA-256-named `.ros/locks` lease
and a versioned `.ros/transactions/artifact-registries.json` write set. A normal
build locks, recovers a pending generated-registry transaction, plans all
changes, durably records the set, atomically replaces each registry, and removes
the record. Recovery replays only the eight configured registry paths; malformed
or unknown transaction data is rejected as indeterminate rather than followed.

This is safe because canonical Markdown remains source of truth and registry
projection is idempotent. It is not a generic transaction implementation, does
not version canonical inputs, and must not be reused for work or telemetry state
without their own recovery/authority design.

## Git provenance seam

MIG-06 adds a read-only F# boundary for repository status. The domain
result is closed over `Clean`, `Changed`, and `Unavailable`; a failure can no
longer be represented as an empty change list. Tracked status preserves separate
index and work-tree deltas, while rename/copy changes retain both destination
and origin paths. Infrastructure alone invokes `git status --porcelain=v1 -z`,
the application exposes the observation use case, Contracts owns the versioned
JSON shape, and the CLI maps unavailable to a non-zero exit.

Production work and telemetry consumers now share `tools/ros_git.mjs`, whose
versioned observation shape is byte-for-byte compatible with the F# contract.
Work uses destination paths for rename attribution, permits only the documented
greenfield non-repository case, and rejects other unavailable status before
completion effects. Telemetry preserves unavailable ending state instead of
emitting a measured zero. The two former fail-open Git helpers were removed.

The Node adapter remains the installed process implementation because MIG-01
did not authorize a .NET consumer dependency. F# therefore owns the typed
semantic contract while differential tests constrain the compatibility
implementation; a production runtime switch still belongs to the distribution
slice. No custom Git behavior replaces the Git executable.

## Live-work decision seam

MIG-07 begins with the pure semantic core of the live-work lifecycle. Four
states (`Ready`, `Active`, `Blocked`, `Complete`) and four requested actions
encode only the five characterized legal edges. The decision also rejects a
missing block reason and missing configured evidence types. These decisions
have no filesystem, Git, clock, telemetry, event, or persistence effects.

`ros-fs work decide` is a shadow diagnostic surface with an explicit JSON
result. It is not a state-changing command and does not claim that evidence
paths exist. Application/effect orchestration and comparative persisted
context/event behavior remain required before a production switch.

## Work-state recovery seam

The second MIG-05 sub-slice defines a bounded `work-state` recovery journal for
the final two live-transition projections, in their characterized order:
`.ros/events/events.jsonl` then `.ros/context/current.json`. Each declared write
stores the before-content SHA-256, intended after-content SHA-256, and complete
replacement content. Recovery preflights both targets before writing: only the
recorded before state or already-applied after state is legal. Divergence is an
`Indeterminate` outcome and leaves the journal and both targets untouched.

Production Node live-work transitions now use this record while holding the
existing `work-protocol` lease. Before making a new transition they recover any
compatible pending record; normal completion leaves no journal. The F# and
Node implementations accept the same JSON shape, hashes, ordered targets, and
divergence rules. This is shared recovery infrastructure, not an authority
switch: Node still decides and performs every state-changing work command.

Telemetry execution files and backlog queue/projection files remain explicitly
outside this two-file unit. Telemetry effects currently precede journal
preparation, so backlink/finalization validation detects—but does not
automatically repair—an interruption in that earlier interval. Backlog and
telemetry require bounded store-specific recovery designs.

## Backlog-state recovery seam

The third MIG-05 sub-slice gives the captured backlog its own bounded recovery
unit: authoritative `.ros/work/queue.json` followed by derived
`.ros/work/queue.md`. Node and F# share a versioned, hash-preconditioned journal
contract. Production capture, update, attachment-reference, and backlog
transition operations now hold `work-protocol` across the complete
read/modify/write cycle, and live transitions acquire that same capability.

The unit deliberately excludes live context/events, telemetry, and attachment
bytes. Attachment content is written before its queue reference, preserving the
existing failure direction: interruption can leave an orphan but not a missing
referenced file. The Markdown file remains a rebuildable projection and is
regenerated on backlog changes, not claimed as independent authority.

## Telemetry-link recovery seam

Telemetry execution JSON differs from the multi-file projections: each record
is already atomically replaced under an execution-specific lease. MIG-05
therefore keeps that boundary and addresses the cross-store creation seam.
Execution creation and context linking are composed under `work-protocol`, and
the backlink is written through the existing work-state recovery record.

The typed F# decision distinguishes starting a new record, recovering exactly
one detached active/finalized record, requested-ID conflict, and ambiguous
detached evidence. Production uses the same behavior. An exact existing
`--execution-id` is the repair/idempotency key; ambiguous recovery never chooses
silently. Provider mapping and metric semantics remain MIG-08 work.

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
7. A registry-file replacement is atomic per file; the bounded replay record
   recovers an incomplete generated-registry set but is not a general-purpose
   transaction facility.
