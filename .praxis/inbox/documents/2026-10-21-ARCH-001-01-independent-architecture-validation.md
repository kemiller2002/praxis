# ARCH-001-01: Independent architecture validation at completion, outside the agent loop

Priority: P2
Owner system: Praxis (host verifier policy); Dokimos (rule engine, see ARCH-001-02)
Intake: Praxis document ingestion
Evidence: kemiller2002/cohort-based-engineering `research/architectural-correctness/REPORT.md`, `RESULTS.json`, `BASELINE.md`, `ARCHITECTURE-RULES.md` (branch `claude/echelon-architectural-correctness-m4glhq`, PR #16).
Related: REL-GATE-003, REL-GATE-004 (attested completion), DOK-R008, SDE-DOCTRINE-003 v0.2.1.

## Finding
ARCH-001 ran 64 confirmatory trials: 8 tasks × 4 arms × 2 replicates, each with an initial and a follow-up phase, 128 requirements in all. Every arm reached 32/32 accepted requirements with **zero** compiler-detected architecture violations.

Arm D added in-loop architecture enforcement: `archcheck` during development plus a required gate in the completion command. Compared with arm C (Ordo contracts, no enforcement), it cost:
- +4.0% model cost (95% CI −0.3% to +8.0%, inside the preregistered ±10% equivalence margin);
- about +48% wall time (9.1 vs 6.2 minutes per trial);
- 6.4 verifier runs per trial.

It prevented nothing, because nothing was attempted. The same verifier, run independently against sealed per-task probes, caught all 7 planted solutions that are functionally correct but architecturally invalid.

No Echelon application runs a dependency-direction check today (BASELINE.md §2.1). The Praxis host verifier can already attest any argv validation bound to the exact commit.

## Requirement
1. Praxis supports a repository-declared architecture validation as one `validations[]` entry of the `echelon.validation-policy/1` host policy. The rule file and tier map are host-held or pinned through `protectedPaths`, so the validation runs under the existing attested, commit-bound completion path.
2. By default the validation is **not** injected into agent briefs or development loops. It runs at completion (host verifier) and may also run in CI.
3. When a repository declares a tier map but no architecture validation, `praxis validate` reports it as a warning.
4. The validation's receipt names the policy digest, the verifier digest, the commit and the tree hash.

## Acceptance criteria
- A completion attempt whose commit violates a deterministic architecture rule of severity ≥ 2 is refused with the rule id, file and line. A clean commit passes. Both are covered by tests using a sample repository.
- An agent cannot weaken the rule file: a change under `protectedPaths` refuses completion (PRX.COMPLETION.* codes).
- No architecture tool is added to agent briefs unless a repository opts in explicitly.
- The docs cite ARCH-001's limits: a ceiling on a small clean fixture, one model, n = 2 per cell, and in-loop enforcement with no measured benefit.
