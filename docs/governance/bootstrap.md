---
id: GOV-BOOT-001
title: Completion authority provisioning and secure bootstrap
status: canonical
created: 2026-11-26
work_item: WI-0116
---

# Completion authority and secure bootstrap

A completion authority is the set of verifiers a repository's protected branch trusts to attest that its
validation policy ran on an exact commit. Praxis cannot make itself trustworthy by issuing a receipt that
says so; trust starts with one human action on the platform.

## The authority (`.praxis/trust/verifiers.json`, `praxis.verifier-trust/1`)

```json
{ "schema": "praxis.verifier-trust/1", "configVersion": 1, "repository": "OWNER/NAME",
  "approvedBy": "OWNER-LOGIN", "approvedAt": "2026-11-26T12:00:00Z",
  "verifiers": [
    { "id": "merge-gate-v1", "kind": "github-actions-oidc",
      "issuer": "https://token.actions.githubusercontent.com", "repository": "OWNER/NAME",
      "workflowRef": "OWNER/NAME/.github/workflows/praxis-merge-gate.yml@refs/heads/main",
      "events": ["pull_request_target"], "validFrom": "…", "validUntil": null,
      "revokedAt": null, "revocationReason": null, "policyScopes": [] },
    { "id": "host-1", "kind": "ecdsa-p256", "keyId": "sha256:…", "publicKey": "<base64 SPKI>", "validFrom": "…" } ] }
```

| Element | Meaning |
|---|---|
| Trusted verifier identities | `github-actions-oidc` (keyless, default) or `ecdsa-p256` (host verifier, existing `praxis verifier serve`) |
| Pinned trust root | The workflow identity, or the key's SPKI digest (`keyId` must equal `sha256(publicKey)`; parsing refuses otherwise) |
| Verification scope | `policyScopes` (empty = any policy of this repository); the policy's digest binds the repository |
| Configuration version | `configVersion`; any other change without an increase is a weakening |
| Validity | `[validFrom, validUntil)` against the receipt's observation (or token issue) time |
| Revocation | `revokedAt` refuses **every** receipt of that verifier (a compromised key can back-date) |
| Audit evidence | The merged change that introduced each version (Git), plus each merge decision listing verifier IDs |

The authority is read **only from the protected branch**. A candidate's copy is a proposal: changing it is a
protected change (`.praxis/trust/**`) requiring an elevated approval bound to the exact candidate head, and
`merge authorize` names each weakening (`adds verifier`, `replaces the identity`, `removes the revocation`,
`extends`, `widens the scope`).

## Why keyless by default

Evaluated options: a host-held ECDSA key (existing; needs a machine the agent cannot reach — the owner has
none in routine use), a GitHub Actions secret (a key someone must generate and paste; candidate code must
never share a job with it), a managed KMS (cost and a new provider), and **GitHub Actions OIDC** (free,
managed by the hosting provider already trusted to enforce rulesets, no secret to generate, leak or rotate).
The OIDC identity is pinned to the workflow file on the protected ref and the `pull_request_target` event,
so a candidate cannot obtain a matching token. Praxis verifies the RS256 token against the issuer's JWKS
with the .NET base library (no new dependency).

## Bootstrap (once per repository)

| Step | Who | Action | Why a human |
|---|---|---|---|
| 1 | Agent | `praxis governance bootstrap --repository OWNER/NAME --approved-by OWNER --validation tests="./build.sh test" [--agent AGENT-LOGIN]` on a branch; open a PR | — (writes a proposal; creates no key) |
| 2 | **Owner** | Review and merge the PR (GitHub mobile is sufficient) | Establishes the initial human identity and approves the trust root, policy and gate. An agent merging it would be the agent establishing its own verifier |
| 3 | Agent | Open a trivial PR (declaring `Praxis-Work-Item:`) and confirm the `praxis-merge-gate` check runs and passes | A required gate that cannot run would block every merge, including its own fix |
| 4 | **Owner** | Settings → Actions → General → Workflow permissions: **Read repository contents** | Otherwise any pull-request workflow's token can post a check named like the gate |
| 5 | **Owner** | Settings → Rules → Rulesets → New branch ruleset (desktop-mode browser on a phone works): target the default branch; *Restrict deletions*; *Block force pushes*; *Require a pull request* (0 approvals); *Require status checks*: add `praxis-merge-gate`, source **GitHub Actions**, *require branches to be up to date*; no bypass list. (Alternatively import `.praxis/governance/ruleset-main.json`.) | Only an administrator can change platform rules; agents must not hold that permission |
| 6 | Agent | `praxis governance inspect --repository OWNER/NAME --out .praxis/evidence/inspect.json` and commit it as bootstrap evidence | — |

`--validation NAME=COMMAND` takes words or a JSON array (`tests=["sh","-c","make test"]`); add
`testCountPattern`/`minimumTests` to the generated policy for test suites. Bootstrap writes `.praxis/trust/verifiers.json`, `.praxis/validation-policy.json` (names the repository),
`.praxis/governance/authorities.json`, `.github/workflows/praxis-merge-gate.yml`,
`.praxis/governance/ruleset-main.json`, and `ros.json` `governance.requireVerifiedMerge: true` with the
required check. It refuses to run when the protected branch already has a trust root, so an ordinary agent
cannot repeat or replace the bootstrap; it never creates a signing key.

**Bootstrap limitation (stated, per brief §28).** The first merge that introduces the gate is not itself
gated: the gate is not yet on the protected branch. Its legitimacy rests on the owner's merge of a reviewed
proposal (step 2), not on Praxis. Praxis's own initial implementation of this capability (WI-0116) is in the
same position: it is validated by the repository's existing CI and tests, and is not verified by the verifier
it introduces.

## Rotation, revocation, emergency recovery

- **Rotation** (e.g. renamed workflow, new branch, host key change): a PR adds the new verifier and sets
  `validUntil` on the old one, increments `configVersion`; it needs `/praxis approve-elevated <head-sha>` by an
  elevated approver (in person, not via an app). Receipts observed before `validUntil` stay valid.
- **Revocation** (suspected compromise of a host key, or a workflow found unsafe): set `revokedAt` and
  `revocationReason`. Revoking is never a weakening, but it touches a protected path, so it still needs an
  elevated approval. Every receipt of the revoked verifier is refused at once.
- **Emergency** (gate unavailable — GitHub outage, issuer down — and a merge is necessary): an exception
  authority commits a `praxis.governance-exception/1` record for the exact head(s), ≤ 30 days, naming the
  waived codes, compensating safeguards, residual risk and follow-up; because it lives under
  `.praxis/governance/`, it also needs an elevated approval. The decision is `authorized-by-exception`,
  never `verified`, and `governance audit` reports `AUD.INTEGRATED_BY_EXCEPTION` until follow-up. Identity,
  forgery and protected-change refusals cannot be waived. If the owner account itself is lost, recovery is
  the platform's account recovery; Praxis has no back door.

## The host-verifier path (unchanged)

Repositories with a verifier host continue to use `praxis verifier serve`, `workProtocol.trustedVerifierKeyIds`
and `praxis work finish` (`docs/verified-completion-rollout.md`). Its receipts can be presented to
`merge authorize` by committing them under `.praxis/receipts/<WORK-ITEM>/` and pinning the key as an
`ecdsa-p256` verifier.

## Diagnosing a missing authority

`praxis governance authority` reports which authority is missing, why an agent-held key is not acceptable,
what can be reused, who resolves it, the exact steps, what work may continue and what stays blocked. Exit 3
until ready.
