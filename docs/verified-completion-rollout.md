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

Adoption is gradual and per repository: each repository opts in through its own `ros.json`.

Setting `requireVerifiedCompletion` alone requires attestation: an authority with no `TrustedVerifierKeys`
is refused with `PRX.COMPLETION.ATTESTATION_REQUIRED`. Without attestation, the evidence is produced by the
implementation agent itself, which experiment REL-001 case C3b and gate case GATE-3 showed can be forged.
`ros.json` lives on the protected default branch, so any of these flags can change only through a reviewed
change. No per-run file, environment variable or CLI flag can switch attestation off once the repository
requires it.

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
   - Policy and authority files come only from the protected branch.

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
- **Key in memory.** A root-privileged process on the verifier host can read the key from memory. The
  gate's trust-root item is the mitigation.
- **Claim re-read is not a trust root.** The verifier re-reads the claim through the agent's chosen store (local or `--store git` on the clone's remote) only to check holder and generation are current. The trust root remains the key, the policy and the authority.
- **Unauthenticated socket.** The verifier socket does not authenticate callers. That is acceptable because
  callers can only ask the verifier to run the host policy itself; they cannot supply results.
