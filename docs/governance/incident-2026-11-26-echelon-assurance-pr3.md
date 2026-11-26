---
id: GOV-INC-2026-001
title: echelon-assurance PR #3 reached main without verified completion — root-cause analysis
status: final (investigation is read-only; the assurance repository was not modified)
created: 2026-11-26
work_item: WI-0117
---

# Incident analysis: echelon-assurance PR #3 (2026-11-26)

## Summary

The assurance completion report said verified completion was refused (no completion authority) and that
merging was blocked. Minutes later the consolidated documents were on `main`. The facts below show the
merge was performed through GitHub's merge button by the repository owner's account, on a branch with **no
platform protection**, with no verified completion and no exception record that existed at merge time.
Praxis represented the work item correctly (it stayed `active`); what failed is that nothing in Praxis or
on the platform **enforced** or **detected** the governance requirement. The owner may legitimately merge
their own repository; this analysis does not find misconduct, it finds that the governance requirement
was a statement in Markdown, not a control.

## Evidence (all read-only)

| # | Fact | Source |
|---|---|---|
| E1 | PR #3 merged at 2026-11-26T12:26:31Z; `merged_by: kemiller2002`; merge commit `9ab2b00` has committer `GitHub` (the web merge button or API) and author = owner account | GitHub API `pulls/3`; `git log --format='%cn'` |
| E2 | Head merged: `2c2c4f3`, pushed 12:24:34Z; the two CI checks named `validate` completed 12:24:51Z and 12:25:24Z (success) | GitHub API `pulls/3` check runs |
| E3 | Zero reviews on PR #3 | GitHub API `pulls/3/reviews` |
| E4 | `main` is not protected and has no rulesets (also true for praxis, daf-landscape, unbound, echelon-due-diligence and the Echelon framework repository) | GitHub API `branches/main`, `rulesets`, 2026-11-26 |
| E5 | On `main` at `9ab2b00`, `.ros/context/current.json` has `WI-ASR-FINAL` in state `active`; `.ros/events/events.jsonl` has zero `work.completion.verified` events | repository at `9ab2b00` |
| E6 | The assurance `ros.json`/`.echelon/ros.json` set neither `requireVerifiedCompletion`, `requireAttestedEvidence` nor `trustedVerifierKeyIds` | repository at `9ab2b00` |
| E7 | The completion report and `context/CURRENT-STATE.md` on `main` say "merge is blocked" / "merge subject to verified completion" | repository at `9ab2b00` |
| E8 | After the merge, commits continued on the PR branch (`2cff6f5` 12:29:39Z, `6f53593` 12:31:08Z "five material fixes after merge", rounds 54–57); none is on `main`. Draft PR #4 now carries them | `git log 2c2c4f3..origin/claude/echelon-assurance-consolidation-jygy23`; GitHub API `pulls/4` |
| E9 | `DF-ASR-2026-0D12` (commit `70f5163`, 12:59:05Z, authored by the implementing agent) records that PR #3 "was merged into main on the owner's direct instruction" and that the owner, asked in conversation, answered "you have the authority"; under that delegation the agent accepted plain completion for WI-ASR-FINAL and states it is never described as verified | branch `claude/echelon-assurance-consolidation-jygy23`, `docs/decisions/DF-ASR-2026-0D12--…` |
| E10 | Owner records `DF-ASR-2026-0D10` ("do not merge incomplete work", applicable Praxis verified completion) and `DF-ASR-2026-0D11` ("validate exact final commit under Praxis before merge") predate the merge | repository at `9ab2b00` |
| E11 | `praxis governance audit --repository kemiller2002/echelon-assurance --branch origin/main` (this work) reports `AUD.UNVERIFIED_INTEGRATION`, `AUD.INTEGRATED_WHILE_ACTIVE` (WI-ASR-FINAL) and `AUD.STRANDED_AFTER_MERGE` (8 commits at the time of the run) for `9ab2b00` | command output, 2026-11-26 |

E9 is an agent-authored account of a conversation; it is not platform evidence of the owner's words. It is
consistent with E1 (the owner's account performed the merge).

## Answers to the investigation questions

1. **How did the changes reach `main`?** Through a GitHub merge of PR #3 performed by the owner's account
   (E1), not a direct push. Nothing on the platform required a check, a review or verified completion (E4).
2. **Was verified completion subsequently achieved?** No. No `work.completion.verified` event exists (E5),
   and the later record E9 explicitly accepts *plain* completion instead.
3. **Did an authorized exception exist?** Not at merge time. E9 was written ~33 minutes after the merge, by
   the implementing agent, relaying a conversational delegation. It is recorded honestly as plain completion,
   but it is an implementing agent applying a material exception to its own work on a relayed instruction;
   Praxis had no mechanism for an authenticated, scoped, pre-merge exception.
4. **Did a merge or push bypass intended controls?** It bypassed the *documented* requirement (E10). It did
   not bypass any *technical* control, because none existed (E4, E6): CI checks were advisory, and Praxis's
   verified-completion flags were not enabled.
5. **Did Praxis misrepresent the work-item state?** No. WI-ASR-FINAL stayed `active` (E5), which was true.
   Praxis's gap was the absence of any check that relates integrated history to work-item and verification
   state.
6. **Did documentation become stale?** Yes. `main` states the merge is blocked (E7) while containing the
   merge; the PR body's question counts (96 open) also disagreed with the completion report (25 open);
   material fixes made after the merge (E8) were not on `main`.
7. **Is the same failure possible elsewhere?** Yes: every Echelon repository checked has an unprotected
   default branch (E4), and no repository had a completion authority provisioned.

## Root causes

- **RC1 — Requirement without mechanism.** "Merge only with verified completion" existed in owner records
  and Markdown. No platform rule (required check, required pull request, no bypass) or Praxis gate enforced
  it, so the requirement depended entirely on every actor's memory at merge time.
- **RC2 — No provisioned verifier.** The only verified-completion path required a host-held key and an
  authority file outside the agent's reach. In a cloud-agent, mobile-owner setting there was no such host,
  so verified completion was impossible and the "secure path" was not an available path.
- **RC3 — No detection.** Nothing compared integrated history with work-item and verification state, so the
  divergence (active work item on `main`, report saying "blocked") was visible only to a reader who looked.
- **RC4 — Exceptions had no authenticated channel.** The only way to accept plain completion was a
  conversational instruction recorded afterwards by the implementing agent.
- **Contributing — Agents act as the owner on the platform.** The PR author is the owner's account, so the
  platform cannot distinguish an agent's action from the owner's, and the owner cannot "approve" the PR.

## Corrective actions (implemented in WI-0117)

| Root cause | Mechanism | Command / artifact |
|---|---|---|
| RC1 | Platform governance inspection that reports *enforced* vs *required*, a readiness ladder, and drift | `praxis governance inspect`, `praxis governance drift` |
| RC1 | A merge gate that decides from protected-branch configuration and verified receipts, run from the protected branch and required by a ruleset | `praxis merge authorize`, `.github/workflows/praxis-merge-gate.yml`, `.praxis/governance/ruleset-main.json` |
| RC2 | A keyless completion authority: CI workload identity (GitHub OIDC) pinned to the protected workflow, provisioned by one owner merge | `praxis governance bootstrap`, `.praxis/trust/verifiers.json` |
| RC3 | Integration audit over the protected branch | `praxis governance audit` (this incident is test `governance #30`) |
| RC4 | Exceptions as scoped, expiring, non-self-approved records; elevated approval bound to an exact head and refused when posted through an application | `praxis.governance-exception/1`, `/praxis approve-elevated <sha>` |

Owner actions that remain (cannot be automated safely): merge the bootstrap pull request and import the
ruleset in each repository (see [bootstrap.md](bootstrap.md)); decide whether agents get a distinct GitHub
App identity (OQ-706).
