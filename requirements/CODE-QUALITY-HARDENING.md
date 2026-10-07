# Praxis Code-Quality Hardening Work Items

Status: approved work plan
Date: 2026-10-04
Origin: post-implementation review of provider usage pacing and remote executor version skew

## Goal

Preserve the good provider-neutral pacing domain model while removing expedient
boundary, persistence, and process choices. Make the same class of shortcuts
harder to introduce in future Praxis work.

## Immediate pacing hardening

### PRX-QUAL-001 — Restore four-tier architecture for pacing
Priority: high

Split the current pacing implementation so domain policy remains in
`Praxis.Domain.Pacing`, orchestration and ports live in
`Praxis.Application.Pacing`, provider/filesystem/process implementations live
in `Praxis.Infrastructure.Pacing`, and `PacingCommands.fs` becomes a thin CLI
adapter.

Acceptance:
- CLI contains parsing/rendering only and no HTTP, Keychain, filesystem-state,
  process-protocol, locking, sleeping, or provider-normalization policy.
- Application code depends on typed ports, not concrete providers.
- Infrastructure contains Codex, Claude, persistence, clock/sleeper, and logging
  adapters.
- architecture tests enforce the dependency direction.
- behavior remains compatible with the current public CLI.

### PRX-QUAL-002 — Version and harden pacing persistence
Priority: critical
Depends on: PRX-QUAL-001

Replace ad-hoc `hold.json` and provider snapshot persistence with a
schema-versioned state contract and explicit read outcomes.

Acceptance:
- persistent documents carry schema version and revision.
- parse/corruption/version failures are represented as typed
  `Indeterminate`/fault outcomes, never silently converted to empty state.
- safety state uses atomic writes and bounded recovery semantics comparable to
  Praxis work-state persistence.
- state migration behavior is documented and tested.
- unknown newer state refuses unsafe gating unless an explicit override is
  supplied.

### PRX-QUAL-003 — Persist hard-limit holds explicitly
Priority: critical
Depends on: PRX-QUAL-002

Hard >98% exhaustion SHALL be durable state rather than an incidental property
of a cached provider response.

Acceptance:
- hard holds record provider, quota identity, scope, observed usage, reset
  identity/time, and creation evidence.
- hard holds expire only from trusted reset/fresh evidence according to policy.
- losing or partially parsing a provider snapshot cannot silently authorize work
  before an established hard hold expires.
- weekly holds also record reset identity so a new quota window cannot inherit
  the prior window's hysteresis latch accidentally.

Status (2026-10-06, PRAXIS-QUAL-01): implemented. Hard and weekly holds
persist their creation evidence (`evidence: {observedAt, reference}` in
`hold.json`, additive within schema 2 so older readers ignore it); holds
written without it read as unrecorded, never invented. Tests:
PacingHardeningTests "a hard hold records the reading that created it" and
the evidence round-trip/migration cases.

### PRX-QUAL-004 — Model observation completeness explicitly
Priority: critical
Depends on: PRX-QUAL-001

Replace the current whole-response boolean completeness with per-window
observation status.

Acceptance:
- each expected quota window can be Observed, Missing, Invalid, Stale, or
  Unsupported.
- a missing Codex secondary/weekly window cannot be classified fresh merely
  because the primary window exists.
- previously observed Claude scoped windows cannot disappear and silently make
  the response complete.
- partial responses preserve only the evidence justified by each window.
- provider response-shape drift produces explicit diagnostics.

Status (2026-10-06, PRAXIS-QUAL-01): implemented. `WindowStatus` is
Observed, Missing, Invalid, Stale (carried from the cache, or reported after
its reset) or Unsupported (a window kind the adapter rules do not map); every
non-observed status makes the reading incomplete and is named in the
diagnostic and in status `coverage`. Tests: PacingHardeningTests coverage
cases.

### PRX-QUAL-005 — Introduce typed provider and quota identities
Priority: high
Depends on: PRX-QUAL-001

Remove string heuristics such as model-name substring matching from policy and
centralize provider mappings in versioned adapters.

Acceptance:
- provider, model identity, quota bucket, and scope use typed values at the
  application/domain boundary.
- Codex Spark bucket selection and Claude scope mapping are explicit adapter
  rules with fixtures.
- unknown models have a declared conservative behavior rather than accidental
  substring behavior.
- adapter capability/version information is observable.

Status (2026-10-06, PRAXIS-QUAL-02): implemented. `ProviderId`,
`ModelFamily`/`ModelIdentity`, `QuotaBucket` and `QuotaScope` are typed;
model matching is whole-token equality against known families, with
unspecified, unrecognized or ambiguous models conservative. Codex bucket
selection and Claude window/scope mapping are versioned rule tables
(`PacingAdapterRules`, `codex-rules/1`, `claude-rules/1`) reported by `pacing
status`. A guard test refuses substring model matching in pacing code.

### PRX-QUAL-006 — Make pacing gate orchestration deterministic and testable
Priority: high
Depends on: PRX-QUAL-001

Inject clock, delay/cancellation, provider refresh, and state repositories.

Acceptance:
- no domain/application test depends on wall-clock sleeps.
- gate timeout, polling, refresh cadence, cancellation, and override transitions
  have deterministic tests.
- long-running synchronous hook behavior remains supported without embedding
  control flow directly in CLI code.

### PRX-QUAL-007 — Correct status and operator contracts
Priority: high

Fix the current machine-readable status inconsistency and define a stable
status schema.

Acceptance:
- JSON reports the real override state.
- text and JSON are projections of the same typed status model.
- freshness, held reasons, reset estimates, provider errors, and indeterminate
  safety state are machine-readable.
- contract tests cover override on/off and unavailable provider cases.

### PRX-QUAL-008 — Record pacing as Praxis telemetry
Priority: medium
Depends on: PRX-QUAL-001

Replace best-effort text-only `pace.log` as the primary evidence channel with
typed telemetry while retaining human diagnostics.

Acceptance:
- hold-started, hold-retained, hold-released, provider-unavailable,
  override-enabled/disabled, and hard-limit events have stable codes.
- events capture provider/scope/reason/reset without credentials or sensitive
  payloads.
- repeated polling does not spam duplicate transition events.

Status (2026-10-06, PRAXIS-QUAL-03): implemented. Typed
`praxis.pacing-event/1` events with the stable codes above (plus
`state-fault`) in `events.jsonl`; `pace.log` is their rendering. Hold
transitions are derived from persisted state; the gate deduplicates against
the last recorded event. Diagnostics are redacted and bounded.

### PRX-QUAL-009 — Integrate provider capacity into planning/work groups
Priority: medium
Depends on: PRX-QUAL-001, PRX-QUAL-008

Expose pacing/capacity as planning evidence so Praxis can prefer provider-free
work, another qualified provider, checkpointing, or waiting rather than only
blocking at a hook.

Acceptance:
- planner consumes capacity through a provider-neutral port.
- capacity uncertainty is distinct from zero capacity.
- scheduling decisions explain whether pacing affected ordering.
- provider switching never occurs without capability/model compatibility.

Status (2026-10-07, PRAXIS-QUAL-05): implemented. The planner reads
provider-neutral `ProviderCapacity` (available, constrained, exhausted,
unknown) through `PlanningReadPort.Capacity`: supplied in the observations
file, or mapped from usage-pacing state by `FilePacingCapacity` when
`PRAXIS_PACING_DIR` is set. Unknown is distinct from zero; limited capacity
orders `provider-free` items first with explaining reasons and a finding;
the planner never switches provider (PlanningCapacityTests, docs/planning.md).

### PRX-QUAL-010 — Replace release-version equality with compatibility authority
Priority: high

The regression fence added after the 3.6.0/3.7.0 skew is a useful emergency
guard, but exact `release.json == toolchain praxis` equality is not the final
design.

Acceptance:
- release metadata declares remote-executor protocol/state-schema compatibility.
- the self-hosting repository pins an existing attested release that is
  compatible with the repository state it must read.
- source development may advance to a future release version without requiring
  a not-yet-published binary.
- a post-release workflow can advance the pin only after immutable assets and
  attestations exist.
- CI proves incompatible executor/state combinations are refused before remote
  mutation.

Status (2026-10-07, PRAXIS-QUAL-06): implemented.
- `release.json` declares `compatibility` (remote protocol, and per state
  document the schema versions read and written); a fence keeps it equal to
  the compiled `StateCompatibility.current`.
- The pin is no longer tied to `release.json`: the bump changes only
  `release.json`; `scripts/praxis-remote-enable.sh --skip-release` advances
  the pin after verifying checksum and attestation and records the
  release's compatibility in `quality/release-compatibility.json`.
- A fence requires the pinned release's recorded reads to cover the
  committed state, or an owned, unexpired exception. QX-COMPAT-1 records the
  live gap: PRAXIS-GROUP-08's group store schema 2 is not read by the pinned
  3.7.2 (expires 2026-10-21; retired by the next release's pin advance).
- `remote execute` refuses mutation of state it does not read with
  `incompatible-state`, before any effect (RemoteExecuteCliTests).

### PRX-QUAL-011 — Add adversarial pacing integration tests
Priority: critical
Depends on: PRX-QUAL-002, PRX-QUAL-003, PRX-QUAL-004, PRX-QUAL-006

Add tests specifically aimed at failure behavior rather than happy-path policy.

Required cases:
- corrupt, truncated, newer-version, and concurrently-written state.
- missing Codex primary/secondary windows.
- disappearing Claude global/scoped windows.
- stale readings across reset boundaries.
- hard hold survival across provider outage and process restart.
- weekly reset identity rollover.
- concurrent hook processes and lock contention.
- provider process timeout/EOF/malformed JSON.
- Keychain unavailable/expired credential/HTTP redirect/HTTP error.
- override status and latch preservation.
- unknown model/scope behavior.
- live hook contract fixtures for both providers.

## Praxis process hardening

Status (2026-10-06, PRAXIS-QUAL-04): every listed case is covered.
PacingSafetyTests holds the state, window, reset, hold, lock and override
cases; PacingHardeningTests adds Keychain unavailable/missing/expired/
malformed credentials, HTTP redirect/error/transport failure (injected
handler), provider process timeout/EOF/error reply/malformed JSON (scripted
protocol channel), unknown model/scope behaviour, and hook contract fixtures
for both providers (`tests/fixtures/pacing/hooks`, written to the runtimes'
documented hook input shapes, not captured live).

### PRX-QUAL-020 — Add engineering-risk metadata to work planning
Priority: high

Meaningful work SHALL declare, when applicable:
- change class and risk level;
- persistent-state impact;
- external protocol/provider impact;
- security/privacy impact;
- failure posture: fail-open, fail-closed, or indeterminate;
- expected architecture boundaries/tier ownership;
- required live/integration proof.

Praxis should derive completion obligations from this metadata.

### PRX-QUAL-021 — Add explicit design-debt capture to completion
Priority: high

A vertical slice may intentionally collapse architecture temporarily, but the
debt SHALL be named before the work can be called complete.

Acceptance:
- completion can declare no known design debt, or references to captured debt
  work items.
- untracked known debt is a completion failure for high-risk work.
- follow-up debt records rationale, risk, and the boundary that was compromised.
- prototypes cannot become canonical silently.

### PRX-QUAL-022 — Add verification matrices for high-risk work
Priority: high

For stateful control-plane, persistence, release/bootstrap, security, or remote
execution work, Praxis SHALL require evidence across:
- happy path;
- negative/failure path;
- corruption/partial state;
- concurrency where relevant;
- compatibility/version skew;
- recovery/rollback;
- representative live effect.

Green compilation/unit tests alone SHALL not satisfy completion.

### PRX-QUAL-023 — Integrate Dokimos quality evidence into Praxis completion
Priority: medium

Praxis SHALL be able to consume Dokimos change-quality results as evidence,
not duplicate Dokimos analysis.

Acceptance:
- completion may require a named Dokimos profile.
- introduced architecture violations or policy-blocking debt prevent completion.
- legacy debt can be baselined/ratcheted rather than forcing unrelated cleanup.
- unavailable Dokimos evidence is explicit, never treated as pass.

Status (2026-10-05, branch `quality/prx-qual-023-quality-evidence`,
`DF-ROS-2026-A052`): implemented as an opt-in completion gate.
- Praxis consumes `dokimos.ratchet` 1.0.0 reports (`--evidence
  dokimos-ratchet=PATH`) and `ordo.boundary-amplification/1` assessments
  (`--evidence ordo-boundary=PATH`); it never runs either tool and never
  recomputes their judgements.
- Policy: `workProtocol.qualityEvidence` (`dokimos`/`ordoBoundary`:
  `required|optional|off`, `dokimosBaseline`, `requiredFacets`, `workTypes`).
  A named Dokimos profile is the pinned accepted baseline (`dokimosBaseline`);
  the 1.0.0 contract carries no other profile identity.
- Regression and invalid exceptions block; legacy debt stays accepted through
  Dokimos's own baseline/ratchet and exceptions; unavailable, malformed and
  unsupported evidence is recorded and never a pass.
- Readiness facets (implementation, behaviour, architecture, release) are
  recorded as `praxis.completion-readiness/1` on the completion event and item.

Tracked debt (migration bridge): the policy is off by default so existing
repositories keep their behaviour. Retire it, defaulting new repositories to
`dokimos: required`, once Conditor installs Dokimos by default. Not yet done:
a digest of each consumed evidence file in the record, and a consumable
release-readiness contract (`release-ready` is always unavailable when
required). PRX-QUAL-020, 021 and 022 remain open; readiness facets are the
hook their obligations will feed.

## Tracking

Work items (captured 2026-10-06 from the requirements audit; groups and
analyses in `research/groups/`):

| Requirement | Work item | Group |
|---|---|---|
| PRX-QUAL-003 (creation evidence), PRX-QUAL-004 | PRAXIS-QUAL-01 | GROUP-PRAXIS-PACING-001 |
| PRX-QUAL-005 | PRAXIS-QUAL-02 | GROUP-PRAXIS-PACING-001 |
| PRX-QUAL-008 | PRAXIS-QUAL-03 | GROUP-PRAXIS-PACING-001 |
| PRX-QUAL-011 (remaining cases) | PRAXIS-QUAL-04 | GROUP-PRAXIS-PACING-001 |
| PRX-QUAL-009 | PRAXIS-QUAL-05 | - |
| PRX-QUAL-010 | PRAXIS-QUAL-06 | - |
| PRX-QUAL-020 | PRAXIS-QUAL-07 | GROUP-PRAXIS-COMPLETION-001 |
| PRX-QUAL-021 | PRAXIS-QUAL-08 | GROUP-PRAXIS-COMPLETION-001 |
| PRX-QUAL-022 | PRAXIS-QUAL-09 | GROUP-PRAXIS-COMPLETION-001 |
| PRX-QUAL-023 (digest, release-readiness) | PRAXIS-QUAL-10 | GROUP-PRAXIS-COMPLETION-001 |

## Sequencing

1. PRX-QUAL-007 can be fixed immediately.
2. PRX-QUAL-001, 002, 003, and 004 form the critical pacing hardening group.
3. PRX-QUAL-005, 006, and 011 complete the provider/gate reliability boundary.
4. PRX-QUAL-008 and 009 make pacing a first-class Praxis capability.
5. PRX-QUAL-010 resolves the temporary self-hosting equality fence.
6. PRX-QUAL-020 through 023 change Praxis so similar debt is visible before
   future work is declared complete.

## Principle

The objective is not to maximize abstraction or reject vertical slices.
Praxis should permit the smallest robust slice, but it must make knowingly
collapsed boundaries, unsafe failure defaults, and missing verification
explicit obligations rather than invisible shortcuts.
