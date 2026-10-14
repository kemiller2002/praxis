# Signed exact local worker delegation

Work: [#231](https://github.com/kemiller2002/praxis/issues/231).
Proposal: [DF-ROS-2026-A063](../research/decisions/DF-ROS-2026-A063--signed-exact-local-worker-delegation.md).

`LocalWorkerDelegation` binds a complete local worker packet to an operator key
in the current protected ECIR policy. It is a verifier and optional authority
callback for the existing application ports. It does not register a command,
launch a worker or provide authenticated operator provisioning.

An ECIR approval covers the source, cohort, requirements and decisions. A
separate delegation signature additionally binds worker and execution identities,
role, allowed files, prerequisite evidence, acceptance assignments, time and
budgets through `LocalAgentHandoff.packetDigest`. An ECIR signature cannot be
reused as a delegation signature: the signing payload has a separate versioned
domain and UTF-8 byte-length framing. Both signatures use ECDSA NIST P-256,
SHA-256 and the 64-byte IEEE P1363 representation.

The five-field envelope uses schema `praxis.local-worker-delegation/1`:
`schemaVersion`, `keyId`, `approver`, `packetDigest`, `signature`. JSON input is
bounded to 8 KiB and depth two, with exact fields and bounded strings. Duplicate,
unknown, missing and nonstring fields refuse. Decoding is never approval.

Verification checks the freshly loaded policy revision and qualified release,
the independently bound repository identity/root, exact host member/source map,
cohort/source/blueprint pins and current receipt bytes. The packet's decisions
must match the valid ECIR receipt. The packet's interval must lie wholly inside
that approval's interval, and the current UTC clock must be inside both. The
current ECIR approver key must also sign the complete packet. Revoking that key
or advancing policy revision invalidates the delegation. This version does not
support delegating approval to a different key.

The packet identifies a worker by `WorkerId`; it does not embed a process spec.
The protected host must separately bind that identity to a qualified executable,
dependency closure, argv, worktree and environment. Changing that binding needs
fresh qualification and delegation policy revision. The signature cannot make
an arbitrary adapter selected by a caller safe.

`authority` reloads policy and the envelope, then samples the clock each time
admission, reservation, launch intent or intake invokes it. It returns ephemeral
`LocalHandoffAuthority`; downstream `CheckedLocalWorkerPacket` admission remains
mandatory. The host must hold protected locks through these checks and launch.
A successful callback sampled outside that scope is not a durable capability.

The privileged operator must independently review the entire packet before a
host-owned signing operation. The signing payload helper has no private key or
signing implementation. A protected public-key allowlist authenticates a
signature as that key's approval; it does not prove OS user identity, signer
custody, native member executions, current validator observations, worktree
containment or semantic correctness of an operator's selection. Those production
bindings remain required. No fixture key or synthetic observation may enable the
runtime. Existing immutable reservations continue to reject automatic relaunch;
a signature is not a one-time-use store.

The host persistence and deployment limits remain in
[local ECIR authority](local-ecir-host-authority.md). Worker isolation and the
actual launch/intake/integration/completion path are still pending qualification.

## Verified checks

The Release tests/CLI build passed with zero warnings and errors. All 13 new
delegation tests passed, including signed-authority composition through actual
file journals, rejection before reservation after revocation, immutable replay
refusal, tampering, expired/revoked authority, strict JSON and unavailable host
observations. The existing handoff filter passed 29 tests, journaled-worker
filter passed 19, and the ECIR filter passed 57 (including two delegation tests).
These counts overlap and are not a combined total. Fixture signer keys, process
identities and native/validator observations are synthetic; no isolated model
worker or authenticated operator provisioning was qualified. Registry build,
native validation and diff checks passed. Full CI on this updated head is pending.
