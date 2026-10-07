---
id: EX-ROS-2026-A020
title: F# artifact-management shadow migration
research_area: repository-operating-system
status: completed
created: 2026-09-07
author_agent: openai-codex
tests_hypotheses:
  - HY-ROS-2026-A021
  - HY-ROS-2026-A022
  - HY-ROS-2026-A023
  - HY-ROS-2026-A024
inputs:
  - EV-ROS-2026-A018
  - RP-ROS-2026-A017
outputs:
  - EV-ROS-2026-A028
related_theories: []
related_documents:
  - DF-ROS-2026-A027
  - JR-ROS-2026-A019
tags: [fsharp, artifacts, shadow, compatibility, sde]
---

# Experiment

## Research question

Can ROS move artifact validation and deterministic registry projection into a
typed, structurally local F# vertical slice while the unchanged Node runtime
remains authoritative and all required observable behavior remains recoverable?

## Hypotheses tested

Primary: `HY-ROS-2026-A021`, `HY-ROS-2026-A022`, and
`HY-ROS-2026-A023`. Instrumentation feasibility only:
`HY-ROS-2026-A024`. Workflow/distribution hypotheses A025/A026 receive no
causal conclusion from this slice.

## Variables

- **Treatment:** F# semantic model, application handler, explicit front-matter
  boundary, filesystem adapter, and shadow CLI for artifact validation/registry
  projection.
- **Controls:** immutable Node baseline at the tag; Python implementation as an
  independent legacy oracle where its scope overlaps.
- **Primary outcomes:** structured finding identity, exit status, managed
  registry filenames/bytes, canonical-input mutation count, repeated-run
  stability, architecture-boundary result, and rollback viability.
- **Secondary outcomes:** independent rule sites, build/test attempts and
  failures, defects by detector, changed files/LOC, elapsed time, and navigation
  observations.

## Method

1. Preserve the immutable baseline tag and current Node production entry.
2. Freeze language-neutral valid/invalid artifact fixtures before F# behavior.
3. Add a repository-local framework-dependent .NET 10 shadow solution with
   inward dependency checks; do not package or route production callers to it.
4. Implement only artifact metadata validation and registry projection.
5. Run Node, Python, and F# against equivalent temporary repositories and
   compare structured meaning and registry bytes.
6. Prove positive and rejection paths, including a deliberately invalid
   architecture fixture or equivalent adversarial input.
7. Keep all negative results and explain intentional deltas. Node remains the
   rollback path because it is not changed or dual-writing during comparison.

## Acceptance criteria

- Existing baseline test/build gates continue to pass.
- The F# solution compiles with no outward Domain dependency.
- Architecture verification passes on production projects and rejects a known
  forbidden dependency fixture/model.
- Valid fixtures produce no artifact findings across implementations.
- Invalid fixtures agree on required semantic finding categories; any text-only
  difference is documented.
- F# registry output is byte-for-byte equal to Node for all managed registries
  on controlled fixtures and the repository snapshot.
- Dry-run/check do not mutate canonical Markdown; repeat build is idempotent.
- No production launcher, bootstrap profile, or workflow authority switches.
- Rollback is deletion/non-use of the additive shadow surface.

## Falsification criteria

Stop or revise the slice on unexplained registry bytes, lost fields, different
required finding identity, canonical input mutation, false-success exit status,
outward Domain dependency, hidden shell decision logic, or need to guess an
unreconstructable contract. Do not weaken fixtures after observing F# results.

## Controls

The control commit/tag is immutable. Fixtures are copied into new temporary
directories for each implementation. Timestamps and temporary paths are
excluded only where they are not part of the artifact contract. Registry bytes
and process exits are compared directly. Node and F# never write the same test
directory concurrently.

## Checkpoints and instrumentation

The installed SDE convention governs: T0 experiment start; T1
instrumentation/bootstrap complete; T2 semantic foundation established; T3
first vertical slice complete; T4 implementation complete; T5 verification
complete; T6 final evidence. T0 began at
`2026-09-07T20:31:41.590Z`; T1 was frozen at `2026-09-08T00:08:50Z`; T2 at
`2026-09-08T00:19:38Z`. Later timestamps belong in JR-A019 and EV-A028.

Telemetry capability state at T0 is authoritative in execution
`EXE-20260907T203141590Z-54f547f8`. Model/model-version, token, cache, context,
tool, and cost values are not estimated: runtime-known token fields are
`supported-unavailable`; other fields are `unknown` as recorded. Git/clock,
explicit test/build counts, and agent orchestration facts use their named
sources.

## Results

MIG-03/MIG-04 completed without an authority switch. Valid F# fixtures had no
findings; the invalid fixture's ten preregistered path/field/message identities
matched Node; all eight F# registries matched Node bytes; canonical fixture
Markdown hashes were unchanged; a repeated build had zero changes; the current
repository passed F# artifact validation and registry check. An injected
second-write failure remained explicit as an indeterminate incomplete outcome.
The full unrestricted suite passed 87 Node, 7 Python, 8 F#, and 3
differential/smoke tests, plus both TypeScript builds. See `EV-ROS-2026-A028`.

## Threats to validity

- One deterministic capability cannot establish migration-wide productivity or
  defect-rate effects.
- The Python oracle shares historical semantics and is not fully independent.
- The primary agent created the fixtures and implementation unless the final
  adversarial review can run in a separate context.
- The T0 repository lacked project SDE map/manifests, so retrospective baseline
  context metrics are missing.
- Local macOS/.NET success does not establish consumer platform support.

## Replication notes

Use the baseline tag for control, the fixture manifest/checksums for inputs, and
the commands in `docs/migrations/fsharp/README.md`. Retain the resulting logs or
machine-readable comparison evidence, including failures.

## Conclusion

The bounded claim is supported: a typed F# artifact shadow can preserve the
measured deterministic projection contract while the Node authority stays in
place. It does not establish a general production-switch, transaction, hosted
workflow, or consumer-distribution conclusion.

## Registry updates required

Run `./ros registry build` after adding this record and again at mission
closeout.
