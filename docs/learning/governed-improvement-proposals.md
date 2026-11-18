# Governed improvement proposals (PRX-LEARN-001)

Status: proposed (domain contract only; no CLI or storage yet).

A learning loop that turns failures into rules can make an autonomous system worse:
it can memorise one failure, regress earlier successes, or let an agent relax the
rules that constrain it. `Praxis.Domain.Learning.ImprovementProposal` makes each of
those a refusal rather than a convention.

## Contract

A proposal records: the originating failure(s) with evidence references, a root-cause
class, the corrective action, its target, the expected improvement, an estimated
execution-plus-maintenance cost, a rollback plan, the proposer's identity, and the
records it derives from (versioned provenance).

States: `Proposed -> Evaluated -> Approved -> Applied -> RolledBack`, with `Rejected`
carrying every reason. Transitions are pure functions returning `Result`.

| Rule | Code |
|---|---|
| Missing evidence, action, rollback or proposer | `PRX.LEARN.INCOMPLETE` |
| The original failure still fails, or was not re-run | `PRX.LEARN.COUNTERFACTUAL_FAILED` |
| A previously successful case fails, or none was re-run | `PRX.LEARN.REGRESSION` |
| No passing held-out case of the same root cause | `PRX.LEARN.GENERALIZATION_UNPROVEN` |
| A "held-out" case is one the proposal was written from | `PRX.LEARN.MEMORIZED` |
| The approver proposed the change | `PRX.LEARN.SELF_APPROVAL` |
| Protected governance approved by a non-human | `PRX.LEARN.HUMAN_APPROVAL_REQUIRED` |
| Transition from the wrong state | `PRX.LEARN.ILLEGAL_TRANSITION` |
| Approving a proposal other than the one evaluated (target, proposer or action changed) | `PRX.LEARN.PROPOSAL_CHANGED` |

Hardening (PRX-LEARN-002, from an independent adversarial review):
- Case ids and identities compare trimmed and case-insensitively, so `"d-t4 "` is the original case `"D-T4"`, and `" OWNER"` is `"owner"`.
- Every evaluation case and every listed failure evidence reference must be non-blank.
- **Initially every change needs a human approver**, prompts and skills included. `Unknown` and extension actor kinds can never approve.
- A "knowledge" change to agent-governing files (`AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, `ros.json`, `conditor.json`, `.github/`, `.claude/`, `.ros/`, `docs/00-governance/`) is classified as governance.
- States carry the evaluated proposal, so a proposal cannot be swapped after evaluation.

**Known limit.** Actor kinds are self-reported provenance, not authentication (see `docs/agent-provenance.md`). An agent that declares itself `human` under a different id passes `approve`. Binding approval to the owner-signed `RunAuthorization` (`Autonomy/Authorization.fs`) is the next step.

Protected governance reuses `Praxis.Domain.Autonomy.ProtectedClass` (verification
policy, security policy, execution policy, workflows, repository rules, credentials,
permission expansion, infrastructure, frozen requirements, acceptance criteria), so
autonomous execution and the learning loop agree on what an agent may not change.

## Not yet done

- Persistence (an append-only proposal log beside `.ros/events`), CLI verbs and
  provenance records for proposals.
- Running the counterfactual, held-out and regression cases: the contract judges
  results; producing them belongs to Dokimos/Ordo runners.
- Measuring whether accepted proposals reduce future failures (needs real history).
