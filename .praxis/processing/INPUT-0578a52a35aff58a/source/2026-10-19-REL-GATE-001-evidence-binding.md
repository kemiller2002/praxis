# REL-GATE-001: Completion evidence is not bound to a check result or revision

Priority: P0 (blocks the REL-001 experiment completion gate)
Owner system: Praxis
Intake: Praxis document ingestion
Existing work item: PRAXIS-REL-005 (captured) — derive into it; do not create a duplicate
Related: RQ-ROS-2026-A032 (PRX-REL-006, PRX-REL-007); Dokimos REL-DOKIMOS-001 freshness members (dokimos#45)
Experimental finding: kemiller2002/cohort-based-engineering research/experiments/REL-001/gate/gate-report.json (condition P4, D2)

## Observed failure
With the released Praxis 3.14.0 (59223f5) and Dokimos 0.6.0 (828208f), `praxis work complete-verified`
reaches `verified` (exit 0) for evidence that records a failing test run (`12 test(s); 7 passed; 5 failed`)
and for a file that is not a test report at all. Praxis builds the Dokimos authority by asserting that
every evidence file it can hash is a *passed* observation *at the verification revision*
(`FileWorkClaims.observeEvidence` + `CompletionGate.authority`), and it never sends the optional
freshness members Dokimos 0.6.0 supports (`ClaimAcquiredAt`, `LeaseExpiresAt`, `EvaluatedAt`,
`ObservedAt`) or per-observation `Validation` binding. Dokimos therefore verifies an assertion Praxis
did not establish: stale, failing or unrelated evidence completes work.

## Reproduction
`research/experiments/REL-001/gate/scripts/gate-evidence-binding.sh PRAXIS_BIN DOKIMOS_BIN ORDO_CONTRACT WORKDIR`
in cohort-based-engineering, run against the release assets (checksums verified):
both cases print `"outcome": "verified"` with exit 0.

## Expected behavior
Completion is accepted only when the host has independently established, for each required validation,
that a check ran at the verification revision and passed, within the claim's lease window.

## Acceptance criteria
- Evidence observations carry status, revision and observation time derived from a machine-readable,
  host-observed result (e.g. a test-result document naming the commit it ran at), never from file existence.
- A failing, unparsable, revision-mismatched or pre-claim (non-reusable) evidence document is refused with
  a stable `PRX.COMPLETION.*` or `DOK.COMPLETION.*` code; both reproduction cases above are refused.
- The authority sends `ClaimAcquiredAt`, `LeaseExpiresAt`, `EvaluatedAt`, per-observation `ObservedAt`
  and `Validation`; Dokimos 0.6.0 enforces them (`EVIDENCE_STALE`, `STALE_LEASE`, `EVIDENCE_VALIDATION_MISMATCH`).
- Regression tests reproduce both gate cases against the real Dokimos CLI.

## Dependencies
Dokimos 0.6.0 (released, already supports the members). No Ordo change.
