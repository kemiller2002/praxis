# ROS Central Service and Integration Contract Upgrade — Migration Plan

This plan restates the migration specification's phases and work items in
this repository's own terms, grounded in the inventory in
`CURRENT-SYSTEM-INVENTORY.md` and the evidence in `BASELINE.md`. It does
not authorize execution of any phase beyond Phase 0. Per the
specification's own instruction ("do not execute later work items merely
because they are listed"), each phase below is a plan to be approved and
entered deliberately, not a queue to run through.

## Governing constraints (apply to every phase)

1. Existing ROS behavior must keep working at every commit. If a change
   under this migration breaks existing validation, stop and fix before
   continuing — never weaken an existing test to make a migration step
   pass.
2. No component is deleted, moved, or mechanically rewritten from an
   existing language into F# for its own sake. `CURRENT-SYSTEM-INVENTORY.md`
   marks every existing component "Replacement planned: No" except where
   explicitly noted; that marking does not change without its own
   characterization → tests → parallel run → equivalence → real-use
   evidence, per the specification's non-negotiable rule.
3. Local repository-level ROS (`./ros validate`/`build`/`test`, SDE
   verification) must never gain a dependency on the Central service.
   "Centralize organizational authority. Decentralize repository
   execution."
4. Every new externally-facing behavior ships behind a feature flag,
   defaulting to off/false: `ROS_CENTRAL_ENABLED`,
   `ROS_EXTERNAL_ACTIVITY_ENABLED`, `ROS_CHRONA_EXPORT_ENABLED`. These are
   migration controls, removed once the corresponding capability is fully
   adopted — not permanent configuration surface.
5. Every phase must be independently reversible by disabling its feature
   flag(s); rollback is never "revert months of architecture."
6. Metrics used to evaluate any phase are collected contemporaneously
   during that phase's implementation and shadow run — never invented or
   reconstructed after the fact.

## Target structure (semantic, additive)

Existing: `Ros.Domain`, `Ros.Contracts`, `Ros.Application`,
`Ros.Infrastructure`, `Ros.Cli`, `Ros.Tests` (per `Ros.slnx`).

New, added incrementally as each phase's entry criteria are met — never
all at once:

- `Ros.Integration` — the standalone, dependency-free public contract
  package (`EchelonFoundry.Ros.Integration`). Phase 1.
- `Ros.Integrations.GitHub` — GitHub SDK boundary (repo discovery,
  identity mapping, GitHub App auth, GitHub datastore writes). Phase 8+,
  only once domain/contract boundaries are stable.
- `Ros.ProjectAdministration` — organization/project/repository/work-item
  relationship model. Phase 7.
- `Ros.Persistence` — Central's own managed persistence (separate from
  `Ros.Infrastructure`'s file-based local repositories). Phase 4, only if
  the existing storage model is confirmed insufficient (see "Database"
  below).
- `Ros.Host` — the single Central F# executable. Phase 4.

This list is a ceiling, not a target: "start with the smallest number of
assemblies necessary." A phase that can be satisfied without introducing
a new assembly should not introduce one.

## Phase-by-phase plan

### Phase 0 — Baseline and inventory (WI-1) — **in progress, this document is part of it**

- **Entry criteria**: clean working tree, known commit SHA.
- **Work**: `BASELINE.md` (done), `CURRENT-SYSTEM-INVENTORY.md` (done),
  this `MIGRATION-PLAN.md` (in progress), `COMPATIBILITY-MATRIX.md`
  (next).
- **Exit criteria**: all four documents exist and are reviewed; baseline
  tag exists (see open item below); no code has been modified.
- **Status / open item**: the baseline tag `ros-central-integration-baseline`
  could not be pushed from this session (hard 403, documented in
  `BASELINE.md`) — needs a human with full push access to run the
  documented three-line command before Phase 0 is formally closed. This
  does not block writing the remaining documents, since the tag points at
  a commit that is already on `main` and independently verifiable.

### Phase 1 — Integration contract (WI-2, WI-3, WI-4)

- **Entry criteria**: Phase 0 exit criteria met and reviewed by the user.
- **Work**:
  - WI-2: write down the receiver-owned contract standard as a short
    governance note (feeds into the permanent-rules addition below).
  - WI-3: create `src/Ros.Integration/Ros.Integration.fsproj`, namespace
    `EchelonFoundry.Ros.Integration`, zero dependencies beyond
    `FSharp.Core`/`System.*`. Types are illustrative in the
    specification, not prescriptive — use ROS's existing vocabulary
    (e.g. `WorkItemId`-shaped identifiers already exist in
    `Ros.Domain.Work.Identity`; the new package defines its own
    identifier types independently, since it must not reference
    `Ros.Domain` at all, but should mirror naming rather than invent
    divergent terms). `create` returns
    `Result<ActivityObservation, Error list>` with a closed `Error`
    union; construction validates structural well-formedness only
    (never "is this legal for this project"), which stays a
    `Ros.Domain` concern.
  - WI-4: `serializeActivity`/`deserializeActivity`, golden-file tests
    under `tests/contracts/activity-observation-v1.json`, proving
    object→JSON, JSON→object, old V1 JSON→current package,
    unknown-fields policy, missing-required-field rejection,
    duplicate-processing-ID preservation.
- **Exit criteria**: package builds standalone; golden tests pass; no
  existing project references it yet (it is inert until Phase 3+).
- **Compatibility requirement**: contract version field (`"1"` or
  `"1.0"`) is present and separate from the NuGet package's own semver
  from the very first commit — never added retroactively.

### Phase 2 — Isolated consumer tests + package CI (WI-5, WI-6)

- **Work**: `tests/Ros.Integration.Consumer.Tests` referencing only the
  published-shape public package surface (never `Ros.Domain`); new thin
  workflow `integration-package-ci.yml`, paths-scoped to
  `src/Ros.Integration/**`, that restores/builds/tests/packs and uploads
  the `.nupkg` as a workflow artifact. No publish step in this workflow.
- **Exit criteria**: workflow runs green on a PR touching only
  `src/Ros.Integration/**`; `ros-validation.yml` is provably untouched
  and still green on the same PR (both workflows' runs checked, not
  assumed).
- **Compatibility requirement**: if any test in
  `Ros.Integration.Consumer.Tests` needs to reference an internal type to
  pass, that is a signal the public contract boundary is wrong — fix the
  boundary, not the test.

### Phase 3 — Central domain design (WI-7, WI-8)

- **Work**: design (with tests, no host yet) `Ros.ProjectAdministration`'s
  `Organization → Project → RepositoryAssignment` model, preferring
  immutable GitHub repository IDs over names as identity authority; and
  the `ExternalActivityState`/`OutboundDeliveryState` explicit state
  machines with only named legal transitions (no arbitrary status
  assignment).
- **Exit criteria**: state models and their transition rules are unit
  tested in isolation, no host or persistence wiring yet.
- **Compatibility requirement**: none yet — this is new code with no
  existing consumers.

### Phase 4 — Central host (WI-9, WI-10, WI-11)

- **Entry criteria**: Phase 3 domain model reviewed; a decision recorded
  on whether the existing file-based storage model can serve Central's
  needs or a managed database (preferring PostgreSQL, no auto-added ORM)
  is genuinely required — "don't pick a database just because Central
  exists."
- **Work**: `Ros.Host` single executable; `GET /health`, `GET /version`,
  `POST /integration/v1/activities`, `POST /integration/v1/executions`;
  idempotent persistence keyed on `source + external ID`; transport →
  contract validation → ROS command → domain validation → state
  transition → persistence pipeline with explicit `400`/`422`/`409`/`500`
  error codes, no exceptions as control flow.
- **Exit criteria**: a retried `POST /integration/v1/activities` with the
  same `activityId` never creates a duplicate record (proven by test, not
  assumed); `ROS_CENTRAL_ENABLED=false` by default, so no existing
  workflow talks to this host yet.
- **Compatibility requirement**: `Ros.Host` depends inward on
  `Ros.Integration` (never the reverse); `Ros.Integration → Ros.Domain →
  Ros.Persistence` remains a forbidden dependency direction.

### Phase 5 — Outbound integration / outbox (WI-12)

- **Work**: `OutboundIntegration` record and processing loop inside the
  monolith (no message bus), covering retry/durability/auditability/
  idempotency for outbound deliveries.
- **Exit criteria**: outbox create/retry/completion unit tested; no
  outbound target wired live yet.

### Phase 6 — First package publication (WI-13)

- **Work**: `publish-integration-package.yml`, triggered only on
  `ros-integration-v*` tags, `permissions: packages: write`,
  `secrets.GITHUB_TOKEN` (no permanent PAT); publish
  `EchelonFoundry.Ros.Integration` `1.0.0` to GitHub Packages only after
  restore/build/unit/contract/serialization-golden/compatibility tests
  and pack all pass.
- **Exit criteria**: package installable by a separate consumer test
  project via `<PackageReference Include="EchelonFoundry.Ros.Integration"
  Version="1.0.0" />` (pinned, never `*`/floating).

### Phase 7 — Project Administration goes live; first producer repo (WI-14, WI-15)

- **Entry criteria**: Phases 4–6 exit criteria met; `ROS_CENTRAL_ENABLED`
  flipped on for exactly one test repository.
- **Work**: connect one repository to Central; run shadow mode — existing
  ROS behavior in that repo continues unchanged, Central receives and
  correlates activity but does not alter local behavior; compare old vs.
  new metrics; document differences before considering authority switch.
- **Design note**: `ros_hub_server.mjs`'s existing `/api/repos` +
  cross-repo `/api/work` aggregation (see
  `CURRENT-SYSTEM-INVENTORY.md` §2.3) is prior art for repository
  mapping; the new `Organization → Project → RepositoryAssignment` model
  should be evaluated against it for conceptual overlap, but
  `ros_hub_server.mjs` itself is not modified or replaced by this phase.
- **Exit criteria**: shadow comparison shows no unexplained divergence
  between old and new metrics on the one connected repository, for an
  agreed observation period.

### Phase 8 — Chrona integration contract definition (WI-16)

- **Work**: define `Chrona.Integration` requirements jointly with Chrona;
  until it exists, use `IntegrationTarget = Chrona | Summa | Other of
  string` and `DeliveryState = NotReady | Pending | Delivered | Failed`
  so ROS can collect activities with Chrona delivery state `NotReady`
  without blocking on Chrona's own timeline.
- **Exit criteria**: requirements document agreed with Chrona's side;
  `Ros.Integrations.GitHub` boundary (repo discovery, identity mapping,
  GitHub datastore writes, event interpretation, GitHub App auth) is
  built here or in Phase 9, strictly after domain/contract boundaries are
  stable — GitHub App hosting is infrastructure, added last.

### Phase 9 — Consume Chrona.Integration; shadow ingestion (WI-17, WI-18, WI-19)

- **Entry criteria**: `Chrona.Integration` package published by Chrona's
  side.
- **Work**: ROS references `Chrona.Integration` (receiver-owned contract
  rule: ROS is the sender here, Chrona is the receiver of time-entry
  data, so ROS depends on Chrona's contract, not the reverse); implement
  the GitHub datastore adapter; ROS constructs a `TimeObservation`-like
  payload via `Chrona.Integration` and writes it to Chrona's agreed
  GitHub datastore location — ROS never creates Chrona's final
  authoritative time entry.
- **Exit criteria** (explicit, from the specification): one observation
  produces one candidate; repeating the Chrona load still yields one
  candidate; repeating the ROS delivery still yields one candidate — all
  proven before enabling real time-entry generation (`ROS_CHRONA_EXPORT_ENABLED`
  stays `false` until this is demonstrated).

### Phase 10 — Expand producer repositories (WI-20)

- **Work**: incrementally onboard additional repositories/applications
  beyond the first test repo, one at a time, each repeating the Phase 7
  shadow-comparison discipline before being trusted.
- **Exit criteria**: per-repository, not global — each repo's shadow
  comparison clears before the next repo is onboarded.

### Phase 11 — Retirement of superseded components

- **Entry criteria** (explicit, from the specification, all four
  required): a tested replacement exists; all known consumers have
  migrated; no workflow or repository still references the old
  component; a rollback path exists.
- **Candidates to evaluate at this phase** (not decided now): whether any
  part of `ros_hub_server.mjs`'s repo-registry function can be retired in
  favor of Central Project Administration data — only if all four entry
  criteria are independently satisfied for that specific function. No
  other component in `CURRENT-SYSTEM-INVENTORY.md` is currently a
  retirement candidate.

## Permanent governance additions (not tied to a single work item)

The specification requires three rules to be added permanently to ROS's
governance, verbatim in spirit:

1. **Receiver-Owned Integration Contracts** — the receiver of data owns
   the contract type for that data; a sender depends on the receiver's
   published contract, never the reverse, except when the "receiver" is
   ROS accepting externally-owned input (e.g. an app sending ROS-owned
   activity data uses `Ros.Integration`, which ROS owns).
2. **Compatibility Before Replacement** — no existing component is
   removed until its replacement is characterized, tested to equivalence,
   run in parallel where practical, and proven in real use; architectural
   cleanliness is never sufficient justification on its own.
3. **Complexity Requires Evidence** — infrastructure (databases beyond
   what's demonstrated necessary, message buses, microservices,
   orchestration frameworks) is added only once a concrete, observed need
   is documented — never speculatively.

**Recommended placement**: `AGENTS.md` (the canonical ROS agent contract)
already establishes core governance rules near its top (semantic
authority, never fabricate, escalate high-impact decisions, etc.) — these
three rules fit as a new subsection there, since they are agent-facing
behavioral rules, not architecture diagrams (those belong in
`.sde/architecture/`, which could carry a cross-reference). This
placement decision is a recommendation for user review, not yet applied
in this pass — adding it is out of scope for Phase 0, since Phase 0 is
documentation-of-current-state only, and editing `AGENTS.md` is a
substantive governance change deserving its own explicit approval and
commit, not a byproduct of writing the migration plan.

## Explicit non-goals for every phase above

Restated from the specification's forbidden-behaviors list, because they
apply uniformly and are easy to violate incrementally without noticing:
no rewrite-from-scratch; no deleting a script because an F# equivalent
now exists; no file moves for cleanliness alone; no unrelated behavior
changes riding along with a migration commit; no dependency or .NET
version upgrades without necessity; no schema changes without
compatibility handling; no command renames without aliases; no silent
workflow input/output changes; no in-place serialized-format changes; no
forcing simultaneous multi-repo migration; no new central dependency for
local ROS operation; no infra frameworks/microservices/queues without
demonstrated need; no integration package exposing internals; no producer
defining a receiver's schema.

## What happens after this document

Per the plan stated to the user at the start of this task: after
`COMPATIBILITY-MATRIX.md` is written, Phase 0/WI-1 is complete and this
session stops for review. No Phase 1 code (including
`src/Ros.Integration`) is created without explicit go-ahead.
