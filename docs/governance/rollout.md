---
id: GOV-ROLL-001
title: Cross-repository rollout of enforceable governance
status: proposed (no other repository was modified by WI-0117)
created: 2026-11-26
work_item: WI-0117
---

# Rollout plan

## Observed state (2026-11-26)

Read only for repositories this session was permitted to access. "Unknown" entries are discovered by the
first rollout step (`praxis governance inspect`), not assumed.

| Repository | Praxis pin | `main` protected | Rulesets | Completion authority | Notes |
|---|---|---|---|---|---|
| praxis | source (3.26.1) | no | none | none | Gate and trust root introduced by WI-0117, not yet merged |
| echelon-assurance | 3.25.0 | no | none | none | Incident GOV-INC-2026-001; PR #4 open |
| echelon-due-diligence | 3.25.0 | no | none | none | Has `praxis-validation.yml` |
| unbound | none pinned | no | none | none | |
| daf-landscape | none pinned | no | none | none | |
| Echelon-AI-Native-Enterprise-Design-Framework | none pinned | no | none | none | `ci.yml` only |
| dokimos, echelon-registry, echelon-organization-administration, strata, vigila, arca, conditor, fides, aegis | unknown | unknown | unknown | unknown | Outside this session's access; Dokimos provides attested verification (≥ 0.7.0) for the host path |

## Order (by dependency)

1. **Praxis** — merge WI-0117, cut the release (release workflow), then bootstrap Praxis itself. Praxis must
   be governed by its own released gate before others depend on it.
2. **Dokimos** — its verification is used by the host path; bootstrap next so changes to the verifier are
   themselves gated.
3. **echelon-assurance** — closes WI-ASR-03 and WI-PRAXIS-03 (CI workload path satisfies "pinned verifier
   keys": the pin is the workload identity); merge PR #4 through the gate.
4. **echelon-registry / echelon-organization-administration** — governance records; `authorities.json` can
   later be sourced from the role register (WI-EOA-01).
5. **Strata, Vigila, Arca, Conditor, Unbound**, then remaining active repositories.

## Per-repository procedure (agent steps unless marked)

1. Upgrade the pin to the release containing WI-0117 (`praxis upgrade`), validate, PR. *(Routine work.)*
2. `praxis governance inspect --repository OWNER/NAME --out .praxis/evidence/inspect-before.json` (baseline).
3. `praxis governance bootstrap --repository OWNER/NAME --approved-by kemiller2002 --validation NAME=COMMAND …`
   with the repository's real test commands; add `testCountPattern`/`minimumTests` to the generated policy for
   each test suite (e.g. Praxis: `(?<count>\d+) test\(s\);`). PR declaring `Praxis-Work-Item:`.
4. **Owner:** review and merge the bootstrap PR.
5. Open a trivial routine PR; confirm the gate runs and returns `verified`.
6. **Owner:** set workflow permissions to read-only and create the default-branch ruleset (bootstrap.md steps 4–5); `praxis governance inspect --out
   .praxis/evidence/inspect-after.json` should reach `verified-completion-enabled`; after the first verified
   integration, `praxis governance audit` should show it (`operationally-demonstrated`).
7. Optionally add a standing authorization for documentation and tests (elevated change).

## Migration risks

| Risk | Mitigation |
|---|---|
| Existing CI checks named generically (`validate`, twice in assurance) are not identifiable | The gate check has a unique name and is pinned to GitHub Actions |
| Gate refuses PRs that change workflows, build files or dependencies | Expected: those need `/praxis approve-elevated <sha>`; consider a dependency-update standing category only after review |
| Long test suites run on every PR update | The gate runs on `opened`, `synchronize`, `reopened`, `ready_for_review` (not `edited`); make it the only per-PR validation workflow; strict up-to-date re-runs open PRs after each merge |
| Workflows that push to `main` (Praxis `release.yml` bumps the version) are refused once the ruleset is active | Route the release bump through a pull request before importing the ruleset |
| Pull requests created by `GITHUB_TOKEN` (`praxis-remote.yml` pull-request mode, `praxis-reconcile-apply.yml`) do not start workflows, so the gate never runs | A person re-runs or closes/reopens them, or they move to an App identity |
| Private repositories on a free plan do not enforce rules | `inspect` reports `GOV-RULES` platform-unavailable; public repositories enforce at no cost |
| Issuer or API outage blocks merges | Exception procedure (bootstrap.md, emergency) |
| Agents authored as the owner cannot be approved on GitHub | Elevated approval uses the comment command, not reviews; a distinct agent App identity is recommended (OQ-706) |

## Expected evidence per repository

`inspect-before.json`, the merged bootstrap PR, the imported ruleset (visible in `inspect-after.json`), the
first `praxis-merge-decision` artifact, and an `audit` with one verified integration and no error findings.

## Human actions required (all repositories)

Per repository: merge one bootstrap PR and import one ruleset (≈ 5 minutes, possible from the GitHub mobile
web view). Once: decide on a distinct GitHub App identity for agents (OQ-706) and on moving repositories to an
organization account (audit log, push restrictions, required workflows, merge queue). Neither decision
involves a purchase by this plan.

## Instructions for the next agent

Start in Praxis: verify WI-0117 is merged and released (`release.json`), then follow the per-repository
procedure above, one repository per work item, in the order given. Never run `bootstrap` where `inspect` shows
a trust root; never merge a bootstrap PR yourself; never grant yourself elevated approval.
