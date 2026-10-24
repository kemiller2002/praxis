# ARCH-002-01: Four-tier architecture guidance in agent briefs by default

Priority: P2
Owner system: Praxis (work context and brief assembly)
Intake: Praxis document ingestion
Evidence: kemiller2002/cohort-based-engineering `research/architectural-correctness/arch-002/REPORT.md`, `RESULTS.json` (PR #17); ARCH-001 `research/architectural-correctness/REPORT.md`.
Related: ARCH-001-01 (independent architecture validation), ARCH-001-02 (Dokimos rules), SDE-DOCTRINE-003 v0.2.1.

## Finding
ARCH-002 was a preregistered trial with 279 scored trials on brownfield code and a real Summa snapshot, using two models.

- With requirements only, agents introduced new architecture violations in 29 of 68 trials:
  - Opus 12/36, Sonnet 17/32;
  - Summa 16/23, mostly JSON serialization written into Tier 1/2, copying nearby code.
- With SDE-DOCTRINE-003 and the repository's tier map in the brief, 0 of 71 trials introduced any. This is H1a/H1b,
  Supported after Holm correction: architectural acceptance rose 37 points [14, 57].
- Adding Ordo contracts or in-loop `archcheck` gating removed no further violations, because none remained.

Guidance costs about +22% raw model cost. Because failures stop being paid for, cost per accepted requirement fell from $0.556 to $0.437.

## Requirement
1. When a repository declares a project → tier map (the policy `tiers` block, or an equivalent Praxis-readable declaration), Praxis includes the following in the agent's work context or brief by default:
   - SDE-DOCTRINE-003, byte for byte, version-pinned;
   - the repository's instance document: tier map, allowed directions and placement rules, with no rules beyond the doctrine.
2. The included documents are recorded by digest on the work item's execution record.
3. A repository can opt out explicitly, and the opt-out is recorded.
4. This is guidance only. It does not replace the independent validation in ARCH-001-01, and it adds no in-loop gate.

## Acceptance criteria
- A repository with a declared tier map produces briefs or contexts containing both documents, and their digests appear in the execution record.
- A repository without a tier map is unaffected.
- The docs cite ARCH-002's scope:
  - the violations came from brownfield precedent (on clean code, ARCH-001 found none);
  - one model family;
  - Sonnet with guidance alone had more functional failures (60/70 requirements) than with contracts (66/70).
