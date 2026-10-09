# ECIR source-validated execution gates

Status: **interim fail-closed bridge**, not the completed trusted Ordo execution adapter.
Tracks [praxis#220](https://github.com/kemiller2002/praxis/issues/220), [ordo#59](https://github.com/kemiller2002/ordo/issues/59), and [conditor#81](https://github.com/kemiller2002/conditor/issues/81).

## Scope

Praxis reuses its existing execution groups and work-item lifecycle. Source requirement keys, acceptance obligations, decisions and verification evidence continue to be attached to each member; a grouped pass must never count all source requirements as independently verified.

Ordo owns ECIR parsing and semantic checks. Conditor owns the independent source manifest. Praxis owns the **decision to begin work** and checks trusted Ordo observations, external authorizations, source/blueprint hashes, cohort membership and unresolved requirements. Dokimos owns independent behavioral verification.

## First increment: the safety gate

`EcirGates.problems` is a pure validation function over *observed*, not model-claimed, facts. All fields required, all requirements exact, no blocked entries, matching blueprint/source digests, pinned validator, verified Git artifact commit, externally verified decision approvals. Unknown or missing evidence is a refusal.

**Existing work groups are unchanged.** A work group that explicitly declares ECIR with a `sharedContext` entry prefixed `ecir/1:` is identified at `plan execute-group`. Until the pinned Ordo reader and externally verifiable approval adapter are qualified in the shared release registry, such groups are **refused before beginning a work item**, including when an agent omits extra CLI flags. This is intentional: a model-supplied JSON "approved" bit is not authorization.

The group's source manifest and blueprint must be committed immutable artifacts and reference the Conditor intake digest. `sde ecir validate --manifest ... --blueprint ...` checks structural and semantic validity **only** and prints a canonical SHA-256 digest, never execution authorization. A later adapter must pin the released Ordo binary and artifact SHA, observe that result independently and verify decision authorization with approved provenance before supplying `EcirExecutionObservation`.

## Remaining mandatory implementation before ECIR execution can start

1. Release/qualify the ECIR-enabled Ordo version and its `sde ecir validate` operation through Conditor.
2. Implement the filesystem/Git/process adapter that reads the immutable blueprint from a pinned revision, invokes the **pinned** Ordo executable and verifies canonical digests.
3. Resolve group-to-cohort/source requirement identity and decision authorization from trusted Ordo/Praxis data, **never** from ECIR self-attestation.
4. Wire successful `EcirGates.allowsExecution` results into `plan execute-group`, preserving atomicity, member-scoped evidence, telemetry and checkpoints.
5. Independently verify behavioral acceptance with Dokimos and measure batch-vs-sequential drift/rework in the registered experiment.

Do **not** remove the fail-closed group guard to force a demo to pass. For Indy Init, retain the existing proven execution mode until this entire chain is qualified; planning-only ECIR generation can still be rehearsed.
