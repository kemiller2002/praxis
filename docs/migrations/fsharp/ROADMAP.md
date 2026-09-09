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
| MIG-07 work lifecycle/evidence | in progress | typed live/backlog decisions, pure item/context/promotion plans, batch evidence composition that runs only after complete semantic acceptance, telemetry execution-ID result-feedback that freezes the item/event projection whenever no new execution record is required, real Git observed/meaningful-path composition (config-driven glob filter plus optional `ROS_BASE_REF` committed-range diff) for `work context-plan`, and a read-only `work validate` diagnostic mirroring production `workFindings` (enforcement gate, baseline/attribution/active-work exclusions, and the synthetic `.git` finding on Git failure); exhaustive guards, legal-edge projections, evidence comparisons, ordered multi-item behavior, all 16 backlog state/action differentials, begin/resume/finalize telemetry differentials, Git baseline/meaningful-path/base-ref differentials, and work-attribution differentials pass; persistence ports exist | new-execution creation effect, state-changing effect execution, evidence-containment policy, and production/distribution switch remain; Node stays state-changing authority |
| MIG-08 execution/telemetry core | deferred | lifecycle, identity, provenance, metric/capability semantics, aggregation, unknown/raw preservation; `EV-ROS-2026-A044` inventories the full scope and corrects an earlier estimate that treated new-execution creation as separable from it | provider adapters remain at edge; needs its own architecture challenge and first-slice choice before treatment, per `DF-ROS-2026-A027`'s own precedent |
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
   the real effect. This absorbs the remainder of MIG-07 and all of MIG-08,
   plus new modeling for backlog capture/update/attach and telemetry
   producer commands the roadmap has not yet named as their own slice.
2. **Consumer distribution evidence** — the macOS/Linux/Windows
   install/startup/size/update/offline/integrity/rollback evidence
   `DF-ROS-2026-A027` named as blocking, for a specific chosen distribution
   shape.
3. **The switch decision itself** — only once 1 and 2 are both accepted,
   carrying that evidence plus an explicit rollback plan.

`./ros` continues to invoke Node exclusively until Phase 3 is accepted.
