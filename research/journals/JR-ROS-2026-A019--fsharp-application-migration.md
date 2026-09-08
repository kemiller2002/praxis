---
id: JR-ROS-2026-A019
title: ROS F# application migration execution journal
status: active
version: 1.3.0
research_area: repository-operating-system
author_agent: openai-codex
created: 2026-09-07
updated: 2026-09-08
related_mission: WI-0011
related_package: RP-ROS-2026-A029
evidence_ids:
  - EV-ROS-2026-A015
  - EV-ROS-2026-A018
  - EV-ROS-2026-A030
  - EV-ROS-2026-A031
  - EV-ROS-2026-A032
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

# Highest-value next step

Complete T6 closeout, then prioritize MIG-05 transaction/recovery semantics
before moving work or telemetry writers.
