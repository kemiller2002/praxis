---
id: JR-ROS-2026-A019
title: ROS F# application migration execution journal
status: active
version: 1.14.0
research_area: repository-operating-system
author_agent: openai-codex
created: 2026-09-07
updated: 2026-09-09
related_mission: WI-0011
related_package: RP-ROS-2026-A029
evidence_ids:
  - EV-ROS-2026-A015
  - EV-ROS-2026-A018
  - EV-ROS-2026-A030
  - EV-ROS-2026-A031
  - EV-ROS-2026-A032
  - EV-ROS-2026-A033
  - EV-ROS-2026-A034
  - EV-ROS-2026-A035
  - EV-ROS-2026-A036
  - EV-ROS-2026-A037
  - EV-ROS-2026-A038
  - EV-ROS-2026-A039
  - EV-ROS-2026-A040
  - EV-ROS-2026-A041
  - EV-ROS-2026-A042
hypothesis_ids:
  - HY-ROS-2026-A021
  - HY-ROS-2026-A022
  - HY-ROS-2026-A023
  - HY-ROS-2026-A024
  - HY-ROS-2026-A025
  - HY-ROS-2026-A026
theory_ids: []
tags: [fsharp, migration, execution-journal, defects, sde]
---

# Objective

Execute as much of the accepted staged F# migration as can be completed safely,
without moving production authority before characterization, comparative
verification, and explicit evidence.

# Starting state

- repository `repository-operating-system`, clean `main` at
  `6a188073474f5088decf9617f4539625e4bdb451`;
- immutable and pushed tag
  `ros-fsharp-migration-baseline-20260907-202926`;
- branch `migration/ros-fsharp-application`;
- active WI-0011 and execution
  `EXE-20260907T203141590Z-54f547f8`;
- baseline 84 Node + 7 Python tests and two TypeScript builds passed;
- Node/npm remained production and installed authority.

# Checkpoint journal

## T0 — experiment start — 2026-09-07T20:31:41.590Z

Confirmed identity, clean state, branch/SHA/remotes, runtime versions, and
baseline gates. Created/pushed the immutable baseline tag and created the
migration branch. Began and classified WI-0011; recorded factual R&D context and
available execution metrics.

## T1 — instrumentation/bootstrap complete — 2026-09-08T00:08:50Z

Read all installed SDE architecture/method/reference/template authorities and
the applicable ROS governance, decisions, telemetry, context, prior evidence,
journal, and REP A017. Inspected telemetry capability state and
preserved tokens/model/cost/context as unavailable or unknown. Mechanical
source/caller/config/workflow scans refreshed the older inventory.

## T2 — semantic foundation established — 2026-09-08T00:19:38Z

Froze `EV-ROS-2026-A018`, `SDE-MAP.md`, six feature manifests, this journal,
experiment A020, hypotheses A021–A026, decision A027, and migration architecture,
roadmap, traceability, status, and telemetry documents. No F# or other
implementation source had been added. The pre-treatment architecture challenge
narrowed distribution and contract scope.

## T3 — first vertical slice complete — 2026-09-08T06:14:34Z

Completed MIG-03/MIG-04 as a repository-local F# shadow. The artifact feature
now has typed domain policy/projection, application use cases with explicit
dependency outcomes, filesystem/front-matter infrastructure, explicit JSON
renderers, CLI mapping, and tests. Node remained the source of production
authority; there was no dual-write or launcher switch.

## T4 — implementation complete — 2026-09-08T06:18:00Z

Committed `30fcc6e` for the capability slice. Root validation and publication
workflows now provision .NET and run the additive `test:all` gate; starter
workflows remain Node-only platform declarations. Documentation and
traceability identify scoped F# commands and retained adapters.

## T5 — verification and self-review complete — 2026-09-08T06:23:23Z

The unrestricted complete suite passed: 87 Node, 7 Python, 8 F#, and 3
Node-driven differential/smoke tests, plus both TypeScript builds. The F# build
reported zero warnings/errors. The self-review rechecked the scope against the
Node artifact implementation, output bytes, failure outcomes, adapter thinness,
and authority boundary. It is expressly self-review: the three attempted
parallel reviewers had exhausted their quotas before a final separate review.

## T6 — execution finalization — 2026-09-08T06:29:33.997Z

Completed WI-0011 with implementation and test evidence. ROS finalized
`EXE-20260907T203141590Z-54f547f8`, recording 35,872,407 ms calendar/wall span,
four commits in its captured range, 78 added and five modified files, 4,624
added and four deleted lines, and zero ending dirty files at finalization. The
test aggregate retains the earlier listener-restricted 14 failures alongside
the unrestricted successful final run; no provider token/cost fields were
invented.

# Discovery and defect journal

| ID | Checkpoint | Symptom/detection | Root cause/class | Legacy or introduced | Repair/verification/prevention |
|---|---|---|---|---|---|
| MIG-D001 | T0 | first annotated-tag command could not create `.git/TAG_EDITMSG`; tool runtime error | sandbox/environment boundary | execution environment | retried exact tag with approved Git capability; resolved tag/SHA and pushed; use explicit escalation when `.git` writes are denied |
| MIG-D002 | T1 | no project `SDE-MAP.md` or feature `manifest.md`; all-file/navigation scan | architecture/navigation defect and undeclared SDE dependency | legacy | add compact map/manifests at T2; future changes must update them and architecture checks should detect stale paths |
| MIG-D003 | T1 | A017 claimed bootstrap invoked validation and described wrong workflow/manifests/SDE layout; source comparison | documentation drift | legacy research record | A018 records seven exact corrections without rewriting historical evidence; use executable/source-derived inventory checks |
| MIG-D004 | T1 | `package.json` version 1.2.1 versus both lockfile root fields 1.1.1; JSON comparison | representation/distribution drift | legacy | do not mix unrelated repair into pre-treatment; record and repair in an explicit packaging slice or bounded mission change, then test pack metadata |
| MIG-D005 | T1 | schema excludes `medium-high`, runtimes/tests accept it; schema/code/test comparison | boundary contract drift | known legacy | preserve behavior in fixtures and record explicit future contract decision; add schema conformance gate before authority switch |
| MIG-D006 | T1 | dead layout helpers; `write_file` would label a new post-write file overwrite; call/reference inspection | obsolete code plus latent representation defect | legacy, unreachable | deprecate generator; do not repair dead unverified behavior during artifact slice; remove only after usage window |
| MIG-D007 | T1 | all three parallel reviewers exhausted their separate execution quota after partial work; orchestrator status | environment/agent-execution limitation | mission environment | independently verify returned claims in source; label final review accurately and retry a fresh independent reviewer only if available |
| MIG-D008 | T2 | decision draft contained two malformed generated word fragments; immediate readback | agent execution mistake | introduced | repaired with `apply_patch`, searched for fragments, and require readback/validation after large patches |
| MIG-D009 | post-T2 | concurrent initial `dotnet` build/run plus sandboxed restore produced silent stalls; process/build output and missing assets | environment/toolchain execution issue | mission environment | approved one explicit restore, shut down build servers, then built sequentially with shared compilation/build parallelism disabled; document deterministic npm command |
| MIG-D010 | post-T2 | architecture rejection test expected one finding but correctly received both graph mismatch and outward-Domain findings | introduced verification expectation defect | introduced | changed assertion to require both findings; rebuilt and reran 2/2 tests; guard rejection remained effective |
| MIG-D011 | T3 | initial F# test helper had invalid `and` type/module syntax; compiler diagnostic; later Node differential used strict-mode reserved parameter and compared enriched JSON against stripped Node findings | introduced test representation defects | introduced | replaced with a recursive helper; renamed the parameter; compared preregistered semantic fields. Final 8 F# + 3 differential tests pass; require compile/run after test generation |
| MIG-D012 | T5 | sandboxed complete suite produced 14 `EPERM` loopback-listener failures in HTTP tests | environment/platform issue | mission environment | reran exact suite with approved local listener capability; all 87 Node tests passed. Preserve failed run as environment evidence rather than suppressing it |
| MIG-D013 | T5 | `./ros validate` rejected telemetry provenance source type `agent-journal` after metric recording | introduced boundary/provenance defect | introduced | corrected it to registered `agent-report`; rerun validation proves the source-vocabulary guardrail fires rather than silently accepting a new label |
| MIG-D014 | MIG-06 | first three Git-slice builds rejected an F# 10 reserved local name, unconstrained recursive parser types, a misplaced match branch, and interpolation syntax | introduced representation/agent-execution defects | introduced | renamed/bounded the locals, added explicit parser types, restored exhaustive branch locality, and simplified interpolation; subsequent build has zero warnings/errors and all 21 tests pass |
| MIG-D015 | MIG-06 | the current-repository artifact smoke test regenerated stale registries before asserting no change | verification side-effect defect | legacy from MIG-04 | replaced the builder call with read-only `./ros registry check`; regenerated the expected projections once through the governed workflow; rerun must prove the check has no hidden write |
| MIG-D016 | MIG-07 | initial F# block guard rejected whitespace-only reasons while Node accepts them | characterization mismatch found by adversarial source comparison | introduced | changed absence semantics from whitespace to empty; added differential coverage for both empty and whitespace values; policy tightening requires a separate intentional decision |
| MIG-D017 | MIG-05 work persistence | first recovery builds exposed unconstrained .NET overloads, a record-label collision between artifact/work failures, and filesystem exceptions outside typed outcomes | introduced boundary/representation defects | introduced | constrained boundary types, annotated artifact fixtures, wrapped prepare/recover reads, and added corrupt-journal rejection; final narrow build has zero warnings/errors and 30 tests pass |
| MIG-D018 | MIG-05 production integration | direct typed-test command selected the default Debug output after only Release had been built | agent execution mistake | introduced | reran with explicit `--configuration Release --no-build`; 31/31 typed tests passed; pin configuration in direct verification commands |
| MIG-D019 | MIG-05 backlog persistence | adding a nominally distinct backlog write record made the existing unannotated F# work-write test helper infer the new type | introduced representation/test defect | introduced | annotated both helper return types explicitly; next build succeeded with zero warnings/errors and all 34 typed tests passed |
| MIG-D020 | MIG-05 backlog verification | first complete gate's read-only F# repository smoke rejected stale evidence/journal registries after new canonical records were added | verification-order finding; generated projection stale | introduced by unbuilt canonical evidence changes | ran the configured registry build, confirmed current projections, and reran the unchanged complete gate; all 147 tests passed |
| MIG-D021 | MIG-05 telemetry-link verification | the no-uncomposed-write guard passed, then its test helper failed while listing an execution directory that correctly did not exist | introduced test expectation defect | introduced | made the helper model absent storage as an empty file set; rerun passes all 28 telemetry tests |
| MIG-D022 | MIG-07 evidence self-review | evidence path normalization occurred before the filesystem adapter's exception boundary, so a malformed path could escape instead of returning `unavailable` | introduced boundary error | introduced | moved normalization inside the typed boundary; added a malformed-path rejection assertion; final full gate passes |
| MIG-D023 | MIG-07 evidence verification | the new malformed-path assertion used a nonexistent assertion helper and stopped the first full F# build | introduced test/agent-execution mistake | introduced | changed it to the repository's established `Assert.isTrue` helper and reran the complete gate; all 172 tests pass |
| MIG-D024 | MIG-07 context archaeology | production multi-item transition delays context/event persistence but executes telemetry inside the item loop, so a later item rejection can leave earlier detached telemetry | legacy boundary/transaction issue | legacy | F# context planner validates the complete ordered plan before returning any effects; keep Node authority and use existing single-detached-record recovery until effect execution is migrated |
| MIG-D025 | MIG-07 context implementation | first two builds found ambiguous regex and System.Text.Json overloads | introduced representation issue | introduced | annotated string and writer boundaries explicitly; subsequent build and focused typed/differential gates pass |
| MIG-D026 | MIG-07 backlog differential | first backlog effect shape implied abandonment cleared a prior block reason, while production preserves it | introduced semantic/representation error | introduced | replaced optional final values with explicit keep/clear/set field operations; reran all 16 state/action comparisons successfully |
| MIG-D027 | MIG-07 backlog implementation | three builds found nominal request/plan and CLI/JSON overload ambiguity | introduced representation issue | introduced | added explicit request, string, and writer annotations; focused build is warning-free and typed/differential gates pass |

# Observations

- Six actual semantic areas were evidenced: artifacts, work, execution
  telemetry, bootstrap/distribution, project administration, and
  automation/release.
- Physical scripts cross those areas. The target cannot be a file-for-file port.
- Artifact validation is the safest architecture proof because its writes are
  disposable projections and three current rule accounts provide comparison.
- The largest near-term safety issue is inconsistent file/Git effect discipline,
  not Node versus F# by itself.
- A .NET SDK on one machine supports a shadow build but cannot settle consumer
  distribution.

# Post-T2 implementation journal

## MIG-02 — architecture-enforced skeleton

Added `Ros.Domain`, `Ros.Contracts`, `Ros.Application`, `Ros.Infrastructure`,
`Ros.Cli`, and `Ros.Tests` targeting .NET 10. The CLI exposes only
version/help. The architecture verifier reads the real project graph and also
checks a deliberately invalid Domain -> Infrastructure graph. After the one
test-expectation repair, the solution built with zero warnings/errors and both
architecture tests passed. No production launcher, package payload, starter
profile, workflow, or ROS state behavior changed.

## MIG-03/MIG-04 — artifact shadow slice

Implemented feature-local `Artifacts` modules rather than a line-for-line
translation. `ArtifactValue` keeps scalar/list/map data explicit;
`ArtifactPolicy` holds ID, filename, status, confidence, duplicate, reference,
and reciprocal rules; `ArtifactOperations` distinguishes rejected data,
external failure, and indeterminate partial write. The filesystem adapter makes
each registry replacement atomic but returns an explicit incomplete result
across a multi-file write. Explicit JSON writers, frozen fixtures, and a
Node-driven differential test preserve observed registry output.

## MIG-05 — bounded artifact persistence

Node and F# registry projection now share one SHA-256 lease resource and a
versioned, path-restricted replay record for the eight generated registries.
Recovery and ownership-change tests prove explicit failed versus indeterminate
outcomes. This does not generalize to work or telemetry state.

## MIG-06 — typed Git provenance shadow

Added a feature-local Git model with disjoint clean, changed, and unavailable
outcomes. The infrastructure parser preserves separate index/work-tree deltas
and both paths for rename/copy records from porcelain-v1 `-z`; malformed output
becomes unavailable instead of an empty list. The explicit JSON contract and
CLI expose the observation without changing `./ros`. Unit, real-repository,
missing-tool, non-repository, and Node/Git differential checks pass. The two
production Node helpers remain fail-open, so the overall slice is in progress
until work and telemetry callers migrate with their own compatibility proofs.

## MIG-07 — typed live-work decision shadow

Added a pure closed model for the four current live-work states, four actions,
five legal edges, block-reason obligation, and configured evidence-type
obligation. An exhaustive 16-pair differential invokes the real Node transition
guard and the F# diagnostic command. The first adversarial pass caught and
removed an accidental whitespace-policy change. No context, event, telemetry,
Git, clock, or file writer moved; those effects remain blocked on persistence
and caller-specific comparison.

## MIG-05 — hash-preconditioned work-state recovery shadow

Added a separate `work-state` journal rather than reusing the artifact replay
format. It declares exactly event then context, records before/after content
hashes, preflights both targets, replays interrupted or already-applied writes,
and refuses divergence, malformed data, incomplete/reordered sets, or a second
pending journal. No production writer is connected yet; work-lock capability
and Node/F# caller integration remain required.

## MIG-05 — production work-state recovery integration

The production Node transition kernel now uses the same ordered, versioned,
hash-preconditioned event/context journal while holding `work-protocol`.
Recovery runs before each live transition. Cross-runtime tests prove Node can
finish a partially applied F#-compatible journal and F# can finish a manually
serialized Node-shaped journal; a divergence test proves preflight rejects
before the earlier event target is written. The complete gate passes 140 tests.

This is infrastructure adoption, not F# command authority. Node still owns work
decisions and effects. Telemetry can be mutated before journal preparation and
backlog has a distinct queue/projection unit, so MIG-05 remains open for those
store-specific recovery designs.

## MIG-05 — backlog recovery and serialization

Added a separate `backlog-state` journal for queue JSON followed by its Markdown
projection. The production Node and typed F# implementations share the exact
record shape. Production backlog operations now hold `work-protocol` over the
whole read/modify/write sequence rather than only replacing individual files.
Partial replay and divergence rejection pass in both runtimes, and a
multi-process test retains all eight concurrent captures. The final complete
gate passes 147 tests. Attachments remain outside the journal with
file-before-reference ordering.

## MIG-05 — telemetry execution-link recovery

Confirmed that telemetry record updates already have a suitable single-file
atomic/lease boundary. Moved execution/context linking into the recoverable work
capability and added retry adoption for one detached execution. Multiple
detached records reject guessing and the exact existing `--execution-id`
provides the repair path. Added a pure typed F# decision with explicit start,
recover, conflict, and ambiguity outcomes. The complete gate passes 157 tests.
MIG-05 is complete within its bounded store contracts.

## MIG-06 — production Git consumer consolidation

Removed the two production Git-status helpers and routed work attribution and
telemetry through one installed Node process adapter whose versioned observation
matches the F# clean/changed/unavailable contract exactly. Work now attributes
rename destinations correctly and rejects unavailable Git before completion
effects, except for the documented pre-`git init` greenfield compatibility
case. Telemetry no longer converts unavailable ending Git state into a measured
zero. The full gate passes 163 tests.

The first adversarial assertion incorrectly expected a zero-line numstat record
for a pure rename. Git emits a rename representation that does not key directly
to the normalized destination, so ROS correctly preserves per-path line stats
as unavailable while retaining aggregate rename count and both paths. The test
was repaired without inventing a measurement.

## MIG-07 — live-work orchestration planning

Added a pure F# plan above the existing transition decision. It projects the
updated work item, semantic event, and ordered telemetry intents from supplied
clock/Git/config/evidence observations without performing effects. Four typed
tests and a production differential over all five legal edges pass. A final
contract review caught that the first JSON encoder collapsed typed rejection
details to a reason string; the encoder now retains state/action or missing
evidence and an explicit CLI test proves it.

Three initial compiler attempts exposed ambiguous record inference and JSON/CLI
overload inference; explicit boundary annotations repaired them. The complete
gate passes 169 tests. Evidence-path I/O, whole-context/multi-item planning,
backlog promotion, and production authority remain open.

## MIG-07 — typed evidence observation

Added a present/missing/unavailable evidence port and filesystem adapter, then
composed it only after the pure transition plan succeeds. Missing and
unavailable issues retain request order and full evidence identity. Controlled
differentials match Node for files, directories, missing paths, and the current
acceptance of absolute paths. Repository containment remains an explicit open
policy question because no authority was found; this shadow slice does not
silently change the contract. One compiler failure exposed the newly required
root composition and was repaired without moving root into the domain planner.
Adversarial self-review then found path normalization outside the adapter's
exception boundary. Moving it inside preserves malformed paths as typed
`Unavailable` outcomes. The first full gate after that repair exposed only a
test-helper naming error; correcting it and rerunning the unchanged gate passed
all 172 checks.

## MIG-07 — whole-context planning

Lifted the pure item planner over an ordered context selection. Existing item
order is retained, newly begun items append in request order, events retain
caller order, first-begin metadata is explicit, and any invalid ID, missing
item, or later transition rejection returns no plan. An explicit context JSON
decoder rejects unknown semantic states, while the output remains a planning
view rather than claiming lossless persistence authority.

Node/F# differentials match multi-item begin and later-item rejection with no
context write. Source archaeology also exposed that current Node telemetry
effects can precede such a later rejection. The future effect handler must
freeze the complete plan before telemetry; this slice does not hide or switch
that production boundary. The complete heterogeneous gate passes all 179
checks with a zero-warning, zero-error F# build.

## MIG-07 — backlog transition and promotion planning

Separated local queue state changes from promotion into live work. The four
queue states and four actions encode only seven observed legal edges. State
changes use explicit keep/clear/set field operations, which a differential
forced after showing that abandonment preserves an earlier block reason.
`Start` is a promotion effect and leaves queue state ready.

Batch promotion preflights all local items as ready while allowing direct IDs
that do not exist in the repository-local backlog. Four typed tests and two
production differentials cover the full 16-pair matrix, block guard, batch
rejection, direct-ID compatibility, and queue-state preservation. Production
state-changing authority remains Node. The complete heterogeneous gate passes
all 185 checks with a zero-warning, zero-error F# build.

## MIG-07 — verified context composition

Composed the whole-context semantic plan with the existing evidence port.
Context rejection and non-completion perform no evidence I/O; accepted
completion observes the command evidence list once in request order and
preserves every missing or unavailable issue. This centralizes evidence
observation for item and context planning without moving filesystem behavior
into Domain.

Two typed tests and a multi-item present/missing production differential pass.
The result remains deliberately pre-effect: telemetry execution IDs must feed
back into final item/event projections before persistence can be rendered. The
complete heterogeneous gate passes all 188 checks.

## MIG-07 — telemetry execution-ID resolution

Composed the existing abstract `TelemetryIntent` plan with an observed
candidate-execution read model. Begin reuses the already-verified
`ExecutionLinkRecovery` single recover-or-reject decision unchanged. Reading
production's resume branch closely showed a materially different rule: it
appends every currently active execution for the work item regardless of
prior link state and never rejects on multiple candidates, unlike begin's
ambiguity guard over unlinked candidates. Finalize similarly re-scans every
active candidate rather than only linked ones, which is how production
recovers an orphaned execution on completion. Modeling resume and finalize as
bulk, non-rejecting operations (sharing one helper) rather than reusing the
recover-or-reject shape avoided inventing a rejection path production does not
have.

Resolution honestly halts and reports `PendingNewExecution` rather than
inventing an ID when an intent would require creating a new execution record,
since that creation is a clock/ID-generation effect outside this migration
phase; steps after the halt are not evaluated because they would need to
observe a not-yet-created record. The outcome/port types were first drafted in
the Application layer, then moved into `Ros.Domain.Work` after noticing the
existing `VerifiedWorkPlanOutcome`/`VerifiedWorkContextPlanOutcome` precedent:
outcome types stay in Domain so Contracts can render them without an
architecture-violating Application reference; only the effect-port record
stays in Application, consistent with `WorkEvidenceRepository`.

Five Node differentials confirm real production behavior for begin recovery,
begin ambiguity rejection, resume bulk-linking across two concurrently active
executions, and completion recovering an orphan before finalizing it versus
finalizing an already-linked execution without re-ensuring it. Thirteen typed
tests cover the pure decision directly. The complete heterogeneous gate passes
all 206 checks (104 Node, 7 Python, 70 F#, 25 differential/smoke) with a
zero-warning, zero-error F# build.

New-execution creation, queue/context persistence effects, and the production
switch remain open.

# Decisions and rationale

`DF-ROS-2026-A027` records the accepted boundary and rejected alternatives. The
Node implementation remains production authority. Project Administration and
Time Entry do not enter the core. SDE draft methods are treated as experimental
governance and their measurements are reported honestly.

# Failures and dead ends

- Subagent execution limits prevented three final parallel reports; partial
  inventory corrections were retained only after direct source verification.
- Baseline context navigation metrics cannot be reconstructed because map and
  manifests did not exist at T0.
- No hosted GitHub/npm or consumer-platform run was available at T1.
- Current hosted-action syntax was source-checked against the official
  `actions/setup-dotnet` documentation; a hosted workflow execution remains
  unobserved.

# Files changed through T5

Before T2: navigation, evidence, experiment, hypothesis, decision,
migration-doc, and ROS work/telemetry records. After T2: the additive F# shadow,
its tests, workflow provisioning, documentation, and result evidence were
added; no production authority source was removed or redirected.

## Four-tier compliance and shadow/production parity audit

Reviewed every `src/` file against `SDE-DOCTRINE-003` (Four-Tier Architecture)
directly rather than relying only on the project-reference architecture
test: grepped every `open` in `Ros.Domain` and `Ros.Application` for
host/effect types (`System.IO`, `System.Diagnostics`, `DateTime.Now/UtcNow`,
`Guid.NewGuid`, `Console`, `Environment`), read every Application-layer
operations module to confirm it composes ports and Domain decisions rather
than inventing its own legality rules, and confirmed Infrastructure/CLI
reference only inward. No tier violation was found — the project-reference
architecture test's structural check and the doctrine's semantic
requirements agree in this codebase.

The audit did surface one real shadow/production parity gap distinct from a
tier violation: `ros-fs work plan --resolve-telemetry` could only be given
candidate executions as synthetic `--candidate` flags, unlike every sibling
observation (`FileEvidenceRepository` for evidence, `ProcessGitRepository`
for Git status), which reads real repository state by default. Added
`Ros.Infrastructure.Work.FileTelemetryStateRepository`, reading the same
`.ros/telemetry/executions/*.json` records production's
`showTelemetry`/`loadExecutions` observe in the same sorted order, and wired
it as the CLI's default when no `--candidate` flags are given (flags remain
available only to force an out-of-repository scenario for controlled
testing). Rewrote the five telemetry differential tests to drop synthetic
candidates entirely, reading the real fixture directory instead — a
strictly stronger parity proof than comparing against a hand-simulated
input. Two new typed tests cover the repository's filtering and
missing-directory behavior directly. Complete gate: 208 checks (104 Node, 7
Python, 72 F#, 25 differential), zero-warning build.

A second, larger gap was named but deliberately not closed in this pass:
`work context-plan`'s `ObservedGitPaths`/`MeaningfulChangedPaths` also remain
flag-only, but production's equivalent composes the Git status observation
with a `ros.json`-configured glob include/ignore filter and an optional
`ROS_BASE_REF` committed-range diff — neither a config-glob matcher nor a
committed-range Git operation exists yet anywhere in F#. Building that
faithfully (matching Node's exact glob semantics and the base-ref existence
check) is a materially larger, novel undertaking than wiring an
already-built port, so it is recorded as its own future slice rather than
attempted ad hoc alongside a compliance audit.

## MIG-07 — Git observed/meaningful-path composition

Closed the second gap the four-tier audit named rather than closed: `work
context-plan`'s `ObservedGitPaths`/`MeaningfulChangedPaths` were flag-only
while every sibling observation (evidence, telemetry, Git status itself) had
moved to a real default. Ported production's `globMatch` character-for-
character rather than reaching for a generic regex-escape helper first —
`Regex.Escape` also escapes `*` itself and several characters (`?`, `#`
among them) Node's manual escape list leaves untouched, which would have
silently broken the `**`/`*` distinction the whole filter depends on. Caught
this by running the exact same fifteen pattern/value pairs through a real
Node script and a throwaway `dotnet fsi` script against the built assembly
before wiring anything into the CLI, rather than trusting a hand-derived
translation.

`ros.json`'s `workProtocol.meaningfulPaths`/`.ignoredPaths` read field-by-
field with the same defaults production falls back to per field, not an
all-or-nothing default — matching a subtlety only visible by testing a
config with one field set and the other absent. The optional `$ROS_BASE_REF`
committed-range extension needed two Git operations beyond the existing
status port (`cat-file -e`, `diff --name-only`); modeled as `NotConfigured` /
`RefUnavailable` / `Committed` / `Unavailable` so an unresolvable ref stays
production's documented silent no-op while a resolvable ref whose diff
itself fails is a hard error, never folded into a silently empty path list.
The CLI gates real observation on production's own condition (completion, or
a repository's first begin) so an irrelevant action never risks a spurious
Git failure; explicit flags remain available to force an out-of-repository
scenario, matching the telemetry `--candidate` precedent.

Five Node differentials (first-begin baseline capture, ROS-housekeeping-path
exclusion from a completion's paths, custom configured patterns, a
resolvable `ROS_BASE_REF` range, and a silently skipped unresolvable one)
and new typed tests for the glob matcher, the config reader, and the base
comparison all pass. One test bug surfaced during the base-ref case: an
unrelated prior `begin` call in the test itself had already set the
context's `startedAt`, silently defeating the first-begin gate the test
meant to exercise — a reminder that a differential's own setup can produce
the same class of false result production code can.

The complete heterogeneous gate passes all 220 checks (104 Node, 7 Python,
79 F#, 30 differential/smoke) with a zero-warning, zero-error build.

## Scope correction: new-execution creation is not a small next step

This journal previously described the next step as "the new-execution
creation effect (clock/ID generation under the work-protocol capability)" —
a parenthetical that badly understated it. `EV-ROS-2026-A044` inventories
`tools/ros_telemetry.mjs` in full: `startExecution` alone composes identity
discovery (provider/runtime detection), a Git baseline snapshot, metric-
registry loading, and initial capability-status seeding before a
schema-valid record can be written at all. A partial record (fabricated or
empty identity/capabilities) would violate the project's own "never invent
a metric" rule and would not be readable by Node's own telemetry consumers.
This is effectively most of MIG-08, not a standalone effect — consistent
with how `DF-ROS-2026-A028`'s Phase A already frames it ("absorbs... all of
MIG-08"), now grounded in the actual code rather than an estimate. MIG-08
needs its own architecture challenge and a deliberately chosen first
vertical slice before implementation begins, following the same discipline
`DF-ROS-2026-A027` used for the original migration.

# Highest-value next step

Give MIG-08 its own architecture challenge and choose its first vertical
slice deliberately, informed by `EV-ROS-2026-A044`, rather than starting
from the execution-creation effect as previously assumed. Until that
scoping happens, continue closing bounded shadow/production parity gaps in
already-claimed areas (matching the telemetry-candidate and Git-path slices)
rather than reaching into MIG-08 piecemeal.
