# REL-GATE-002: Unready Ordo contracts reach Dokimos-verified completion

Priority: P0 (blocks the REL-001 experiment completion gate)
Owner system: Praxis (with Ordo ORDO-REL-003)
Intake: Praxis document ingestion
Existing work item: none matching (checked .ros/work/queue.json); create PRAXIS-REL-008
Related: RQ-ROS-2026-A032 (PRX-REL-008 Ordo contract binding); REL-GATE-001 (PRAXIS-REL-005); Ordo ecir-v1 negative test 5
Experimental finding: kemiller2002/cohort-based-engineering research/experiments/REL-001/results/b/treatment.jsonl (cases B5b, B6a)

## Observed failure
With Praxis 3.15.0 and Dokimos 0.6.0, `praxis work complete-verified --ordo-contract` reaches `verified`
(exit 0) for two invalid contracts (fixtures `research/experiments/REL-001/fixtures/b/`):
- B6a `contradictory-decisions-contract.json`: the `modeled` requirement `requirements/domain.md#R-001`
  links DEC-ALLOW-REOPEN, DEC-FORBID-REOPEN and a `conflict` node CONFLICT-REOPEN depending on both.
  Ordo `sde ecir check` and `praxis ordo contract check` admit it.
- B5b `unresolved-requirement-contract.json`: the requirement being completed has disposition
  `unresolved` with an open `conflict` node; it is completed and verified.

Root cause: Ordo `ConstructibilityContract.admitContract` and its Praxis mirror
(`src/Praxis.Domain/Ordo/ContractAdmission.fs`) check well-formedness only. Ordo's scoped execution
readiness (`ConstructionIr.validateCohort`: BlockedRequirement, MissingDecisionAuthorization) needs a
manifest and a cohort, and the completion path never applies any readiness rule.

## Reproduction
`research/experiments/REL-001/harness/b/run.sh treatment` in cohort-based-engineering: B5b and B6a
record `completeVerified.outcome = verified` and `invalidAccepted = 1`.

## Expected behavior
A completion is verified only when the requirements it executes are ready: modeled, reaching no
unresolved conflict, and with every decision in their scope authorized by the host authority (never
by the agent's packet). A contract whose modeled requirement links an unresolved conflict is refused
at admission.

## Acceptance criteria
- Ordo admission refuses a modeled requirement linking an unresolved conflict
  (`ORDO.CONTRACT.UNRESOLVED_CONFLICT`); Ordo gains manifest-free requirement-scope readiness
  (`assessReadiness`; `sde ecir check --requirement --authorized-decision`) and corpus cases.
- Praxis mirrors both byte-for-byte on the extended Ordo corpus.
- `OrdoBoundAuthority.bind` refuses unless the executed requirements are ready, with the authority's
  `RequiredDecisionIds` as the authorized set (`PRX.COMPLETION.CONTRACT_NOT_READY` plus the Ordo codes);
  it yields a value only a ready scope can construct, and the packet/authority builders require it.
- `praxis ordo contract check --requirement KEY --authorized-decision ID` reports readiness before work.
- B5b/B6a-equivalent cases are refused at Praxis level and end to end with the real Dokimos; a ready
  contract still verifies; the B harness re-run refuses B5b and B6a.

## Dependencies
Ordo ORDO-REL-003 (branch claude/echelon-reliability-integration-g8x7vn). Dokimos 0.6.0 unchanged.
