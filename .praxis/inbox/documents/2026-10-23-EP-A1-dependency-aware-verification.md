# EP-A1: Dependency-aware verification selection, in shadow mode first

Priority: P2
Owner system: Praxis (work scheduling, evidence coordination). Dokimos remains the definer of what a check result means.
Target repositories: praxis
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Program: kemiller2002/cohort-based-engineering `program/IMPLEMENTATION-PLAN.md` A1; `program/CONTRACTS.md` C5; experiment EXP-2 (cohort-based-engineering inbox).

## Finding

- **Nothing selects verification from what changed.** Only risk-declared verification dimensions exist
  (`praxis.work-risk/1`, PRX-QUAL-020..022). Worker checklists use a host-selected validator map with no impact
  analysis.
- **The cost of running everything is high.** CI runs the full ~23–29 minute workflow on every push.
- **No existing requirement covers it.** No RQ, DF or intake specifies change-based selection: `program/CURRENT-STATE.md`
  A1 is status 5.

## Requirements (proposed)

- **EP-A1-R1.** A per-repository `praxis.verification-map/0.1`, parsed strictly. It maps path globs, interface
  identifiers and dependency closures to required checks and a risk class. Its non-empty `mandatory` set (security,
  data integrity, authorization boundary, release acceptance) can never be deselected.
- **EP-A1-R2.** `praxis verify plan --base REV --head REV` emits the selected checks, each with its reason: the
  triggering path, interface or dependency; the risk class; and any prior valid evidence (EP-A2b). Output is
  deterministic.
- **EP-A1-R3.** Shadow mode only. The plan is recorded next to the checks actually run, and nothing is skipped.
- **EP-A1-R4.** Moving to enforcing mode requires EXP-2's preregistered criteria (≥ 30% fewer CI minutes with 0
  escaped failures on replay) and owner approval.

## Acceptance evidence

- Map parser tests, including that the mandatory set cannot be deselected.
- Golden plans for representative diffs.
- Shadow records produced for at least 50 historical commits, for EXP-2.
