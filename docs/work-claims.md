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
praxis work validate-run --id ID --token T --generation N --validation NAME --evidence-id EVIDENCE-ID \
  [--timeout-seconds 1800] -- COMMAND [ARGUMENT...]
praxis work complete-verified --id ID --token T --generation N \
  --commit SHA40|SHA64 --verification-revision SHA --authority authority.json \
  --requirement ID ... --validation NAME=passed:EVIDENCE-ID ... \
  --evidence EVIDENCE-ID=RESULT.json[@sha256:HEX] ... [--telemetry-execution EXE-ID] [--timeout-seconds 120]
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

## Evidence model (PRAXIS-REL-005, REL-GATE-001)

Completion evidence is a **host-observed validation result**, never "a file
Praxis can hash". Praxis 3.14.0 asserted every hashable evidence file as a
passed observation at the verification revision, so a file recording a
failing run, or no test run at all, completed work. Now:

1. `work validate-run` is the only producer. It fence-checks the caller
   (token, generation, valid lease; `PRX.CLAIM.*` otherwise), observes
   `git rev-parse HEAD`, and refuses with `PRX.VALIDATION.DIRTY_WORKTREE`
   when a tracked file outside `.ros/` and `.praxis/` differs from HEAD
   (untracked files are ignored). It runs the command as an argument list
   (never through a shell) in the repository root with the timeout (default
   1800 s), and writes:
   - `.ros/claims/<ID>/validations/<EVIDENCE-ID>/<generation>-<rev12>-<observedAt>.output.txt`
     (stdout followed by stderr), and
   - `.ros/claims/<ID>/validations/<EVIDENCE-ID>/<generation>-<rev12>-<observedAt>.json`,
     a `praxis.validation-result/1` document with members, in this order:
     `schemaVersion, workItemId, claimant, claimToken, generation,
     validation, evidenceId, revision, observedAt, startedAt, status,
     exitCode, command, outputSha256`. `status` is `passed` iff the exit
     code is 0, `failed` otherwise, `unavailable` (with `exitCode: null`)
     on timeout or start failure. Files are never overwritten.
   It then appends the I1 event `work.validation.observed` with `claimant,
   claimToken, generation, validation, evidenceId, revision, status,
   observedAt, resultDigest` (SHA-256 of the result document bytes). Exit 0
   when the run passed, 3 when it failed, was unavailable or was refused,
   1 on a storage or event failure; stdout names `resultPath`,
   `resultDigest`, `outputPath` and the result.
2. `complete-verified --evidence EV=PATH` parses PATH strictly as
   `praxis.validation-result/1` and establishes it, failing closed with:

   | Code | When |
   | --- | --- |
   | `PRX.COMPLETION.EVIDENCE_UNRECOGNIZED` | not a validation result (plain text, unparsable, unknown or missing member) |
   | `PRX.COMPLETION.EVIDENCE_UNRECORDED` | its digest is not in a `work.validation.observed` event for this item, token and generation (or the event log cannot be read) |
   | `PRX.COMPLETION.EVIDENCE_FOREIGN_CLAIM` | the result names another work item, token or generation |
   | `PRX.COMPLETION.EVIDENCE_REVISION_MISMATCH` | the result ran at a revision other than `--verification-revision` |
   | `PRX.COMPLETION.EVIDENCE_NOT_PASSED` | the result is `failed` or `unavailable` |
   | `PRX.COMPLETION.EVIDENCE_STALE` | `observedAt` is before claim acquisition or at/after lease expiry |
   | `PRX.COMPLETION.EVIDENCE_VALIDATION_MISMATCH` | it is cited by a `--validation NAME` other than the result's, or as an evidence ID other than the result's |
   | `PRX.COMPLETION.VALIDATION_UNPROVEN` | all submitted evidence is valid but a required validation has none |

3. **Invariant: passing evidence can only come from
   `ValidatedEvidence.establish`.** `ValidatedEvidence`
   (`src/Praxis.Domain/Work/ValidationResult.fs`) is a sealed type with a
   private constructor; `establish` is its only constructor and returns
   the codes above on its error side. `CompletionGate.preflight` returns
   `Result<ValidatedEvidence list, CompletionFinding list>`;
   `CompletionGate.packet` and `CompletionGate.authority` accept only
   `ValidatedEvidence`, and `CompletionAuthority.Evidence` *is*
   `ValidatedEvidence list`. There is no record from which a `Passed`
   observation can be built from a raw file or a failed result: it does
   not type-check. Failed and unavailable results remain representable for
   reporting as a plain `ValidationResult`, which the builders do not
   accept. Before evidence is established, only `submissionDigest` (a
   digest, never a packet) identifies a submission for replay detection and
   refusal events. The test `validation evidence: invariant: ...` is a
   runtime tripwire (no public constructor; non-passing results never
   establish).
4. The authority sends each observation's `Status`, `Revision`,
   `ObservedAt` and `Validation` from the result, plus `ClaimAcquiredAt`,
   `LeaseExpiresAt` and `EvaluatedAt` (Praxis's injected clock). Dokimos
   0.6.0 enforces them as defence in depth (`EVIDENCE_FAILED`,
   `EVIDENCE_STALE`, `STALE_LEASE`, `EVIDENCE_VALIDATION_MISMATCH`). The
   packet/1 and authority/1 schema versions are unchanged: the members are
   optional and additive in Dokimos 0.6.0.

**Remaining risk.** The event log and result files are repository state. A
principal with write access to that state can still forge a result
document *and* a matching `work.validation.observed` event; Praxis
detects inconsistency between them, not collusion. Results are as honest as
the host that ran `validate-run`. CI-produced evidence (results run and
signed by a CI identity the agent cannot write as) is the next hardening
step.

## Completion gate

1. The caller holds the current token, generation and a valid lease.
2. Commit and verification revision are full 40/64-hex object IDs.
3. Every `RequiredEvidenceIds` entry is supplied, every evidence file is a
   recorded `praxis.validation-result/1` established as `ValidatedEvidence`
   (above), and every required validation has such evidence; Praxis
   computes each SHA-256 digest itself.
4. The claim moves to `awaiting-verification` by CAS.
5. Praxis writes `praxis.completion-packet/1` (the agent's claim) and
   `dokimos.completion-authority/1` (claim token, generation and lease
   window, expected commit, required IDs, the established evidence with its
   status, revision, observation time and validation, the evaluation time,
   consumed packet digests) and runs `dokimos completion verify --packet P --authority A`
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
`EVIDENCE_MISSING`, `EVIDENCE_UNREADABLE`, `EVIDENCE_UNRECOGNIZED`,
`EVIDENCE_UNRECORDED`, `EVIDENCE_FOREIGN_CLAIM`, `EVIDENCE_REVISION_MISMATCH`,
`EVIDENCE_NOT_PASSED`, `EVIDENCE_STALE`, `EVIDENCE_VALIDATION_MISMATCH`,
`VALIDATION_UNPROVEN`, `AUTHORITY_UNAVAILABLE`,
`VERIFIER_UNAVAILABLE`, `VERIFIER_TIMEOUT`, `VERIFIER_FAILED`,
`VERIFIER_INPUT_REJECTED`, `RECEIPT_UNPARSABLE`, `RECEIPT_MISMATCH`,
`VERIFICATION_REJECTED` (followed by the receipt's `DOK.COMPLETION.*` codes
verbatim), `CONFLICT`. `PRX.VALIDATION.DIRTY_WORKTREE` (`validate-run`).
Each refusal says how to refresh and replan.

## Events (I1)

Appended to `.ros/events/events.jsonl` with the standard work-event envelope;
an identical event (same content, same `eventId`) is recorded once:
`work.claim.acquired`, `work.claim.rejected`, `work.claim.renewed`,
`work.claim.released`, `work.completion.rejected`, `work.completion.verified`,
`work.completion.replayed`, `work.claim.expired` (durable expiry by
`work expire`; the next acquisition still reports `recovered: true`),
`work.validation.observed` (one `validate-run`; its `resultDigest` is what
makes a result usable as completion evidence).

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
| 5 | Valid implementation, insufficient evidence | `PRX.COMPLETION.EVIDENCE_MISSING`; `PRX.COMPLETION.VALIDATION_UNPROVEN` for a required check with no validated result (no packet is built); a Dokimos `DOK.COMPLETION.*` refusal for a required decision the packet omits | `reliability pipeline: scenarios 1, 5, 7, 8` |
| 6 | Stale Ordo contract | `ORDO.CONTRACT.STALE_REFERENCE` / `PRX.COMPLETION.STALE_ORDO_CONTRACT` before verification | `reliability pipeline: scenario 6` |
| 7 | Replayed completion | Praxis replays idempotently without re-verifying; Dokimos refuses the consumed packet (`REPLAYED_PACKET`) | `reliability pipeline: scenarios 1, 5, 7, 8` |
| 8 | Token usage unavailable | Verified count measured; token/cost ratios `unavailable` and `null`, never 0 | `reliability pipeline: scenarios 1, 5, 7, 8` |
| 9 | Token usage present and attributed | Deduped per observation, attributed by ledger or session window | `reliability:` report tests |
| 10 | Independent requirements concurrently | Each item claimed by its own agent in parallel, all at generation 1, no rejection events | `reliability pipeline: scenario 10`; `work claims: batched slices keep independent claims and evidence` |
| 11 | Failing, unrelated, stale or mis-bound evidence (REL-GATE-001) | `EVIDENCE_UNRECOGNIZED` / `EVIDENCE_NOT_PASSED` / `EVIDENCE_STALE` / `EVIDENCE_VALIDATION_MISMATCH` before Dokimos; Dokimos 0.6.0 independently refuses such observations on the wire | `reliability pipeline: REL-GATE-001 ...`; `validation evidence:` tests |
