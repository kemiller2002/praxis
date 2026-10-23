# EP-A2b: Reuse completion evidence across commits only when its validity digests still match

Priority: P2
Owner system: Praxis (completion evidence)
Target repositories: praxis. Depends on dokimos EP-A2a (validity block).
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Program: kemiller2002/cohort-based-engineering `program/IMPLEMENTATION-PLAN.md` A2; `program/CONTRACTS.md` C3/C4; `program/RELEASE-GATES.md` §4.

## Finding

- **Today's rule is exact identity.** Evidence binds to the exact clean revision (`PRX.COMPLETION.EVIDENCE_STALE`,
  `src/Praxis.Domain/Work/ValidationResult.fs:24-63`), and checklists require exact closure identity (DF-ROS-2026-A067).
  Any commit invalidates all evidence, even when the closure a check reads is unchanged.
- **Dokimos is adding the needed digests.** EP-A2a adds digests over source closure, tool, configuration, SDK,
  dependencies and environment.

## Requirements (proposed)

- **EP-A2b-R1.** Completion may accept evidence produced at an earlier commit only when every validity digest,
  recomputed at the current HEAD over the evidence's declared closure, matches.
- **EP-A2b-R2.** Any mismatch refuses with `PRX.COMPLETION.EVIDENCE_STALE` and names the mismatched digest.
- **EP-A2b-R3.** Never-deferred gates are never satisfied by reused evidence: security, data integrity, authorization
  boundary and release acceptance.
- **EP-A2b-R4.** Evidence without a validity block keeps today's exact-revision rule.
- **EP-A2b-R5.** Telemetry records reused vs re-run evidence per completion (feeds EXP-2).

## Acceptance evidence

Tests for:
- reuse allowed when every digest matches;
- one refusal per mismatched digest;
- mandatory gates never reused;
- legacy evidence unchanged.
