# ECIR signed receipt continuation, 2026-10-10

Work item: GH-220 / https://github.com/kemiller2002/praxis/issues/220.
Base: Praxis PR #222, commit `5607e3f51990efcf4ed6a85cc1174f1dbab7e0eb`.

## Verifier correction

The verifier previously accepted any ECDSA key with 256 bits, despite the
wire protocol requiring NIST P-256. A host allow-list entry containing a
secp256k1 public key and a correctly signed receipt was accepted. The
verifier now checks the named curve OID and explicitly uses IEEE P1363
signature encoding. No signing key or production approval was created.

Validation: the nine signed-receipt tests passed through F# Interactive.
Running those same tests against the base verifier produced eight passes
and the expected alternate-curve rejection failure. The added tests also
force signature verification to detect tampering by matching the changed
scope to the host's expected scope, and cover issuance/expiry boundaries,
malformed base64, signature length, and DER encoding rejection.

## Next execution integration, still blocked

`ExecuteGroupCommands.run` currently obtains `FileGroupExecution.facts`
before mutation, invokes `beginMember`, then records the group using
`FileWorkGroupRepository.transact`. The latter lock protects only the group
store. It does not make the earlier authorization, member start and group
record one transaction. Retain the ECIR refusal until the following are
implemented and qualified together:

1. A host-controlled policy supplies source/release pins, signer keys and
   revocation state from outside agent-writable repository/configuration
   files. A policy revision and an audited approval issuance/revocation flow
   must be observable; receipt possession is not policy authority.
2. Under a transaction boundary shared by membership changes and member
   state transitions, reread group membership, requirement identities,
   current policy/revocations and a fresh host clock. Revalidate committed
   inputs, pinned Ordo evidence and receipt against that same snapshot.
   A prior read-only positive preflight is insufficient.
3. Persist member start, group execution and authorization evidence as one
   recoverable operation. A crash must not leave an authorized-looking group
   without its member transition, or a started member without group evidence.
   Preserve each member's separate evidence, telemetry and checkpoints.
4. Test membership/source/decision/policy changes between preflight and
   dispatch, revocation and expiry during validation, concurrent starts,
   duplicate retries and failure at each persistence boundary. Refusals
   must leave work state unchanged; recovery must avoid duplicate starts.
5. Qualify the actual released Ordo and Conditor pins plus independent
   Dokimos acceptance before opening ECIR execution.

This document records observed gaps and existing acceptance requirements;
it does not establish a new approval authority or unlock execution.
