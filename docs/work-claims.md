# Atomic work ownership and verified completion

Implements `RQ-ROS-2026-A032` (PRX-REL-001..007, 010, 011; PRAXIS-REL-009, 011, 012). Praxis gives every
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
  revision is a directory `<store>/claims/<ID>/<revision>/claim.json` in the
  out-of-tree store (below), staged
  privately and published by an atomic directory rename. Revisions are dense,
  so exactly one writer can publish revision N+1. Single host only.
- **Git ref** (`--store git [--remote origin]`; `GitRefWorkClaimStore`): the
  claim is a commit under `refs/praxis/claims/<ID>` on a shared remote,
  published with `git push --force-with-lease=<ref>:<observed-oid>` (empty
  oid: the ref must not exist). The remote's ref update is the linearization
  point, so agents on different hosts never rely on a shared filesystem lock.

## Out-of-tree transient state (I8, PRAXIS-REL-011)

REL-001 run-01 found verified completions that left `.ros/` claim and
validation state dirty (4 of 6 runs) or moved HEAD past the verified commit
(2 of 6). Transient protocol state now lives in **the store**,
`$(git rev-parse --git-common-dir)/praxis/`, which Git never tracks and every
worktree of the repository shares:

| Path | Holds |
| --- | --- |
| `claims/<ID>/<revision>/claim.json` | the local claim store |
| `validations/<ID>/<EVIDENCE-ID>/…` | `validate-run` results and output |
| `receipts/<ID>/<generation>-<commit12>-<receiptId>.json` | verifier receipts (DSSE envelopes, exact bytes) and `.output.txt` |
| `events.jsonl` | claim-protocol events (I1), idempotent by `eventId`, behind a lock file |
| `cache/ordo/<digest-hex>.json` | admitted Ordo contracts, canonical form, keyed by digest |

A directory that is not yet a Git repository uses `<root>/.git/praxis/`,
which is where `git init` puts the common directory, so initializing later
loses nothing. The `--store git` claim refs are unchanged.

**Legacy `.ros/claims`.** A claim that exists only in the pre-3.17 tracked
store is never read as current and never silently lost: every command that
reads it (and `work claims`) is refused with `PRX.CLAIM.LEGACY_STORE`
(exit 3) until `praxis work claims migrate [--keep-legacy]` copies every
revision and its validation results into the store, reads the latest
revision back, and only then removes the legacy directory. An item present in
both stores with diverging revisions is reported as `conflict` and left
untouched.

Only the **canonical completion event** is tracked (see "Clean completion
identity"). To report on claim-protocol events, pass the store log to
`telemetry reliability-report --events "$(git rev-parse --git-common-dir)/praxis/events.jsonl"`;
the default tracked log holds the completion events only.

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
praxis work verify --id ID --token T [--timeout-seconds 3600]        # agent; PRAXIS_VERIFIER_SOCKET
praxis work complete-verified --id ID --token T                        # PRAXIS_COMPLETION_AUTHORITY, PRAXIS_ORDO_CONTRACT
  [--authority FILE] [--ordo-contract FILE] [--expected-ordo-digest sha256:HEX] [--claimant NAME] [--generation N]
  [--commit SHA] [--verification-revision SHA] [--requirement ID]* [--decision ID]* [--conflict-resolution ID]*
  [--validation NAME=STATUS:EVIDENCE-ID]* [--evidence EVIDENCE-ID=FILE[@sha256:HEX]]* [--telemetry-execution EXE-ID]*
  [--timeout-seconds 120]
praxis work completion-identity --id ID
praxis work claims migrate [--keep-legacy]
praxis verifier serve --socket PATH --policy FILE --contract-digest sha256:HEX [--key-file PEM]   # host
praxis verifier run --repo PATH --commit SHA --work-item ID --claimant NAME --generation N \
  --policy FILE --contract-digest sha256:HEX --key-file PEM --out FILE                             # host or CI
```

`authority.json` is Praxis's minimal authoritative input (until Ordo
admission is wired in):

```json
{ "OrdoContractVersion": "...", "OrdoContractDigest": "sha256:<64 hex>",
  "SupportedOrdoContractVersions": ["..."],
  "RequiredRequirementIds": [], "RequiredDecisionIds": [], "RequiredConflictResolutionIds": [],
  "RequiredValidationNames": [], "RequiredEvidenceIds": [],
  "ExpectedCommit": "<optional; default Git HEAD>", "ExpectedVerificationRevision": "<optional; default Git HEAD>",
  "TrustedVerifierKeys": [ { "KeyId": "sha256:<hex>", "Algorithm": "ecdsa-p256-sha256", "PublicKey": "<base64 SPKI DER>" } ],
  "ValidationPolicyDigest": "sha256:<64 hex>" }
```

`TrustedVerifierKeys` and `ValidationPolicyDigest` (I6) are optional. A
non-empty `TrustedVerifierKeys` switches the item to **attested mode**
(below). Without it, behaviour is the non-attested `validate-run` path.

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
   - `<store>/validations/<ID>/<EVIDENCE-ID>/<generation>-<rev12>-<observedAt>.output.txt`
     (stdout followed by stderr), and
   - `<store>/validations/<ID>/<EVIDENCE-ID>/<generation>-<rev12>-<observedAt>.json`,
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
   `ValidatedEvidence.establish`** (non-attested mode) **or
   `ValidatedEvidence.attest`** (attested mode, below). `ValidatedEvidence`
   (`src/Praxis.Domain/Work/VerificationReceipt.fs`) is a sealed type with a
   private constructor; `establish` returns
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

**Remaining risk in non-attested mode.** The event log and result files are
repository state. A principal with write access to that state can still
forge a result document *and* a matching `work.validation.observed` event
(REL-001 case C3b); Praxis detects inconsistency between them, not
collusion. Attested mode closes this.

## Attested evidence and the trusted verifier (PRAXIS-REL-009, F1)

In attested mode the only completion evidence is a **verification
receipt** (`echelon.verification-receipt/1`, I4) signed by a verifier whose
key the agent cannot use. A validation result, a forged event, or any file
the agent can write is refused (`PRX.COMPLETION.RECEIPT_MISSING`).

**Verifier engine** (`TrustedVerifierEngine`, shared by `serve` and `run`):

1. Re-reads the claim from the repository's local claim store and requires
   the stated claimant and generation to be the current, unexpired holder
   (`PRX.VERIFIER.STALE_CLAIM`). The request never carries the claim token.
2. `git clone --no-hardlinks` into its own temporary directory and a detached
   checkout of the commit (`PRX.VERIFIER.COMMIT_UNAVAILABLE`).
3. Checks every protected path of the host-owned policy (I5) at that commit:
   it must be a regular tracked file whose SHA-256 equals the pin. Otherwise
   it runs **nothing** and signs one `unavailable` receipt per validation,
   with `PRX.VERIFIER.PROTECTED_PATH` diagnostics in the response.
4. Runs each policy validation in the clone, as an argument list (no shell)
   with its timeout, and signs one receipt per validation: DSSE v1 PAE,
   ECDSA P-256 / SHA-256, IEEE-P1363 (64-byte) signature, BCL only.

**Host, long-running:** `praxis verifier serve` generates the key at start-up
(or imports `--key-file` PEM), holds it only in memory, reads the policy
once, prints one line `{ "keyId", "publicKey", "policyDigest" }`, and serves
newline-delimited JSON on a Unix domain socket, one request per connection.
The host copies `keyId`/`publicKey` into `TrustedVerifierKeys` and
`policyDigest` into `ValidationPolicyDigest` of the authority.
**One-shot:** `praxis verifier run … --key-file PEM --out FILE` is the same
engine (CI, with the key from a secret).

**Agent client:** `praxis work verify --id ID --token T` derives the
claimant and generation from the claim store and the token, refuses a dirty
worktree (`PRX.VALIDATION.DIRTY_WORKTREE`, `git status --porcelain`, untracked
files included), sends `{ repoPath, workItemId, claimant, fencingGeneration,
commit = HEAD }` to `PRAXIS_VERIFIER_SOCKET`, refuses a response bound to
anything else (`PRX.VERIFIER.RESPONSE_MISMATCH`), stores the envelopes in
`<store>/receipts/<ID>/`, and prints the receipt digests, statuses and
durations (exit 0 when every receipt passed, 3 otherwise).

**Completion in attested mode.** `ValidatedEvidence.attest` is the only
constructor of attested evidence. It checks, in order, and maps each failure
to its own code (the Dokimos code in brackets):

| Code | When |
| --- | --- |
| `PRX.COMPLETION.RECEIPT_MISSING` [`EVIDENCE_UNATTESTED`] | the evidence is not a DSSE envelope (a validation result, a plain file) |
| `PRX.COMPLETION.RECEIPT_MALFORMED` [`ATTESTATION_MALFORMED`] | the envelope, its `payloadType`, or the (signed) payload cannot be parsed |
| `PRX.COMPLETION.RECEIPT_UNTRUSTED_KEY` [`ATTESTATION_UNTRUSTED_KEY`] | no signature names a trusted `KeyId` |
| `PRX.COMPLETION.RECEIPT_INVALID_SIGNATURE` [`ATTESTATION_INVALID_SIGNATURE`] | no signature verifies under the trusted key it names (tampered payload, other key) |
| `PRX.COMPLETION.RECEIPT_BINDING_MISMATCH` [`ATTESTATION_BINDING_MISMATCH`] | `workItemId`, `claimant`, `fencingGeneration`, `commit`, `ordoContractDigest`, `validationPolicyDigest`, `validation`, `evidenceId` or `verifier.keyId` differ from the authority, claim or citation; or the authority pins no `ValidationPolicyDigest` |
| `PRX.COMPLETION.RECEIPT_NOT_PASSED` [`ATTESTATION_NOT_PASSED`] | `status` is `failed` or `unavailable` |
| `PRX.COMPLETION.RECEIPT_STALE` [`ATTESTATION_STALE`] | `observedAt` is outside `[ClaimAcquiredAt, LeaseExpiresAt)` or after evaluation |

The payload is only read after its signature verified, over the exact
payload bytes (never re-serialized). Signed members beyond I4 are tolerated
and bind nothing, as in Dokimos 0.7.0. Praxis also binds `claimant` and
`verifier.keyId`, which Dokimos does not. The observation `Digest` is the
receipt identity (`sha256:` over the payload bytes); the authority carries
each receipt as the observation's `Attestation`, with `ObservedAt` and
`Validation` from the receipt, plus `TrustedVerifierKeys` and
`ValidationPolicyDigest`. Dokimos re-verifies everything independently.
Attested mode needs a Dokimos that provides `completion.verify-attested/1`
(0.7.0 or later): Praxis probes `dokimos version` first and otherwise refuses
with `PRX.COMPLETION.VERIFIER_ATTESTATION_UNSUPPORTED` rather than send an
authority Dokimos 0.6.0 would reject or ignore.

**Trust boundary.** Receipts are exactly as trustworthy as the place the
signing key lives and the policy and authority they are checked against.

- The production trust root is a key available **only to the host or CI**:
  the in-memory key of a `verifier serve` process the agent does not control,
  or a GitHub Actions secret (or an environment restricted to the protected
  default branch) read by `verifier run --key-file`. Never give an
  implementation agent the key file, the secret, or write access to the
  verifier process.
- The validation policy and the completion authority (trusted keys, policy
  digest, contract pin) come **only from the protected default branch** or
  the host, never from the candidate tree or a branch the agent can push.
  The policy is read from the host and pinned by digest; protected paths pin
  the oracle (acceptance tests) so the agent cannot weaken it.
- The verifier runs the candidate's code (that is what a test run is). Run it
  as a separate OS user, in a container or a CI job, so candidate code cannot
  read the key from the verifier's memory or environment.

**Limits, stated honestly.**

- A root-privileged process on the verifier host (or the same OS user as the
  verifier) can read the private key from process memory and sign anything.
  Attested mode moves trust from "anyone with repository write access" to
  "whoever controls the verifier host or CI secret"; it does not remove it.
- `verify` re-reads claims from the local file store only; `--store git`
  claims are not yet re-read by the verifier.
- The socket path's permissions are the host's to restrict; Praxis does not
  authenticate the caller. Any local caller can ask for receipts, but a
  receipt binds the current holder, generation and commit, so it only helps
  the actual holder.
- Validations inherit the verifier's environment; keep secrets out of it.

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

## Clean completion identity (PRAXIS-REL-011, F3)

`complete-verified` runs, in this order:

1. Requires HEAD to equal every cited receipt's (or result's) commit
   (`PRX.COMPLETION.HEAD_MISMATCH`) and `git status --porcelain` to be empty,
   untracked files included (`PRX.COMPLETION.DIRTY_WORKTREE`). A source change
   after the receipt, committed or not, therefore fails closed.
2. Verifies with Dokimos (the gate above).
3. Appends the canonical tracked `work.completion.verified` event to
   `.ros/events/events.jsonl`, carrying `verifiedCommit`, `receiptDigests`
   and `packetDigest` (never the claim token).
4. Makes a **metadata-only commit** whose parent is the verified commit and
   which touches only `.ros/events/events.jsonl` (checked after the commit).
5. Outputs `verifiedCommit` and `metadataCommit`; the worktree ends clean.
6. If the commit cannot be made, restores the tracked files and reports
   `PRX.COMPLETION.METADATA_COMMIT_FAILED`. The claim is already completed,
   so running the same command again replays without re-verifying and
   retries the publication (idempotent by event ID).

`praxis work completion-identity --id ID` checks later that
`git diff --name-only verifiedCommit..HEAD` touches only `.ros/`
(`consistent`, exit 0; `PRX.COMPLETION.IDENTITY_VIOLATED`, exit 3, listing
the source paths).

## Derived completion (PRAXIS-REL-012, F4)

`praxis work complete-verified --id ID --token TOKEN` is the whole command
when `PRAXIS_COMPLETION_AUTHORITY` and `PRAXIS_ORDO_CONTRACT` are set:

- claimant and generation come from the claim store (the token must be the
  holder's);
- commit and verification revision from the selected evidence (which must
  equal HEAD);
- the authority from `PRAXIS_COMPLETION_AUTHORITY`, the contract from
  `PRAXIS_ORDO_CONTRACT` plus the authority's digest pin;
- requirement IDs from the authority, else the contract; decisions and
  conflict resolutions from the authority;
- validations and evidence from the latest receipt per evidence ID for this
  generation (attested mode), or the latest `validate-run` result per
  evidence ID (non-attested mode).

Explicit flags remain accepted, but each must equal the derived value
(`PRX.COMPLETION.ARGUMENT_MISMATCH`): `--claimant`, `--generation`,
`--commit`, `--verification-revision`, `--ordo-contract-version`,
`--ordo-contract-digest`, the `--requirement`, `--decision` and
`--conflict-resolution` sets, `--authority` / `--ordo-contract` against the
environment, and `--validation` (names and evidence IDs) against the latest
evidence. Explicit `--evidence` selects which stored results or receipts are
cited instead of the latest; each is still verified by the gate, and
`--validation` then describes them.

**Contract cache.** An admitted contract's canonical form is stored under
`<store>/cache/ordo/<digest-hex>.json`. When the authority pins a digest the
cache holds, admission uses that entry and never reads or admits the source
contract again (`admissionCache: "hit"`). The entry is content-addressed and
re-admitted under the same pin, so a tampered entry can only fail as stale;
REL-GATE-002 readiness is always recomputed.

The output reports `timings` in milliseconds for `claim`, `admission`,
`receipts`, `dokimos` and `metadataCommit`, and `admissionCache`.

## Ordo contract binding (PRX-REL-008)

`work complete-verified --ordo-contract FILE [--expected-ordo-digest D]` admits
the Ordo `ecir/1` contract (`praxis ordo contract check` semantics) and binds
it to the authority: version and digest come from admission, a conflicting
pinned version or digest is `PRX.COMPLETION.STALE_ORDO_CONTRACT`, a required
requirement/decision/conflict-resolution ID absent from the contract is
`PRX.COMPLETION.UNKNOWN_CONTRACT_REFERENCE`, and an authority naming no
required requirements inherits the contract's. Admission failures carry
Ordo's own `ORDO.CONTRACT.*` codes. Nothing reaches Dokimos on refusal.

### Contract readiness (REL-GATE-002)

`--ordo-contract` is required: without the contract no readiness can be
established, so a declared identity alone is refused
(`PRX.COMPLETION.CONTRACT_NOT_READY`). Admission refuses a `modeled`
requirement that links a `conflict` node (`ORDO.CONTRACT.UNRESOLVED_CONFLICT`):
ecir/1 carries no conflict resolution, so contradictory decisions recorded as a
conflict are open. Binding then requires execution readiness, mirroring Ordo's
`ConstructibilityContract.assessReadiness`, for the requirements the completion
executes: the required ones, less those the contract defers, rejects or
supersedes (they stay in the packet for lossless accounting). A non-modeled
requirement reached in scope (`ORDO.CONTRACT.BLOCKED_REQUIREMENT`), a reached
conflict (`UNRESOLVED_CONFLICT`), a decision or standalone rationale in scope
that is not authorized (`MISSING_DECISION_AUTHORIZATION`,
`MISSING_RATIONALE_AUTHORIZATION`), an authorization outside the scope
(`AUTHORIZATION_OUTSIDE_SCOPE`) or an empty executed scope (`EMPTY_SCOPE`) is
refused with `PRX.COMPLETION.CONTRACT_NOT_READY` followed by the Ordo codes.

The authorized decision set is the authority file's `RequiredDecisionIds`. It
comes from the host's authority, never from the agent's packet: citing a
decision with `--decision` does not authorize it. Dokimos still requires the
packet to cite every authorized decision. Success is a
`BoundCompletionAuthority`, a type only an Ordo `ReadyScope` can construct;
the packet and authority builders accept nothing else, so an unready contract
identity cannot reach Dokimos.

`praxis ordo contract check --contract FILE --requirement KEY... [--authorized-decision ID]...`
reports the same readiness before any agent work (`status: ready`, exit 0;
refused, exit 1). Without `--requirement` it is admission only.

## Codes

`PRX.CLAIM.HELD`, `STALE_REVISION`, `STALE_GENERATION`, `STALE_TOKEN`,
`EXPIRED`, `NOT_CLAIMABLE`, `NOT_HELD`, `CONFLICT`, `INVALID_REQUEST`.
`PRX.COMPLETION.INVALID_COMMIT`, `INVALID_VERIFICATION_REVISION`,
`EVIDENCE_MISSING`, `EVIDENCE_UNREADABLE`, `EVIDENCE_UNRECOGNIZED`,
`EVIDENCE_UNRECORDED`, `EVIDENCE_FOREIGN_CLAIM`, `EVIDENCE_REVISION_MISMATCH`,
`EVIDENCE_NOT_PASSED`, `EVIDENCE_STALE`, `EVIDENCE_VALIDATION_MISMATCH`,
`VALIDATION_UNPROVEN`, `AUTHORITY_UNAVAILABLE`, `CONTRACT_NOT_READY`,
`STALE_ORDO_CONTRACT`, `UNKNOWN_CONTRACT_REFERENCE`,
`VERIFIER_UNAVAILABLE`, `VERIFIER_TIMEOUT`, `VERIFIER_FAILED`,
`VERIFIER_INPUT_REJECTED`, `RECEIPT_UNPARSABLE`, `RECEIPT_MISMATCH`,
`VERIFICATION_REJECTED` (followed by the receipt's `DOK.COMPLETION.*` codes
verbatim), `CONFLICT`, `RECEIPT_MISSING`, `RECEIPT_UNTRUSTED_KEY`,
`RECEIPT_INVALID_SIGNATURE`, `RECEIPT_MALFORMED`, `RECEIPT_BINDING_MISMATCH`,
`RECEIPT_NOT_PASSED`, `RECEIPT_STALE`, `VERIFIER_ATTESTATION_UNSUPPORTED`,
`ARGUMENT_MISMATCH`, `DIRTY_WORKTREE`, `HEAD_MISMATCH`,
`METADATA_COMMIT_FAILED`, `IDENTITY_VIOLATED`, `NOT_COMPLETED`.
`PRX.VALIDATION.DIRTY_WORKTREE` (`validate-run`, `verify`).
`PRX.CLAIM.LEGACY_STORE`. `PRX.VERIFIER.STALE_CLAIM`, `INVALID_REQUEST`,
`COMMIT_UNAVAILABLE`, `UNAVAILABLE`, `RESPONSE_MISMATCH`, `PROTECTED_PATH`.
Each refusal says how to refresh and replan.

## Events (I1)

Appended to the out-of-tree `<store>/events.jsonl` (I8) with the standard
work-event envelope (the tracked `.ros/events/events.jsonl` receives only the
canonical completion event of each metadata-only commit);
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
| 12 | C3b: forged result plus forged validation event (attested mode) | `RECEIPT_MISSING`; derived evidence finds no receipt (`EVIDENCE_MISSING`); nothing reaches Dokimos | `attested completion: C3b reproduced ...` |
| 13 | Self-signed receipt; receipt claiming a trusted keyid | `RECEIPT_UNTRUSTED_KEY`; `RECEIPT_INVALID_SIGNATURE` | `attested completion: a self-signed receipt ...` |
| 14 | Tampered payload or malformed envelope | `RECEIPT_INVALID_SIGNATURE`; `RECEIPT_MALFORMED` | `attested completion: a tampered payload ...` |
| 15 | Receipt for another item, generation, commit, contract, policy, validation or evidence ID; stale | `RECEIPT_BINDING_MISMATCH`; `RECEIPT_STALE`; `RECEIPT_NOT_PASSED` | `attested completion: a genuine receipt for another ...` |
| 16 | Genuine failing receipt; protected-path change | `RECEIPT_NOT_PASSED`; `unavailable` receipts, nothing run | `attested completion: a genuine failing receipt ...`, `... protected-path change ...` |
| 17 | Genuine passing receipt | Completed; worktree clean; HEAD^ = `verifiedCommit`; diff limited to `.ros/`; replay idempotent; cache hit | `attested completion: a genuine passing receipt ...`; real Dokimos 0.7.0: `attested completion: end to end ...` |
| 18 | Dirty worktree, moved HEAD, source change after the receipt | `DIRTY_WORKTREE`, `HEAD_MISMATCH` | `attested completion: a dirty worktree ...` |
| 19 | Receipt replayed after re-claim; stale holder at the verifier | `RECEIPT_BINDING_MISMATCH` + `RECEIPT_STALE`; `PRX.VERIFIER.STALE_CLAIM` | `attested completion: a receipt replayed after re-claim ...` |
| 20 | `complete-verified --id --token` alone | Verified, metadata commit, `timings` | `attested completion: F4 end to end ...` |
