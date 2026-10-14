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

## Trusted read-only preflight (implemented on this branch)

The infrastructure now contains two independent adapters:

- `FileEcirPreflight.readCommitted` reads the manifest and candidate blueprint **from one exact full Git commit SHA**. Symbolic branches, tags, shortened hashes, absolute/traversing paths, uncommitted working-tree modifications, and mismatch to the trusted external source-manifest digest fail before code generation. The host, not the agent, supplies the expected source digest and revision.
- `FileEcirValidator.validate` runs the absolute-path **host-pinned Ordo native executable**, compares the actual executable SHA-256 to the host release pin, supplies only those committed inputs, and accepts Ordo's exact `ecir.validate/1` machine response. Ordo's `executionAuthorized:false` is mandatory. Invalid output, mismatched canonical blueprint digest, nonzero exit and timeouts are refusals. `parseResponse` has adversarial tests for spoofed approval, changed schema, stale blueprint and missing source coverage.

Neither step accepts AI-generated authorization. A committed file's immutability is **not a trust claim about its author**, and an executable hash matches a host-provided pin only if the host actually protects that release pin.

The validator is a **read-only inspection boundary**, not a new bypass around `plan execute-group`: ECIR groups remain denied by `FileGroupExecution.facts`. The next integration must bind the returned observation to a trusted, owner-verified decision approval receipt with the exact blueprint digest, cohort ID and source commit, then use `EcirGates.allowsExecution` as part of an atomic group-execution authorization check.

## Signed, exact-scope approval receipts (new implementation)

`EcirApprovals` is the narrow authority boundary between architectural validation and execution approval. The `ecir.approval/1` wire artifact has these required fields:

- `schemaVersion`, `keyId`, `approver`, `issuedAt`, `expiresAt`, `signature`
- `scope.groupId`, `scope.cohortId`, `scope.sourceCommit`, `scope.manifestDigest`, `scope.blueprintDigest`, `scope.requirementKeys`, `scope.decisionIds`

The receipt is **not trusted because it was committed**, because its author was an agent, or because its JSON says "approved". The host injects a protected allow-list mapping `keyId` to a public ECDSA P-256 key and authenticated signer identity. The receipt is a UTF-8 length-framed, domain-separated canonical payload signed by that signer using SHA-256 and a 64-byte IEEE-P1363-format signature, encoded as base64. The verifier does not store or generate private keys.

An approval is accepted only if the signature verifies, the key remains on the host allow-list, identity matches, the UTC interval is valid (maximum 24 hours), and **all** commit, manifest, blueprint, group, cohort, original requirement keys and required decision IDs match the independently supplied scope. A modified or duplicated requirement, newly introduced decision, changed cohort, stale blueprint or revoked signer invalidates the receipt. Removing a signer from the host trust map revokes approvals issued by that key; production must apply changes before dispatch.

`FileEcirAuthorization.verifyReadOnly` composes: verified source commit -> pinned Ordo executable -> independently derived cohort obligations -> signed approval -> `EcirGates`. This adds **read-only evidence**, not an automatic way to start agents. The current `plan execute-group` rejection for ECIR stays in force.

**Remaining host integration:** protect trusted public keys and source pins outside agent-writable files; implement an audited approval issuance/revocation flow; bind the approval and Ordo evidence to work-item membership and the same atomic work-protocol transition; revalidate at the transition boundary to avoid time-of-check/time-of-use races. Never create a default test key or accept unsigned/broad/all-cohorts approval in production. The user approving the PR does not imply approval of every future ECIR blueprint.

## Remaining mandatory implementation before ECIR execution can start

1. Release/qualify the ECIR-enabled Ordo version and its `sde ecir validate` operation through Conditor.
2. Implement the filesystem/Git/process adapter that reads the immutable blueprint from a pinned revision, invokes the **pinned** Ordo executable and verifies canonical digests.
3. Resolve group-to-cohort/source requirement identity and decision authorization from trusted Ordo/Praxis data, **never** from ECIR self-attestation.
4. Wire successful `EcirGates.allowsExecution` results into `plan execute-group`, preserving atomicity, member-scoped evidence, telemetry and checkpoints.
5. Independently verify behavioral acceptance with Dokimos and measure batch-vs-sequential drift/rework in the registered experiment.

Do **not** remove the fail-closed group guard to force a demo to pass. For Indy Init, retain the existing proven execution mode until this entire chain is qualified; planning-only ECIR generation can still be rehearsed.
