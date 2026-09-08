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
| MIG-02 architecture-enforced F# skeleton | ready | five production projects; version/help; dependency rules with positive and rejection proof | remove shadow projects to roll back |
| MIG-03 artifact boundary/fixtures | ready | explicit front matter, IDs, kind/status/confidence/reference shapes; path-specific findings; golden fixtures | Node/Python remain oracles |
| MIG-04 artifact validation and registry projection | ready | `ros-fs artifacts validate`, `registry build/check`; byte parity; no canonical mutation; repeatability | no launcher switch; Python deprecation only after evidence |
| MIG-05 transactional file persistence/recovery | deferred | declared write sets, version checks, lock order, crash recovery, retry-safe outcomes | retain current file stores and Node writer |
| MIG-06 unified Git provenance | deferred | typed clean/changed/unavailable result, rename/copy correctness, failure tests | current Git calls retained until all consumers move |
| MIG-07 work lifecycle/evidence | deferred | typed backlog/live states, legal transitions, evidence guards, comparative state/files/events | shadow then explicit authority switch |
| MIG-08 execution/telemetry core | deferred | lifecycle, identity, provenance, metric/capability semantics, aggregation, unknown/raw preservation | provider adapters remain at edge |
| MIG-09 bootstrap/upgrade | deferred | valid initial state, versioned upgrade/check/rollback; npm materializer thin | retain npm acquisition |
| MIG-10 validation/release workflow thinning | deferred | typed validation/release plans with explicit unknown external outcomes | YAML keeps GitHub/npm effects |
| MIG-11 HTTP/UI hardening and contracts | deferred | loopback/auth policy, sanitized uploads, idempotency, checked capability data | retain Node/TypeScript where useful |
| MIG-12 project administration research | deferred | identity/authority/reconciliation/portability contract | separate bounded context |
| MIG-13 Time Entry research | blocked | allocation/approval/correction/rounding/audit authority required | no implementation without requirements |
| MIG-14 legacy retirement | deferred | all nine removal conditions documented per path | deprecate before delete when external use unknown |

## Next ordered work after this mission

1. Finish MIG-05 file transaction/recovery semantics before moving stateful
   writers.
2. Unify Git provenance and prove unavailable is not clean.
3. Migrate work lifecycle as a controlled shadow slice.
4. Migrate stable execution/telemetry semantics while preserving open provider
   extensions.
5. Run the full consumer distribution experiment before changing bootstrap.
6. Address hub security/locking as separate defects even if its F# migration is
   deferred.
