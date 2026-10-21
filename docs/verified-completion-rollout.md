# Verified-completion rollout gate (PRAXIS-REL-006)

Mandatory verified completion is **not** enabled across Echelon repositories. This document defines the
gate a repository must pass before `workProtocol.requireVerifiedCompletion` is turned on, and the
controls that keep a later configuration change from silently weakening it.

## Controls (`ros.json` `workProtocol`)
| Flag | Effect | Fails closed when |
|---|---|---|
| `requireVerifiedCompletion` | Plain `work complete` is refused for every item unless `complete-verified` recorded a Dokimos-accepted completion. It **implies** `requireAttestedEvidence` unless `allowUnattestedEvidence` is also `true` (REL-GATE-003) | the value is not a boolean, or `ros.json` is unreadable |
| `requireAttestedEvidence` | `complete-verified` is refused (`PRX.COMPLETION.ATTESTATION_REQUIRED`) unless the host authority pins `TrustedVerifierKeys`, so only verifier-signed receipts can complete an item (F1) | same as above |
| `allowUnattestedEvidence` | The explicit, reviewed opt-out: with `requireVerifiedCompletion`, lets an authority without `TrustedVerifierKeys` complete items with unattested evidence. It never overrides `requireAttestedEvidence: true` | same as above; a malformed value refuses every `complete-verified` with `PRX.COMPLETION.ATTESTATION_REQUIRED` |
| `trustedVerifierKeyIds` | The trust root (REL-GATE-004): the verifier key IDs (`sha256:<hex>` over the key's SPKI, as `praxis verifier serve` prints them) that a completion authority may trust. When attestation is required, an authority is refused (`PRX.COMPLETION.UNTRUSTED_AUTHORITY`) unless this list is non-empty and contains every key the authority names. When present without a requirement, it still binds an attested authority | not an array of `sha256:` + 64 lowercase hex key IDs (`PRX.COMPLETION.ATTESTATION_REQUIRED`) |

Adoption is gradual and per repository: each repository opts in through its own `ros.json`.

Setting `requireVerifiedCompletion` alone requires attestation: an authority with no `TrustedVerifierKeys`
is refused with `PRX.COMPLETION.ATTESTATION_REQUIRED`. Without attestation, the evidence is produced by the
implementation agent itself, which experiment REL-001 case C3b and gate case GATE-3 showed can be forged.
## Protected policy and trust root (REL-GATE-004)

Praxis 3.17.0 read these flags from the `ros.json` in the worktree being completed and trusted whichever
authority file the caller named (`--authority`, `PRAXIS_COMPLETION_AUTHORITY`). A process that could run
`complete-verified` could therefore commit a `ros.json` without `requireAttestedEvidence` (cohort probe
PROBE-3), or sign a receipt with its own key and name an authority that trusts it (PROBE-4), and complete
as `verified` without any test run. Completion now decides against a protected source, and the trust root
comes from protected policy, not from the authority file.

**Protected source.** `complete-verified`, and the plain `work complete` guard for
`requireVerifiedCompletion`, compare the worktree's completion policy with:

1. `ros.json` at the merge base of `HEAD` and `refs/remotes/<remote>/<branch>`, where remote and branch
   are `workProtocol.upstreamSync.remote` and `.branch` (default `origin`/`main`, as `praxis sync` reads
   them). A change to the merged protected branch moves the base forward, so a reviewed change applies
   once it is merged.
2. When that branch does not exist locally, every readable committed version of `ros.json` in `HEAD`'s
   history. Each version binds: a policy once committed cannot be weakened by a later commit. Rotating the
   trust root or relaxing policy in such a repository needs a protected branch to compare with.

The worktree's policy applies only if it is no weaker than the protected source: no requirement removed,
`allowUnattestedEvidence` not added, no verifier key trusted that the protected policy does not trust, and
(against a merge base) the same `upstreamSync` remote and branch. Strengthening is allowed. Anything else
is refused with `PRX.COMPLETION.POLICY_CHANGED`. A protected `ros.json` that is malformed, or a `HEAD`
that shares no history with the protected branch or does not resolve, refuses `complete-verified` with
`PRX.COMPLETION.POLICY_UNAVAILABLE`. Outside Git the plain `work complete` guard keeps reading the
worktree policy alone, as before.

**Trust root.** When attestation is required (`requireAttestedEvidence`, or `requireVerifiedCompletion`
without the opt-out):

1. an authority with no `TrustedVerifierKeys` is refused with `PRX.COMPLETION.ATTESTATION_REQUIRED`
   (unchanged; PROBE-1);
2. an authority file that resolves, after following symbolic links, inside the worktree or the Git
   directory is refused with `PRX.COMPLETION.UNTRUSTED_AUTHORITY`, because the completing process can write
   it;
3. the effective policy must pin `trustedVerifierKeyIds`, and every key the authority trusts must be in
   it; otherwise `PRX.COMPLETION.UNTRUSTED_AUTHORITY`. With attestation required and no pin, no key is
   trusted, so adding a pin later is itself a policy change: introduce the pin together with the
   requirement, or merge it to the protected branch first.

The pin is a key ID, not the authority file's digest, because the authority also carries per-contract
requirements that change between runs while the verifier key does not. Key IDs are the SHA-256 of the
public key's SPKI and are re-checked against the key whenever a signature is verified, so a key cannot be
substituted under a pinned ID (gate case GATE-A2B).

**Audit.** An attested completion records the verifier key IDs that signed its receipts as
`verifierKeyIds` on the claim record's `completion`, on the `work.completion.verified` event and in the
`complete-verified` output, so a post-hoc audit can compare them with the host verifier's key. An
unattested completion, or a record written before this field, has an empty list, and its event omits the
member so that its content-derived `eventId` is unchanged on replay.

**Migration.** A repository that already sets `requireAttestedEvidence` or `requireVerifiedCompletion`
must add `trustedVerifierKeyIds` with its host verifier's key ID through a reviewed change before its next
attested completion; until then attested completion fails closed with `PRX.COMPLETION.UNTRUSTED_AUTHORITY`.

**What this does and does not stop.** Attested mode resists forgery only with a host-controlled trust root:
the verifier key held outside the agent's reach, and the pin on a protected branch. Praxis reads the
protected branch from the local repository. A process that can rewrite that local ref or the history it
points to (for example `git update-ref refs/remotes/origin/main`, a rewritten root commit, or pushing to
the protected branch) controls the protected source too. Where the completing process is agent-controlled,
the host must also check, after the fact, that the completion's `verifierKeyIds` match the host verifier's
key, that the authority path the process used is the host's, and that the agent's commits do not change
`ros.json`.

## Attested and unattested completion (REL-GATE-003)

Every verified completion is labelled with `attestation`: `attested` when the host authority pinned
`TrustedVerifierKeys` and every cited evidence item is a verifier-signed receipt, otherwise `unattested`.
The label appears on the claim record's `completion`, on the tracked `work.completion.verified` event, and
in the `complete-verified` output. A claim record written before the label existed reads as `unattested`,
because nothing shows it was attested.

**Non-attested verified completion is not forgery-resistant against anyone with write access to the
repository.** Unattested evidence (`praxis.validation-result/1` documents under
`<git-common-dir>/praxis/validations/` and their `work.validation.observed` events) is written by the same
process the implementation agent controls. Anyone who can write to the repository, its Git directory
included, can write a passing result and the event that records it, and Praxis cannot tell it from a
genuine one. In Praxis 3.17.0 with Dokimos 0.7.0 such a forgery verified (GATE-3); it still completes under
`allowUnattestedEvidence`, and is recorded as `unattested`. An `unattested` completion therefore shows only
that the recorded evidence was internally consistent, not that the validations ran. Treat
`allowUnattestedEvidence` as a statement that the repository accepts this, and record the reason in the
change that sets it.

## Gate (all must hold, with evidence linked in the enabling change)
1. **F1 attested evidence.**
   - The C3b reproduction is refused in attested mode, by Praxis and by Dokimos.
   - A genuine passing receipt verifies; a genuine failing receipt blocks completion.
   - Evidence: `tests/Praxis.Tests/AttestedCompletionTests.fs`, Dokimos `CompletionAttestation*` tests, and
     cohort REL-001 experiment C on the released binaries.
2. **F2 attribution.** Multiple agents under one orchestrator are attributed reproducibly, with no
   double counting (`TelemetryAgentAttributionTests`, run-02 instrumentation).
3. **F3 identity.**
   - Completion references exactly the validated commit.
   - The worktree ends clean, and HEAD's diff from the verified commit touches only `.ros/`.
   - Dirty, moved-HEAD and stale cases fail closed.
4. **No regression** in REL-001 experiments A–C on the released binaries:
   - 0 duplicate active owners;
   - 0 invalid contracts accepted;
   - no invalid completion accepted beyond the documented threat-model residue, which must be listed.
5. **Representative applications.** End-to-end verified completion succeeds on at least one real Echelon
   application repository with its real test policy and a host-held verifier key, not only on fixtures.
6. **Run-02 evidence reviewed.** The REL-001 run-02 four-arm report has been read, and its recommendation
   on default workflow and adaptive routing is recorded in the enabling change.
7. **Trust root in place.**
   - The verifier key is held by the host or CI (a GitHub Actions secret or environment restricted to the
     protected branch), never on an implementation agent's machine.
   - Policy and authority files come only from the protected branch, and protected policy pins the
     verifier key (`trustedVerifierKeyIds`, REL-GATE-004).

## Compatibility
- Requires Praxis ≥ the release containing PRAXIS-REL-009/011/012, and Dokimos ≥ 0.7.0. In attested mode
  an older Dokimos fails closed with `PRX.COMPLETION.VERIFIER_ATTESTATION_UNSUPPORTED`.
- Claims, validations and receipts live under `<git-common-dir>/praxis/`. A claim only in the legacy
  `.ros/claims` store is refused (`PRX.CLAIM.LEGACY_STORE`) until `praxis work claims migrate` runs.
- Non-attested `validate-run` evidence keeps working in repositories that have not opted in.

## Rollback
1. Revert the enabling `ros.json` change through a reviewed PR. Both flags default to false, which
   restores plain completion. No state migration is needed: claims and receipts stay where they are.
2. Removing `requireAttestedEvidence` while keeping `requireVerifiedCompletion` changes nothing, because
   verified completion implies attestation. Setting `allowUnattestedEvidence: true` re-opens the C3b and
   GATE-3 path. It needs the same review as enabling, and the reason must be recorded in the change.
3. Pin the previous Praxis release in the repository toolchain if the new release itself must be withdrawn.
   Completed items remain completed; their metadata commits are ordinary history.

## Known limits
- **Unattested mode is forgeable by a repository writer.** See "Attested and unattested completion"
  above. Only `attested` completions resist a writer who forges store evidence.
- **The protected source is local.** See "Protected policy and trust root" above: a process that can
  rewrite the local protected ref or its history defeats it, so agent-controlled completions also need the
  host-side audit described there.
- **Key in memory.** A root-privileged process on the verifier host can read the key from memory. The
  gate's trust-root item is the mitigation.
- **Claim re-read is not a trust root.** The verifier re-reads the claim through the agent's chosen store (local or `--store git` on the clone's remote) only to check holder and generation are current. The trust root remains the key, the policy and the authority.
- **Unauthenticated socket.** The verifier socket does not authenticate callers. That is acceptable because
  callers can only ask the verifier to run the host policy itself; they cannot supply results.
