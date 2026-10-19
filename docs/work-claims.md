# Atomic work ownership and verified completion

Implements `RQ-ROS-2026-A032` (PRX-REL-001..007, 010, 011). Praxis gives every
work item an optional **claim**: one holder at a time, a lease, a fencing
generation and a record revision. Completion through a claim is accepted only
when the independent Dokimos verifier returns a receipt bound to exactly the
packet Praxis built. The existing `work begin`/`work complete` lifecycle is
unchanged; claims are additive.

## Claim record

A claim (`praxis.work-claim/1`) is stored apart from `.ros/work/queue.json`,
which is never rewritten, so existing queues load unchanged and old readers
are unaffected.

| Field | Meaning |
| --- | --- |
| `revision` | +1 on every durable write; the compare-and-swap key |
| `generation` | fencing token; +1 on every new acquisition (including recovery) |
| `status` | `unclaimed`, `claimed`, `running`, `awaiting-verification`, `completed`, `failed`, `expired` |
| `holder` | claimant, single-use claim token, `acquiredAt`, `leaseExpiresAt` |
| `pendingPacketDigest` | the packet being verified (crash recovery) |
| `consumedPacketDigests` | packets already accepted (replay protection) |
| `completion` | commit, verification revision, requirement IDs, packet and receipt digests, verifier |

`effectiveStatus` in command output reports `expired` for an active claim
whose lease has passed, even before anyone records that durably.

## Transitions

```
unclaimed|failed|expired --claim--> claimed --renew --running--> running
claimed|running --complete-verified--> awaiting-verification --verified--> completed
awaiting-verification --refused--> running            (holder fixes and resubmits)
claimed|running|awaiting-verification --release--> unclaimed (--failed: failed)
active + lease passed --expire--> expired ; --claim/recover--> claimed (generation + 1)
```

Every holder operation presents claimant, token and generation and is refused
unless they match the current holder and the lease is valid. An owner whose
lease expired can never renew or complete, even if no one recovered yet.
Re-sending the same acquisition (same claimant and token) or the same
completion packet returns the original result; no state changes and no
duplicate event is recorded (`work.completion.replayed` is emitted for a
completion retry).

## Stores (one port, three implementations)

`WorkClaimStore.CompareAndSwap expected next` writes only if the stored
revision is still `expected` (`None`: no claim may exist yet).

- **In-memory** (`InMemoryWorkClaimStore`): deterministic tests.
- **Local file** (`--store local`, default; `FileWorkClaimStore`): each
  revision is a directory `.ros/claims/<ID>/<revision>/claim.json`, staged
  privately and published by an atomic directory rename. Revisions are dense,
  so exactly one writer can publish revision N+1. Single host only.
- **Git ref** (`--store git [--remote origin]`; `GitRefWorkClaimStore`): the
  claim is a commit under `refs/praxis/claims/<ID>` on a shared remote,
  published with `git push --force-with-lease=<ref>:<observed-oid>` (empty
  oid: the ref must not exist). The remote's ref update is the linearization
  point, so agents on different hosts never rely on a shared filesystem lock.

## Commands

All commands print one JSON document. Exit codes: `0` recorded or replayed,
`2` invalid arguments, `3` refused (structured codes), `1` storage or event
failure. `--claimant` defaults to the resolved actor ID.

```
praxis work claim --id ID [--token T] [--lease-seconds 1800] [--expected-revision N] [--store local|git] [--remote NAME]
praxis work recover --id ID [--token T]          # only an expired claim
praxis work renew --id ID --token T --generation N [--lease-seconds S] [--running]
praxis work release --id ID --token T --generation N [--failed]
praxis work expire --id ID
praxis work claims [ID]
praxis work complete-verified --id ID --token T --generation N \
  --commit SHA40|SHA64 --verification-revision SHA --authority authority.json \
  --requirement ID ... --validation NAME=passed:EVIDENCE-ID ... \
  --evidence EVIDENCE-ID=PATH[@sha256:HEX] ... [--telemetry-execution EXE-ID] [--timeout-seconds 120]
```

`authority.json` is Praxis's minimal authoritative input (until Ordo
admission is wired in):

```json
{ "OrdoContractVersion": "...", "OrdoContractDigest": "sha256:<64 hex>",
  "SupportedOrdoContractVersions": ["..."],
  "RequiredRequirementIds": [], "RequiredDecisionIds": [], "RequiredConflictResolutionIds": [],
  "RequiredValidationNames": [], "RequiredEvidenceIds": [],
  "ExpectedCommit": "<optional; default Git HEAD>", "ExpectedVerificationRevision": "<optional; default Git HEAD>" }
```

## Completion gate

1. The caller holds the current token, generation and a valid lease.
2. Commit and verification revision are full 40/64-hex object IDs.
3. Every `RequiredEvidenceIds` entry is supplied and every evidence file
   exists; Praxis computes each SHA-256 digest itself.
4. The claim moves to `awaiting-verification` by CAS.
5. Praxis writes `praxis.completion-packet/1` (the agent's claim) and
   `dokimos.completion-authority/1` (claim token and generation, expected
   commit, required IDs, Praxis-observed evidence digests, consumed packet
   digests) and runs `dokimos completion verify --packet P --authority A`
   (`PRAXIS_DOKIMOS_EXECUTABLE`, else `dokimos` on PATH; a `.dll` runs via
   `dotnet`).
6. Only exit 0 with a receipt whose contract, verdict, work item, commit,
   verification revision and packet digest match is verification. Praxis
   computes the packet digest exactly as Dokimos does.
7. The final transition to `completed` is a CAS on the same generation.

Missing verifier, start failure, crash, timeout, unparsable or mismatched
receipt all fail closed. A crash after verification and before the final CAS
leaves `awaiting-verification`; the same holder retrying the same packet
completes exactly once, and if the lease expires first, a new claimant
recovers at generation + 1 and the old owner is refused.

## Codes

`PRX.CLAIM.HELD`, `STALE_REVISION`, `STALE_GENERATION`, `STALE_TOKEN`,
`EXPIRED`, `NOT_CLAIMABLE`, `NOT_HELD`, `CONFLICT`, `INVALID_REQUEST`.
`PRX.COMPLETION.INVALID_COMMIT`, `INVALID_VERIFICATION_REVISION`,
`EVIDENCE_MISSING`, `EVIDENCE_UNREADABLE`, `AUTHORITY_UNAVAILABLE`,
`VERIFIER_UNAVAILABLE`, `VERIFIER_TIMEOUT`, `VERIFIER_FAILED`,
`VERIFIER_INPUT_REJECTED`, `RECEIPT_UNPARSABLE`, `RECEIPT_MISMATCH`,
`VERIFICATION_REJECTED` (followed by the receipt's `DOK.COMPLETION.*` codes
verbatim), `CONFLICT`. Each refusal says how to refresh and replan.

## Events (I1)

Appended to `.ros/events/events.jsonl` with the standard work-event envelope;
an identical event (same content, same `eventId`) is recorded once:
`work.claim.acquired`, `work.claim.rejected`, `work.claim.renewed`,
`work.claim.released`, `work.completion.rejected`, `work.completion.verified`,
`work.completion.replayed`. `work expire` records state only (I1 defines no
expiry event; the next acquisition reports `recovered: true`).

## Batches

Each slice of a cohesive batch is claimed, renewed and completed on its own
claim with its own evidence (`WorkClaims.acquireEach`); no ownership is shared.
