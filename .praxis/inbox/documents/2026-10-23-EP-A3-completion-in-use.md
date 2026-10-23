# EP-A3: Put deterministic completion into use: release it and run the real-Dokimos path in CI

Priority: P1
Owner system: Praxis (completion)
Target repositories: praxis
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Program: kemiller2002/cohort-based-engineering `program/IMPLEMENTATION-PLAN.md` A3; `program/RELEASE-GATES.md` §5.

## Finding

- **The capability is built but not in use.** `work finish` (REL-TOKEN-003, `173cae20`) and `work complete-verified`
  verify evidence and either complete or refuse with about 45 stable `PRX.COMPLETION.*` codes.
- **Four things keep it out of use:**
  - `work finish` is unreleased (after 3.19.0);
  - the real-Dokimos end-to-end cases SKIP in CI because `PRAXIS_TEST_DOKIMOS_DLL` is unset;
  - verified completion is not enabled in any Echelon repository (PRAXIS-REL-006 captured);
  - the Registry's approved set still pins Praxis 3.16.0.
- **So the mission rule does not yet hold anywhere.** "Missing evidence must never produce successful completion"
  holds only where the policy is on.

## Requirements (proposed)

Only EP-A3-R2 and EP-A3-R3 are new. Release and enablement are existing captured items, PRAXIS-REL-003 and
PRAXIS-REL-006; do not duplicate them.

- **EP-A3-R1.** Release a version containing `work finish` and qualify it through Conditor and the Registry
  (PRAXIS-REL-003).
- **EP-A3-R2 (new).** CI provisions a pinned Dokimos release and sets `PRAXIS_TEST_DOKIMOS_DLL`, so the real-Dokimos
  completion cases run in the `validate` job instead of skipping. A SKIP of those cases fails the job.
- **EP-A3-R3 (new).** Idempotence contract tests, end to end through the CLI:
  - missing or stale evidence → exit 3, with identical refusal codes on repeat and no state change;
  - valid evidence completed twice → exit 0 replay, with no duplicate records.
- **EP-A3-R4.** Enable `requireVerifiedCompletion` in one pilot repository (owner decision OD-9) and record the first
  five completions' verdicts (PRAXIS-REL-006).

## Compatibility

Opt-in stays opt-in elsewhere. No change to existing refusal codes.
