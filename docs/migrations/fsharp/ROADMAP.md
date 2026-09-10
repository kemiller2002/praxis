# ROS F# migration roadmap

Status values are `complete`, `in progress`, `ready`, `deferred`, and
`blocked`. Completion is evidence-based; adding a project or compiling does not
complete a capability.

## Prioritization rubric

Candidates are scored 1–5 for semantic importance (S), duplication (D), defect
or risk pressure (R), frequency/fan-out (F), testability (T), architectural
leverage for later slices (L), and uncertainty penalty (U). The aid is
`S + D + R + F + T + L - U`; it is ordering evidence, not scientific precision.

| Candidate | S | D | R | F | T | L | U | Aid | Interpretation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| Artifact validation/registries | 4 | 5 | 3 | 4 | 5 | 5 | 1 | 25 | safest end-to-end architecture proof |
| Shared persistence/recovery | 5 | 4 | 5 | 5 | 3 | 5 | 3 | highest systemic safety leverage after proof |
| Git provenance | 4 | 4 | 4 | 5 | 4 | 4 | 2 | removes duplicated fail-open boundary |
| Work lifecycle/evidence | 5 | 4 | 5 | 5 | 4 | 5 | 3 | central state; requires persistence/Git seams |
| Execution/telemetry core | 5 | 4 | 4 | 5 | 4 | 5 | 4 | high value, newer and extension-heavy |
| Bootstrap/upgrade | 4 | 3 | 4 | 3 | 4 | 4 | 3 | controls installed state and distribution |
| Workflow/release policy | 4 | 3 | 4 | 3 | 3 | 3 | 4 | hosted external outcomes limit local evidence |
| HTTP/UI contracts | 2 | 2 | 4 | 3 | 3 | 2 | 3 | secure/contract, not wholesale rewrite |
| Project administration | 3 | 2 | 4 | 2 | 3 | 2 | 5 | separate authority requirements unresolved |
| Time-entry projection | 2 | 1 | 2 | 1 | 1 | 1 | 5 | no current semantic contract; research only |

Dependency ordering overrides a raw score where state safety requires it.

## Vertical slices

| Slice | Status | Capability and acceptance | Compatibility/removal plan |
|---|---|---|---|
| MIG-00 baseline, inventory, preregistration, navigation | complete | immutable tag; T0–T2; complete inventory; semantic map/manifests; frozen hypotheses/fixtures | additive only |
| MIG-01 bounded distribution decision | complete | repository-local framework-dependent .NET 10 shadow; production consumer choice explicitly deferred | Node/npm stays authoritative |
| MIG-02 architecture-enforced F# skeleton | complete | five production projects; version/help; dependency rules with positive and rejection proof | remove shadow projects to roll back |
| MIG-03 artifact boundary/fixtures | complete | explicit front matter, IDs, kind/status/confidence/reference shapes; path-specific findings; frozen valid/invalid fixtures | Node/Python remain oracles |
| MIG-04 artifact validation and registry projection | complete | `ros-fs artifacts validate`, `registry build/check`; Node/F# byte parity; no canonical mutation; repeated build and partial-write outcome proof | no launcher switch; Python deprecation only after further evidence |
| MIG-05 transactional file persistence/recovery | complete | separate bounded recovery contracts cover generated registries, live event/context, and backlog queue/projection; telemetry execution records remain atomic/per-execution locked while creation/backlink retry adopts detached evidence or rejects ambiguity; cross-runtime, divergence, concurrency, and rejection proofs pass | retain stateful Node writers; each semantic store keeps a bounded contract; no global transaction framework |
| MIG-06 unified Git provenance | complete | F# owns the typed clean/changed/unavailable contract; one compatible installed Node process adapter serves work and telemetry; rename attribution, malformed/missing/non-repository outcomes, pre-effect work rejection, and unavailable-not-zero telemetry pass | retain compatible Node adapter until the MIG-01 distribution decision authorizes an F# runtime switch |
| MIG-07 work lifecycle/evidence | complete | typed live/backlog decisions, pure item/context/promotion plans, batch evidence composition that runs only after complete semantic acceptance, telemetry execution-ID result-feedback that freezes the item/event projection whenever no new execution record is required, real Git observed/meaningful-path composition for `work context-plan`, read-only `work validate`/`work backlog-validate` diagnostics mirroring production `workFindings`/`queueFindings`, and — Phase A (`DF-ROS-2026-A028`) — eight real state-changing effects: `work backlog-transition` (`ready`/`block`/`abandon`), `work capture` (`add`/`captureWork`), `work update` (`update`/`findOrCreateQueueEntry`, including its context-only upsert), `work attach` (`attachFileUnlocked`, including real binary file writes and production's exact filename sanitization/sequencing) — giving every backlog-only Node command genuine F# parity — `work start` (`startWork`/`transitionUnlocked`'s `begin` action, including real telemetry execution creation via `startExecution`), the first live-work and telemetry-creating effect, `work resume` (`transitionUnlocked`'s `resume` action, reusing `work start`'s effect infrastructure verbatim), `work block` (`blockWork`, splitting requested ids between a backlog and a live-context branch under one held lock, matching production's own combined command exactly), and `work complete` (`transitionUnlocked`'s `complete` action, including real evidence-path verification via `WorkOperations.planVerifiedContext`, a real `git diff`-derived clean-baseline change summary, and unconditional telemetry-execution finalization) — giving every live-work transition genuine F# parity. The four backlog effects commit through the shared `work-protocol` lock and `backlog-state` recovery journal (MIG-05) via `JsonNode` surgery (preserving every unmodeled field, matching production's non-HTML-safe JSON escaping) rather than a full typed queue-item model; `work start`/`work resume`/`work block`/`work complete` commit through `work-state` (events + context) via the same JSON-node-surgery discipline, create telemetry executions through a new content-addressed identity/capability/measurement model, and (for `complete`) finalize them through a new `FileTelemetryFinalizationRepository`; see `ARCHITECTURE.md` for the per-slice detail. Exhaustive guards, legal-edge projections, ordered multi-item behavior, all 16 backlog state/action differentials, begin/resume/finalize telemetry differentials, Git differentials, work-attribution/backlog-queue-validation differentials, and effect differentials for all eight real effects pass; persistence ports exist | evidence-containment policy and the production/distribution switch remain, both explicitly deferred beyond MIG-07's own scope |
| MIG-08 execution/telemetry core | in progress | lifecycle, identity, provenance, metric/capability semantics, aggregation, unknown/raw preservation; `EV-ROS-2026-A044` inventories the full scope and corrects an earlier estimate that treated new-execution creation as separable from it. Increment 1 (Phase A/MIG-08's first slice, chosen per `EV-ROS-2026-A044`'s own "almost certainly its own smaller first vertical slice" guidance rather than any recommendation it declined to make) ships the two commands with zero new write-path/lock/identity/adapter complexity: `telemetry adapters` (a hardcoded catalog dump, `Ros.Domain.Telemetry.TelemetryAdapters`) and `telemetry show [TARGET]` (a real, lock-free read of `.ros/telemetry/executions/*.json` through a new `Ros.Infrastructure.Work.FileTelemetryQueryRepository`, with `TARGET` positional rather than a `--` flag, matching production's own `telemetryTarget(args)` exactly); both proven by a real Node differential against production's own exported `showTelemetry`/`TELEMETRY_ADAPTERS`. Increment 2 closes a real gap left open since increments 6-8: `recordTelemetryLifecycle` (`Ros.Infrastructure.Work.FileTelemetryFinalizationRepository.recordLifecycle`) now appends the real `work.blocked`/`work.resumed` events plus their `agent.interruptions`/`agent.resumes` metrics to every currently-active execution, called from `work resume`/`work block` before telemetry resolution runs (matching production's own ordering), so `time.blocked_ms` computes a real nonzero value at finalization instead of always zero | `telemetry summary`'s aggregation, every write-path telemetry-producer command (`start`/`ingest`/`classify`/`record`/`finalize`), and both `adapter call`/`adapter publish` commands remain, each its own future scoping choice; `parentExecutionId` linkage on `resume`'s new-execution path remains open too; provider adapters (payload-shape mapping) remain at edge throughout |
| MIG-09 bootstrap/upgrade | deferred | valid initial state, versioned upgrade/check/rollback; npm materializer thin | retain npm acquisition |
| MIG-10 validation/release workflow thinning | deferred | typed validation/release plans with explicit unknown external outcomes | YAML keeps GitHub/npm effects |
| MIG-11 HTTP/UI hardening and contracts | deferred | loopback/auth policy, sanitized uploads, idempotency, checked capability data | retain Node/TypeScript where useful |
| MIG-12 project administration research | deferred | identity/authority/reconciliation/portability contract | separate bounded context |
| MIG-13 Time Entry research | blocked | allocation/approval/correction/rounding/audit authority required | no implementation without requirements |
| MIG-14 legacy retirement | deferred | all nine removal conditions documented per path | deprecate before delete when external use unknown |

## Next ordered work after this mission

1. Migrate work lifecycle orchestration as a controlled shadow slice now that
   its persistence preconditions exist.
2. Migrate stable execution/telemetry semantics while preserving open provider
   extensions.
3. Run the full consumer distribution experiment before changing bootstrap.
4. Address hub security/locking as separate defects even if its F# migration is
   deferred.

## Authority-switch decision track

`DF-ROS-2026-A028` opens the decision this roadmap's "Production command
switch" row has always deferred, as three independently-gated phases:

1. **Full command-surface effect parity** — real, persisted F# handlers
   (not shadow diagnostics) for every Node command `EV-ROS-2026-A043`
   inventoried as having no F# equivalent, verified by differential proof of
   the real effect. This absorbs the remainder of MIG-07 and all of MIG-08.
2. **Consumer distribution evidence** — the macOS/Linux/Windows
   install/startup/size/update/offline/integrity/rollback evidence
   `DF-ROS-2026-A027` named as blocking, for a specific chosen distribution
   shape.
3. **The switch decision itself** — only once 1 and 2 are both accepted,
   carrying that evidence plus an explicit rollback plan.

Phase 1's backlog-layer scope is complete: `work backlog-transition`
(`ready`/`block`/`abandon`), `work capture` (`add`), `work update` (`update`,
including its context-only-id upsert), and `work attach`
(`attachFileUnlocked`, including real binary file writes and production's
exact filename sanitization/sequencing) are all real, differential-proven
effects — every backlog-only Node command now has genuine F# parity. Phase
1's live-work layer is now complete too: `work start`
(`startWork`/`transitionUnlocked`'s `begin` action, including real
telemetry execution creation via `startExecution`), `work resume`
(`transitionUnlocked`'s `resume` action, reusing `work start`'s effect
infrastructure verbatim), `work block` (`blockWork`, the one command
that splits requested ids between the backlog and live-context layers
under one held lock, matching production's own combined command exactly),
and `work complete` (`transitionUnlocked`'s `complete` action, including
real evidence-path verification against the filesystem, a real
`git diff`-derived clean-baseline change summary, and unconditional
telemetry-execution finalization via a new
`FileTelemetryFinalizationRepository` port of `finalizeWorkExecutions`) are
all real effects — every live-work transition (`begin`/`resume`/`block`/
`complete`) now has genuine F# parity, closing the entire work-lifecycle
command surface. Phase 1 has now opened its MIG-08 scope too:
`EV-ROS-2026-A044` (a full inventory of `tools/ros_telemetry.mjs`)
deliberately declined to recommend which slice to attempt first, only that
one was needed, so this round made that choice — `telemetry adapters` (a
hardcoded catalog dump) and `telemetry show` (a real, lock-free read of
`.ros/telemetry/executions/*.json`), the two commands with zero new
write-path, lock, identity, or adapter complexity. A follow-on round closed
a real gap that increments 6-8 had documented, honestly, as still open:
`recordTelemetryLifecycle`'s within-execution "blocked"/"resumed" event
bookkeeping, so `work resume`/`work block` now record it before telemetry
resolution runs (matching production's own ordering) and `time.blocked_ms`
computes a real nonzero value at finalization instead of always zero.
Everything still missing from Phase 1 (`telemetry summary`'s aggregation,
every write-path telemetry-producer command, both adapter commands, and
`resume`'s still-open `parentExecutionId` linkage) remains its own future
MIG-08 scoping choice. `EV-ROS-2026-A043` has now been re-run as
`EV-ROS-2026-A045`: thirteen of the same twenty-six rows now read "full
parity" (up from three), but the acceptance criterion is still every row
reading "full parity," so Phase 1 remains open until a further re-run
closes the remaining thirteen.

`./ros` continues to invoke Node exclusively until Phase 3 is accepted.
