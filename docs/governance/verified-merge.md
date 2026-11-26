---
id: GOV-VM-001
title: Verified completion, merge authorization, standing authorization, exceptions and evidence
status: canonical
created: 2026-11-26
work_item: WI-0116
---

# Verified merge protocol

Verified completion of repository work means: an authorized work item's exact integrated artifact passed the
protected validation policy, attested by a verifier the implementer does not control. `praxis merge
authorize` is the decision; the `praxis-merge-gate` required check is its platform enforcement.
Domain: `MergeAuthorization.decide` (pure); facts gathered by `GovernanceOperations.authorize`.

## Receipt bindings

A receipt is `echelon.verification-receipt/1` (existing). It is trusted either as a DSSE envelope signed by a
pinned `ecdsa-p256` verifier, or as `praxis.workload-receipt/1`: the exact payload bytes plus a GitHub OIDC
token whose audience is `praxis-receipt:<sha256 of payload>` and whose `repository`, `job_workflow_ref` and
`event_name` match a pinned workload verifier.

| Brief element | Bound by |
|---|---|
| Organization / repository | Validation-policy digest (the protected policy names `repository`); workload token `repository`; request repository must equal protected `ros.json` identity |
| Work item, generation | Signed `workItemId`, `fencingGeneration` (receipts of one work item must share one generation) |
| Authorized execution / authorization record | Work item open on the protected branch, or standing authorization (below) |
| Source commit, artifact | Signed `commit` and `treeId` (cross-checked against Git) |
| Base commit / merge result | Candidate must contain the current target head; then the merged tree equals the verified tree plus exempt bookkeeping |
| Requirements addressed | Work item (requirements trace through the work item record; not separately signed) |
| Validation plan, policy version | `validationPolicyId`, `validationPolicyDigest` = digest of the protected policy |
| Verification environment, verifier, authority | Workload `job_workflow_ref` + run; verifier ID in the trust root; trust root `configVersion` |
| Results, evidence digests | Per-validation `status`, `exitCode`, `outputSha256` |
| Timestamp | `observedAt` (and token `iat`) within the verifier's validity window |
| Exceptions | Listed in the decision (`waived`, exception IDs) |
| Schema version | Payload `schemaVersion`; decision `praxis.merge-decision/1` |

A receipt cannot be reused for another repository (policy digest), work item, generation, commit or tree,
policy, an untrusted/expired/revoked verifier, or a modified artifact. A change after validation outside
`.ros/`, `.praxis/receipts/`, `.praxis/evidence/` refuses (`MRG.ARTIFACT_CHANGED_AFTER_VERIFICATION`); a
moved target refuses (`MRG.STALE_BASE`) and the gate re-runs on the updated branch (`synchronize`).

## Merge decision (refusal codes)

`verified` | `authorized-by-exception` (never verified) | `refused`. Refusals carry code, detail, remedy and
the principal who can resolve them (`agentResolvable` in JSON):

`MRG.REPOSITORY_MISMATCH`, `MRG.TARGET_MISMATCH`, `MRG.WORK_ITEM_UNDECLARED`, `MRG.WORK_ITEM_UNAUTHORIZED`,
`MRG.PROTECTED_CHANGE_UNAUTHORIZED`, `MRG.TRUST_ROOT_UNAVAILABLE`, `MRG.POLICY_UNAVAILABLE`,
`MRG.RECEIPT_FORGED`, `MRG.RECEIPT_UNTRUSTED`, `MRG.RECEIPT_POLICY_MISMATCH`, `MRG.RECEIPT_WORK_ITEM_MISMATCH`,
`MRG.VALIDATION_INCOMPLETE`, `MRG.RECEIPT_NOT_PASSED`, `MRG.RECEIPT_STALE_GENERATION`,
`MRG.ARTIFACT_CHANGED_AFTER_VERIFICATION`, `MRG.STALE_BASE`, `MRG.EXCEPTION_INVALID`,
`MRG.VERIFIED_COMPLETION_NOT_REQUIRED`. An advisory states when the platform does not enforce the profile.

**Platform enforcement.** The gate job is `praxis-merge-gate` in a workflow read from the protected branch
(`pull_request_target`). The ruleset requires it from GitHub Actions (integration 15368), so another app or
token cannot satisfy it, and requires the branch up to date. Administrators can bypass rules the platform lets
them bypass; `governance audit` reports such merges (`AUD.GATE_REFUSED_BUT_MERGED`).

## Work-item lifecycle

Existing states are reused (`ready`, `active`, `blocked`, `complete`, `abandoned`) with claims
(`claimed`, `running`, `released`, `expired`, `completed`). The conceptual states of the brief map as follows:
proposed = `captured`; authorized/ready = `ready` on the protected branch (or standing authorization);
claimed/in progress = claim + `active`; validation pending/failed = gate `observe` running / receipt not
passed; verification pending = gate running; **verified complete = a `verified` merge decision for the
candidate**; merge pending = gate passed, not merged; integrated = on the protected branch (audit);
blocked/cancelled/superseded = `blocked`/`abandoned`. A plain `work complete` is not verified completion; the
audit labels integrations by their gate evidence, and reports `AUD.INTEGRATED_WHILE_ACTIVE` when an active
item reaches the protected branch without verified evidence, or `AUD.COMPLETION_PENDING` when the gate
verified it and the completion record is still to be written (`work complete --evidence merge-decision=…`).

Recovery: the decision is a pure function of Git objects and protected configuration, so a re-run after agent
termination, network loss, CI interruption or a duplicate request yields the same decision; an interrupted
`observe` yields no or `unavailable` receipts (refused); a stale generation is refused; GitHub's own
`synchronize` re-runs the gate for a new head. Claims, leases, checkpoints and the autonomy merge journal keep
their existing recovery semantics (`docs/work-claims.md`, `docs/autonomous-execution/`).

## Standing authorization (`.praxis/standing-authorization.json`)

```json
{ "schema": "praxis.standing-authorization/1",
  "policies": [ { "id": "SA-docs-tests", "repositories": ["OWNER/NAME"], "approvedBy": "OWNER",
    "validFrom": "2026-11-26T00:00:00Z", "expiresAt": "2027-02-26T00:00:00Z", "revokedAt": null,
    "prohibitedPaths": [".github/**", ".praxis/**", "ros.json"],
    "categories": [ { "name": "documentation", "allowedPaths": ["docs/**", "*.md"], "maxChangedFiles": 20 },
                    { "name": "unit tests", "allowedPaths": ["tests/**"], "maxChangedFiles": 10 } ] } ] }
```

A change is covered only if every path matches one category, within its file limit, none is prohibited, and
the policy is active. Standing authorization never covers a protected class: those always need an elevated
approval (`/praxis approve-elevated <head-sha>` by an elevated approver, posted in person). Required
validations come from the protected policy, never from the standing authorization. The file itself is a
protected path. Elevated approval is required for trust roots, verification policy and workflows, governance
records, exceptions, build and dependency configuration, credentials and permission expansion. Production
activation, expenditure, sensitive-data disclosure and destructive administration are outside what Praxis can
authorize and stay with the owner.

## Exceptions (`.praxis/governance/exceptions/*.json`, `praxis.governance-exception/1`)

Required: `id`, `repository`, exact `candidateHeads`, `waives` (codes), `reason`, `authorizedBy` (exception
authority, not an agent identity, not the `implementer`), `compensatingSafeguards`, `residualRisk`,
`followUp`, `grantedAt`, `expiresAt` (≤ 30 days). Not waivable: repository/target mismatch, forged receipts,
invalid exceptions, protected changes. Exceptions under `.praxis/governance/` are protected paths, so adding
one in a candidate needs an elevated approval for that head.

## Provenance

Every decision records: the authorization basis (work item or standing policy), the implementing work items,
each receipt's verifier, basis (`pinned-key`, `workload-identity`) and validation, elevated approvals
(approver, time), waived codes and exception IDs, and the target and candidate commits. Human identities are
platform logins; agent identity remains self-reported provenance (`docs/agent-provenance.md`) and is never
used as authentication. Model names are never evidence of identity. No prompts, credentials or execution
context are stored.

## Evidence

| Event | Evidence |
|---|---|
| Authorization, work items, state transitions, claims | Existing `.ros/` records and events |
| Validation execution | `observe` receipts + output files (workflow artifact `praxis-observations`) |
| Verification, receipt issuance/rejection, merge authorization | `praxis.merge-decision/1` (artifact `praxis-merge-decision`, 90 days; `--evidence-out`) |
| Merge execution, direct pushes, bypass, rewritten history | `praxis governance audit` (`praxis.integration-audit/1`) |
| Policy and authority changes | Git history of protected paths + elevated approvals in decisions |
| Governance drift | `praxis governance drift --baseline report.json` (`praxis.governance-drift/1`) |
| Missing evidence | `AUD.UNVERIFIED_INTEGRATION` |

Evidence storage is Git history and workflow artifacts. It is not immutable storage, and is not claimed to
be. Arca (immutable storage) and Vigila (alerting) do not yet provide these capabilities; when they do, the
decision and audit documents are the records to ship.
