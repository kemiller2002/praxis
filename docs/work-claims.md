# Atomic work ownership and verified completion

Implements `RQ-ROS-2026-A032` (PRX-REL-001..007, 010, 011). Praxis gives every
work item an optional **claim**: one holder at a time, a lease, a fencing
generation and a record revision. Completion through a claim is accepted only
when the independent Dokimos verifier returns a receipt bound to exactly the
packet Praxis built. Claims are additive, but once an item has a claim record
the legacy `work complete` transition is refused
(`PRX.COMPLETION.VERIFIED_COMPLETION_REQUIRED`, exit 3) until
`work complete-verified` has recorded a Dokimos-accepted completion; setting
`workProtocol.requireVerifiedCompletion: true` in `ros.json` extends that rule
to every item. Unreadable policy or claim state fails closed.

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

## Ordo contract binding (PRX-REL-008)

`work complete-verified --ordo-contract FILE [--expected-ordo-digest D]` admits
the Ordo `ecir/1` contract (`praxis ordo contract check` semantics) and binds
it to the authority: version and digest come from admission, a conflicting
pinned version or digest is `PRX.COMPLETION.STALE_ORDO_CONTRACT`, a required
requirement/decision/conflict-resolution ID absent from the contract is
`PRX.COMPLETION.UNKNOWN_CONTRACT_REFERENCE`, and an authority naming no
required requirements inherits the contract's. Admission failures carry
Ordo's own `ORDO.CONTRACT.*` codes. Nothing reaches Dokimos on refusal.

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
`work.completion.replayed`, `work.claim.expired` (durable expiry by
`work expire`; the next acquisition still reports `recovered: true`).

## Batches

Each slice of a cohesive batch is claimed, renewed and completed on its own
claim with its own evidence (`WorkClaims.acquireEach`); no ownership is shared.

## Integration scenarios

| # | Scenario | Expected | Test evidence |
|---|---|---|---|
| 1 | One agent completes one requirement | Verified by the real Dokimos, claim `completed`, plain `work complete` then permitted | `reliability pipeline: scenarios 1, 5, 7, 8` (needs `PRAXIS_TEST_DOKIMOS_DLL`) |
| 2 | Two agents acquire the same item | Exactly one winner; losers get `PRX.CLAIM.HELD`/`CONFLICT` | `work claims:` race tests (memory, file, git with bare origin) |
| 3 | Lost ownership, then completion | `PRX.CLAIM.STALE_*`/`EXPIRED`, nothing verified | `work claims:` stale-owner tests |
| 4 | Agent crashes, work recovered | Recovery at generation + 1; old owner refused; no double completion | `work claims:` interruption/recovery tests |
| 5 | Valid implementation, insufficient evidence | `PRX.COMPLETION.EVIDENCE_MISSING`, and a Dokimos `DOK.COMPLETION.*` refusal for an unproven required check | `reliability pipeline: scenarios 1, 5, 7, 8` |
| 6 | Stale Ordo contract | `ORDO.CONTRACT.STALE_REFERENCE` / `PRX.COMPLETION.STALE_ORDO_CONTRACT` before verification | `reliability pipeline: scenario 6` |
| 7 | Replayed completion | Praxis replays idempotently without re-verifying; Dokimos refuses the consumed packet (`REPLAYED_PACKET`) | `reliability pipeline: scenarios 1, 5, 7, 8` |
| 8 | Token usage unavailable | Verified count measured; token/cost ratios `unavailable` and `null`, never 0 | `reliability pipeline: scenarios 1, 5, 7, 8` |
| 9 | Token usage present and attributed | Deduped per observation, attributed by ledger or session window | `reliability:` report tests |
| 10 | Independent requirements concurrently | Each item claimed by its own agent in parallel, all at generation 1, no rejection events | `reliability pipeline: scenario 10`; `work claims: batched slices keep independent claims and evidence` |
