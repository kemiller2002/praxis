# REQ-PRES-001: Tamper-evident requirements preservation and authenticated ratification

Priority: P1
Owner system: Praxis (intake, requirements, completion)
Target repositories: praxis
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Related: praxis#292 (draft, branch `claude/requirements-preservation-gate`), Indy-init#12 (canonical baseline, draft).
Consolidation: kemiller2002/cohort-based-engineering `research/consolidation/2026-10-24/engineering-improvement-priorities.md` item 2.

## Evidence (observed, not controlled)

- **Defect DC-008.** In Rehearsal 3 an unsupported scope reduction during derivation dropped mandatory obligations,
  including the UI-016 palette and 29 P0 requirements. No gate compared the derivation with its source.
  - kemiller2002/indy-init-rehearsal-003 `research/requirements-preservation-root-cause.md`
  - `research/indy-init-requirements-reconciliation.md`
- **The draft gate can be bypassed.** The independent review of `praxis requirements preservation verify` (praxis#292)
  showed that each of the following passes the gate:
  - a self-written approvals file or review (`authority:"operator"` is not authenticated);
  - an edited baseline (the baseline's own sha256 is never checked);
  - a role swap of two palette values;
  - a negated restatement.
  No CI workflow runs the gate.
  - Source: R3 `docs/REHEARSAL-003-REMEDIATION-REPORT.md` §6; `research/rehearsal-003-independent-review.md`.
- **Agents ratify their own decisions.**
  - R3 decision DF-CAUSA-2026-0001 (read-only scope) was created by actor `claude-code-lead` with `status: accepted`.
    It excluded P0 write requirements (Indy-init `docs/REQUIREMENTS.md` EXT-DATA-006, EXT-DATA-009).
  - RQ-CAUSA-2026-0009/0010 `status: accepted` was likewise set by the agent (remediation §6).

## Requirements (proposed)

- **REQ-PRES-001-R1.** Every disposition records the baseline's sha256. The gate refuses when the baseline's bytes
  differ from the recorded digest, and when no digest is recorded.
- **REQ-PRES-001-R2.** An exclusion, a NOT_APPLICABLE judgement or a ratification (`status: accepted` on an RQ or DF
  record whose effect removes or weakens a P0 obligation) counts only when it carries an **authenticated** owner
  approval: a signature verifiable against a configured owner key, or a reference to an owner-authored Ordo decision
  record. An approval whose actor is an agent execution is refused with a distinct code.
- **REQ-PRES-001-R3.** Tokens extracted from an obligation are bound to the row and role they came from, so a role
  swap of two values fails.
- **REQ-PRES-001-R4.** A repository can declare the gate required. Praxis validation then runs it in CI on every
  change to the baseline, the derived requirements or the dispositions.
- **REQ-PRES-001-R5.** The gate reports negation and paraphrase as `needs-review`, never as preserved. Detecting
  negation is **not** claimed as a capability until tested.

## Acceptance criteria

- The R3 review's bypass scenarios, plus the role swap and the edited baseline, each fail with a distinct
  `PRX.PRESERVATION.*` code. The negated restatement yields `needs-review`.
- The original DC-008 package fails. The corrected R3 package fails only on items that need an owner decision.
- An agent-authored `status: accepted` on a DF record that excludes a P0 requirement is refused.
- The gate's output is byte-identical on rerun.

## Verification obligations

- Run against R3's evidence at `evidence/requirements-preservation/` and against the Indy-init#12 manifest.
- The independent adversarial check that found the original bypasses repeats against the new gate.

## Not claimed

- That this prevents determined bypass by an owner-key holder.
- Any effect on derivation quality. That is measured by NX-3 in the consolidation's `next-experiments.md`.
