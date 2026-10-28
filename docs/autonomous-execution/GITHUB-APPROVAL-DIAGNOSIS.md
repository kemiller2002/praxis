# GitHub approval diagnosis

Work item: WI-0104. Observed 2026-10-28 between 21:40 and 22:10 UTC.

## Method

Everything here comes from read-only GitHub REST calls made in this session, plus the repositories' own files. The calls were:

- `GET /repos/{r}`
- `GET /repos/{r}/branches/main`
- `GET /repos/{r}/branches/main/protection`
- `GET /repos/{r}/rulesets?includes_parents=true`
- `GET /repos/{r}/rules/branches/main`
- `GET /repos/{r}/contents/{CODEOWNERS,.github/CODEOWNERS,docs/CODEOWNERS}`
- `GET /repos/{r}/environments`
- `GET /repos/{r}/actions/permissions{,/workflow}`
- pull-request, review and event listings

The credential was the session's GitHub App user-to-server token (app `claude`) for user `kemiller2002`. GitHub reported `permissions.admin = true` on every repository.

`praxis autonomy diagnose --repository OWNER/REPO [--branch NAME]` repeats the configuration part of this diagnosis.

**Visibility limits.** The session's egress proxy refused these calls with HTTP 403:

- `environments` (deployment environment protection)
- `actions/permissions` and `actions/permissions/workflow`, including the "Allow GitHub Actions to create and approve pull requests" setting

Those properties are reported as **unknown**, not absent. Organization rulesets cannot apply, because every repository below is owned by a user account (`owner.type = User`), not an organization.

## Findings per repository

All nine repositories share the same GitHub configuration. Only the default-branch commit and the workflows differ, so each table states those two values and refers to the common table for the rest.

### Common GitHub configuration (all nine repositories)

| Property | Finding |
|---|---|
| Default branch | `main` |
| Branch protection | **None.** `GET …/branches/main/protection` returned 404 "Branch not protected" to an admin credential, and `branches/main` reports `protected: false`. |
| Rulesets | **None.** `rulesets?includes_parents=true` returned `[]`, and `rules/branches/main` returned `[]`. |
| Organization rulesets | Not applicable (user-owned repository). |
| CODEOWNERS | **None** at `CODEOWNERS`, `.github/CODEOWNERS` or `docs/CODEOWNERS` (404 for each). |
| Required reviews | **None.** There is no protection and no `pull_request` rule. |
| Required checks | **None enforced by GitHub.** Workflows run on pull requests, but no check is required: `required_status_checks.contexts = []` and `enforcement_level = off`. |
| Merge queue | **None.** It needs a ruleset, and there is none. |
| Auto-merge availability | **Disabled** (`allow_auto_merge: false`). Squash, merge-commit and rebase merges are all allowed. |
| Merge permissions | The agent's credential has **admin**. It can merge a pull request, push directly to `main` and push workflow files. A workflow file was pushed to a scratch branch at 21:53 UTC to prove the last point. It cannot create repositories (403 "Resource not accessible by integration"). The session proxy blocks branch deletion. |
| Deployment environment protection | **Unknown** (HTTP 403 through the session proxy). |
| Actions "create and approve pull requests" setting | **Unknown** (HTTP 403 through the session proxy). rehearsal-006's own workflow comment states that the owner enabled it there. |
| Current blocking condition | **No GitHub control blocks an unattended merge.** The approvals come from outside GitHub's rules (see "Where the approvals actually come from"). |
| Recommended correction | Do not loosen anything in GitHub; nothing there requires approval. Make unattended merging an explicit, enforced, owner-authorized path instead (decision package D-1 to D-4 below). |

### Per-repository facts

| Repository | `main` at | Workflows on `main` | Notes |
|---|---|---|---|
| kemiller2002/praxis | `398af6dd1509` | containment-qualification, deploy-pages, foundations-verify, native-release, praxis-reconcile-apply, praxis-reconcile, praxis-remote-inbox, praxis-remote, praxis-validation, release, ros-fs-assets, site | Recent PRs (#283 to #297) were all authored by `kemiller2002` (the agent's token) and merged with no review. |
| kemiller2002/conditor | `94308dd63cfc` | ci, praxis-validation, registry-lifecycle-proof, release-orchestrator, release, remote-bootstrap, remote-execute-codex | Same. The scaffold Conditor writes declares `requiredApprovingReviewCount = 0` (`Scaffolding.fs`), so new repositories are compatible with unattended merges. |
| kemiller2002/Indy-init | `8747179886c7` | offline-factory-rehearsal, preflight-tooling | #37's merge message: *"Merged by the agent on the owner's instruction ('I approve all recommendations and necessary merges going forward')"*. |
| kemiller2002/indy-init-rehearsal-006 | `881ca980ef66` | acceptance-process, build-and-test, echelon-foundations, **open-decision-pr**, praxis-validation | Owner decisions are read from approving reviews (`acceptance-process.yml`). #1 had to be replaced by #2, which `github-actions[bot]` opened, because the owner authored #1 and cannot approve their own pull request. |
| kemiller2002/indy-init-rehearsal-007 | `22b08266049b` | none | Empty (initial commit only). The integration test uses the scratch branch `praxis-autonomy-e2e/base`, not `main`. |
| kemiller2002/ordo | `1e8a95780813` | native-release, pages, praxis-validation, publish, release-trigger-guard, release, sde-distribution | Common configuration. |
| kemiller2002/dokimos | `2dab315ba10c` | ci, deploy-pages, ecir-fast-validation, praxis-validation, reconcile-icon-audit, release, site-validation | Common configuration. |
| kemiller2002/percepta | `c9747c263207` | echelon-bootstrap, limen-verify, percepta-core, percepta-experiment-0002, percepta-repository-lifecycle(-release), percepta-semantic-build/-cli/-release, praxis-validation | Common configuration. |
| kemiller2002/aegis | `2c28b6e20565` | build, dokimos, praxis-validation, publish-nuget | Common configuration. |

## Where the approvals actually come from

Each of the fourteen candidate sources from the brief was checked.

| # | Source | Finding | Classification |
|---|---|---|---|
| 1 | Branch protection | None on any repository. | not a cause |
| 2 | Repository rulesets | None. | not a cause |
| 3 | Organization rulesets | Not applicable (user accounts). | not a cause |
| 4 | CODEOWNERS | None. | not a cause |
| 5 | Required human reviews | None configured in GitHub. | not a cause |
| 6 | GitHub App permissions | Admin. The app can merge. | not a cause |
| 7 | **Agent execution instructions** | **Primary cause.** The cloud agent harness opens pull requests as drafts and treats hard-to-reverse or outward-facing actions (merging) as needing explicit user authorization, unless the user durably authorized them. So every merge waited for the owner. Evidence: Indy-init #37 was moved from draft to ready and then merged only after the owner's chat instruction, and its merge message records that instruction. | unnecessary workflow interruption for preauthorized routine work, as long as no durable, verifiable authorization exists |
| 8 | **Praxis and Indy governance** | **Contributing, and legitimate.** AGENTS.md: "Escalate high-impact irreversible … decisions" and "Do not treat your own proposed decisions as authorized". OD-INDY-7 and AR-003: agents never write approval files or interpret blanket approvals, and owner decisions must be authenticated. Without a verifiable, scoped run authorization, the only safe reading was to ask each time. | owner-imposed governance (keep it; satisfy it with a verifiable authorization instead of repeated asks) |
| 9 | CI configuration | CI runs, but GitHub does not *require* it. Nothing in CI asks for approval except rehearsal-006's `acceptance-process.yml`, which reads owner decisions from approving reviews. That is deliberate. | legitimate owner control (kickoff and acceptance decisions) |
| 10 | Deployment environment protection | Unknown (403). | visibility limit |
| 11 | Missing auto-merge configuration | Auto-merge is off. GitHub requires nothing, so it is not needed for a merge. It would only matter once required checks exist. | technical limitation (minor) |
| 12 | Missing merge permissions | No: the credential is admin. | not a cause |
| 13 | **Token identity restrictions** | **Real constraint.** The agent acts as `kemiller2002`. GitHub forbids approving your own pull request, so an owner review of an agent-opened PR is impossible (rehearsal-006 #1 to #3). GitHub tags PR creation and comments with `performed_via_github_app: claude`, but reviews and merges carry no such tag. So **an "owner approval" review cannot be told apart from the agent acting under the owner's login.** | technical limitation, and a security finding (see SECURITY-REVIEW.md T-1) |
| 14 | PRs going stale after new commits | No dismiss-stale-reviews rule exists, because no review is required. Staleness matters only for Praxis's own evidence, which is now SHA-bound. | not a cause |

## Classification of the barriers

- **Legitimate security controls.** Owner kickoff and acceptance decisions (OD-INDY-7, AR-003, rehearsal-006 `acceptance-process.yml`). These remain owner-controlled (Category B).
- **Owner-imposed governance.** Escalation of irreversible actions, and "agents never ratify". The run authorization satisfies these: the owner signs it once, with a key the agent never holds. They are not removed.
- **Technical limitations.**
  - Author identity equals the owner's login (13).
  - Auto-merge is off (11).
  - Environments and Actions settings are not readable through the proxy (10).
  - No repository creation.
- **Unnecessary interruptions.** Per-merge chat approvals for routine, in-scope, CI-verified work (7). WI-0104 removes these through an owner-signed run authorization and Praxis's merge gate.

## Consequence for security (stated plainly)

GitHub enforces **nothing** today. No check, no review and no restriction on direct pushes to `main` exists, and the agent's credential is admin. Praxis's merge gate is therefore **self-governance by a cooperating agent, not an enforcement boundary against a compromised one**. Only a GitHub ruleset can make the gate mandatory. Making it mandatory is owner-controlled (D-2).

## Decision package (owner-controlled; nothing here was changed)

| ID | Decision | Why | Effect on unattended work |
|---|---|---|---|
| D-1 | Generate an owner P-256 key and register its public key in a trust store kept outside every repository. Then sign the run authorization for each run, once. | This is the only authorization Praxis accepts. The agent cannot produce it. | Required for any real unattended run. |
| D-2 | Add a ruleset on `main` of each target repository: require the CI and `independent-review` status checks from GitHub Actions, block force pushes and deletion, and require no human review. | Turns the advisory gate into an enforced one. Required checks then cannot be bypassed by direct push or API merge. | Compatible: the agent can still merge unattended. |
| D-3 | Optionally, require pull requests for `main` (0 approvals) and restrict direct pushes. | Closes the direct-push path. | Compatible. |
| D-4 | Optionally, enable auto-merge after D-2. | Lets GitHub do the merge once checks pass. Praxis still decides eligibility. | Optional. |
| D-5 | Optionally, a semantic (LLM) reviewer job. It needs an API-key secret in Actions, which is credential creation. | Adds semantic review on top of the deterministic one. | Optional. |
| D-6 | Delete the scratch branches `praxis-autonomy-e2e/*` in indy-init-rehearsal-007 when convenient. The session proxy blocks branch deletion. | Cleanup. | None. |
