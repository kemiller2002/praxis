# REL-GATE-003: Non-attested verified completion accepts forged store evidence

Priority: P1 (blocks rollout of non-attested verified completion; does not block attested mode)
Owner system: Praxis (Dokimos labelling optional)
Intake: Praxis document ingestion
Existing work items: related to PRAXIS-REL-009 (attested evidence, complete) and PRAXIS-REL-006 (rollout gate). Not a duplicate: those add attested mode; this concerns what non-attested mode claims and permits.
Experimental finding: kemiller2002/cohort-based-engineering `research/experiments/REL-001/gate/evidence/evidence-binding-repro-v3.17.0.jsonl` (case GATE-3) and `results/c/treatment-v3.17.0.jsonl` (case C3b)

## Observed failure
Released Praxis 3.17.0 with Dokimos 0.7.0, **without** trusted verifier keys, verifies a completion whose evidence is a forged
`praxis.validation-result/1` written to `<git-common-dir>/praxis/validations/` together with a forged
`work.validation.observed` event appended to `<git-common-dir>/praxis/events.jsonl` (exit 0). Both files are writable by the
implementation agent. In attested mode the same forgery is refused (`PRX.COMPLETION.RECEIPT_MISSING`), as are self-signed
and wrong-key receipts.

## Root cause
Non-attested evidence is produced and recorded by the same process the agent controls, so nothing distinguishes a genuine
record from a written one. This is inherent in non-attested mode. The defect is that the mode is not labelled, and that a
repository can require verified completion without requiring attestation.

## Expected behaviour
1. The completion record, the completion event, and the `complete-verified` output state whether each completion was
   `attested` or `unattested`.
2. `workProtocol.requireVerifiedCompletion: true` implies `requireAttestedEvidence: true`, unless the repository also sets an
   explicit, reviewed opt-out (`workProtocol.allowUnattestedEvidence: true`). The opt-out fails closed when malformed.
3. The documentation states that non-attested verified completion is not forgery-resistant against a repository writer.

## Acceptance criteria
- With `requireVerifiedCompletion` alone, an authority without `TrustedVerifierKeys` is refused
  (`PRX.COMPLETION.ATTESTATION_REQUIRED`).
- With the explicit opt-out, the GATE-3 forgery still completes but is recorded as `unattested`. A test asserts the label.
- Attested-mode behaviour is unchanged. The cohort REL-001 gate cases GATE-A1..A4 and C8a–d keep their results.
