# Group analysis: GROUP-PRAXIS-PACING-001

- Group: `GROUP-PRAXIS-PACING-001` (`./praxis work group show GROUP-PRAXIS-PACING-001`)
- Members: `PRAXIS-QUAL-01`, `PRAXIS-QUAL-02`, `PRAXIS-QUAL-03`, `PRAXIS-QUAL-04`
- Execution repository: kemiller2002/praxis
- Base commit: `f242aae664b4601e106b6e33ebce2b95bedfae35`
- Group record: first declared at 2026-10-06T20:35Z (commit `aaba4aa`); when
  `origin/main` (group store v2) was merged in, the branch's Praxis state was
  taken from main and the declaration was re-recorded with `work group
  create` (commit `2723128`), so the store's `createdAt` is the re-recorded
  time. History is append-only from that commit on.
- Machine-readable form: [`group-analysis.json`](group-analysis.json) (`praxis.group-analysis/1`)

## 1. Members

| Member | Obligation | Acceptance criteria | Depends on |
|---|---|---|---|
| PRAXIS-QUAL-01 | Per-window Stale/Unsupported pacing observation states and hard-hold creation evidence (PRX-QUAL-003, PRX-QUAL-004) | 1. WindowStatus has Observed, Missing, Invalid, Stale and Unsupported, each with a stable code in status JSON coverage. 2. A window carried from a cached reading (not in the current response) is Stale, never Observed; a window kind the adapter does not support (unknown Claude limit kind, unknown Codex slot) is Unsupported with a diagnostic, never silently dropped. 3. Stale and Unsupported expected windows make the reading incomplete (freshness stale), with response-shape drift reported as an explicit diagnostic. 4. Hard-limit holds persist creation evidence (observedAt of the triggering reading and the provider snapshot reference) in hold.json schema 3; schema 2 holds migrate with the evidence marked unrecorded, never invented. 5. Tests cover each state and the migration. | - |
| PRAXIS-QUAL-02 | Typed provider, model, quota-bucket and scope identities for pacing (PRX-QUAL-005) | 1. ProviderId, ModelId and QuotaBucket are typed values at the Application/Domain boundary; QuotaScope matching uses typed model-family identity with no String.Contains substring matching in policy. 2. Codex Spark bucket selection and Claude scope mapping are explicit, versioned adapter rules (one table each) with fixtures. 3. Unknown models have a declared conservative behaviour: every scoped window applies. 4. Adapter id, rule-set version and capabilities are observable in pacing status (text and JSON). 5. An architecture/guard test refuses Contains-based model matching in the pacing domain and application. | - |
| PRAXIS-QUAL-03 | Typed pacing telemetry events with stable codes replacing free-text pace.log (PRX-QUAL-008) | 1. The gate emits typed PacingEvent values with stable codes: hold-started, hold-retained, hold-released, hard-limit, provider-unavailable, override-enabled, override-disabled, state-fault. 2. Events carry provider, scope, reason kind, reset time and observedAt, never credentials, tokens or raw provider payloads. 3. Repeated polling with an unchanged hold emits no duplicate transition events (deduplicated by transition identity). 4. Events persist as praxis.pacing-event/1 JSON lines (events.jsonl) and pace.log remains as a human-readable rendering of the same events; readers (docs, status) are migrated. 5. Tests cover codes, redaction and deduplication. | PRAXIS-QUAL-01 |
| PRAXIS-QUAL-04 | Adversarial pacing adapter tests: Keychain, HTTP errors and redirects, provider process timeout/EOF, live hook fixtures (PRX-QUAL-011) | 1. Claude credential decoding is testable without a real Keychain: Keychain unavailable, missing entry, expired credential and malformed credential are explicit errors and the snapshot is unavailable, never fresh. 2. The Claude usage query is testable through an injected HTTP handler: a 3xx redirect is refused without following it, 4xx/5xx are explicit errors. 3. The provider process protocol is testable with a scripted stand-in: timeout, EOF before reply, error reply and malformed JSON are explicit errors. 4. Recorded live hook payload fixtures for Claude Code and Codex (PreToolUse and Stop events) drive the gate end to end and produce the provider's documented deny shape. 5. No test uses the live network or a real credential store. | PRAXIS-QUAL-01, PRAXIS-QUAL-02 |

## 2. Reuse inventory

| Existing element | Location | Reuse, extend, or not reused (why) |
|---|---|---|
| WindowStatus / WindowObservation | `src/Praxis.Application/Pacing/Contracts.fs:9` | extended: the one per-window observation vocabulary; Stale and Unsupported are added as cases so every projection keeps one source |
| PacingNormalization.coverage | `src/Praxis.Infrastructure/Pacing/Normalization.fs:27` | extended: already classifies expected keys as Observed/Invalid/Missing; unsupported limit kinds and cache-carried windows are classified here too |
| PacingAdapters.mergeMissingHard and observe | `src/Praxis.Infrastructure/Pacing/Runtime.fs:12` | extended: the place windows are carried from the cache; carried windows are marked Stale instead of being merged silently |
| HoldBasis.HardLimit and PacingStateDocument (schema 2) | `src/Praxis.Domain/Pacing/Model.fs:62` | extended: hard holds gain creation evidence; the document gains schema 3 with schema-2 migration in the existing parse function |
| QuotaScope.appliesTo (Contains matching) | `src/Praxis.Domain/Pacing/Model.fs:12` | not-reused: the substring heuristic is what PRX-QUAL-005 removes; replaced by typed model-family matching |
| codex Spark bucket selection by substring | `src/Praxis.Infrastructure/Pacing/Runtime.fs:95` | not-reused: moved into one versioned adapter rule table |
| PacingEventSink { Write: string -> unit } and writeLog (pace.log) | `src/Praxis.Application/Pacing/Contracts.fs:118` | extended: the sink becomes typed; writeLog stays as the human rendering of the typed events |
| PacingProviders.processCapture, queryCodex, queryClaude | `src/Praxis.Infrastructure/Pacing/Providers.fs:13` | extended: the existing adapters gain injectable seams (process runner, HTTP handler, credential reader) for adversarial tests; no second adapter is written |
| PacingStatusProjection / PacingStatus.renderJson | `src/Praxis.Application/Pacing/Status.fs:7` | extended: the one typed status model; adapter id/version/capabilities and new coverage codes project through it |
| PacingSafetyTests helpers (window, snapshotOf, fakeRuntime) | `tests/Praxis.Tests/PacingSafetyTests.fs:16` | reused: deterministic runtime and fixtures for the new adversarial cases |
| Work event log (.ros/events/events.jsonl, FileEventLogRepository) | `src/Praxis.Infrastructure/Work/FileEventLogRepository.fs:1` | not-reused: it is repository-scoped work-transition history; pacing events are per-user state under the pacing directory and must not write repository state from a hook |
| Execution telemetry metrics (Domain/Telemetry) | `src/Praxis.Domain/Telemetry/Usage.fs:1` | not-reused: metric observations of an execution, not state-transition events; pacing transitions are recorded as their own typed event contract |

Searches that established the inventory:

- `grep -rn "Contains(" src/Praxis.Domain/Pacing src/Praxis.Application/Pacing src/Praxis.Infrastructure/Pacing`: QuotaScope.appliesTo and the Spark bucket rule are the only substring model matches
- `grep -rn "Events.Write\|writeLog" src`: PacingOperations.gate and PacingAdapters.observe are the only writers of pace.log
- `grep -rn "WindowStatus" src tests`: Contracts.fs, Normalization.fs, Status.fs, Runtime.fs and PacingSafetyTests are the only users

New abstractions:

- **ProviderId / ModelId / QuotaBucket typed identities**: considered string Provider fields and QuotaScope.Model name; strings allowed substring heuristics; PRX-QUAL-005 requires typed values at the boundary.
- **PacingEvent (typed telemetry event)**: considered PacingEventSink string writes and the work event log; free text has no stable codes or dedupe identity; the work log is repository state.
- **PacingAdapterRules (versioned mapping table)**: considered inline match in PacingAdapters.observe and normalizeClaudeUsage; inline rules are not observable or versioned; the table is the same rules made explicit.

## 3. Group-level design

- Common architecture and shared invariants: Domain/Pacing holds policy; Application/Pacing holds ports and orchestration; Infrastructure/Pacing holds provider, persistence and log adapters (PRX-QUAL-001 tiers, enforced by ArchitectureTests). Invariant: uncertainty, corruption or an unsupported shape never becomes permission.
- Conflicting requirements: PRX-QUAL-008 asks for typed telemetry while keeping human diagnostics; resolved by one typed event stream with `pace.log` as its rendering.
- One design serving several members: typed identities (PRAXIS-QUAL-02) are used by the Stale/Unsupported coverage (01), the event payloads (03) and the adapter fixtures (04).
- Compatibility and migration: `hold.json` schema 3 adds hard-hold creation evidence; schema 2 migrates with evidence `unrecorded`. Status JSON stays schema 1 with additive fields. CLI is unchanged.
- Common tests: PacingSafetyTests (domain, observation, document, store, gate) plus new adapter fixture tests.
- Risks of solving each member independently: four different string vocabularies for the same window/provider identity.
- Shared group infrastructure: the typed identities are attributed to PRAXIS-QUAL-02.

Decisions:

- Pacing events are written next to pace.log in the pacing state directory, not into repository .ros state, because the gate runs from agent hooks across repositories
- Unknown models stay conservative: every scoped window of the provider applies

## 4. Order

- PRAXIS-QUAL-01 before PRAXIS-QUAL-03 (merged)
- PRAXIS-QUAL-02 before PRAXIS-QUAL-04 (merged)

A group checkpoint is recorded after each member's pull request merges.

## 5. Verification pass (before completing any member)

Recorded per member before its completion.
