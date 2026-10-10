---
id: DF-ROS-2026-A059
title: ECIR approval belongs to a protected host authority and dispatch commits one recoverable member transaction
status: draft
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-10
updated: 2026-10-10
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A024]
related_documents:
  - docs/ecir-execution.md
  - docs/ecir-host-integration.md
  - docs/ecir-receipt-handoff.md
  - DF-ROS-2026-A042
tags: [ecir, authorization, security, recovery, decision]
confidence: medium
derived_from: [DF-ROS-2026-A042]
provenance:
  contributions:
    EXE-20261010T091205536Z-2fa1bdc8:
      operations: [created, modified]
      at: 2026-10-10T09:50:15.033Z
      last: 2026-10-10T10:12:10.716Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "GH-220 protected host authority and recoverable dispatch proposal; activation remains gated"
---

# Context

Praxis #220 requires immutable blueprint-directed cohorts with independently
authorized decisions. PR #222 validates signed approvals without starting
work. Its existing group command reads facts, begins a member and then records
the group in separate operations. Repository configuration and self-reported
Praxis actor identity cannot establish independent approval authority.

# Proposed boundary

A host outside the construction agent's write and credential authority owns
policy revisions, qualified release pins, repository identity, group-to-source
identity maps, signer keys, authenticated reviewer identity and an append-only
audit. Fides can provide authenticated identity when its host integration is
available; this change does not introduce another login system.

Issuance covers one independently validated cohort and all its decisions.
Revocation removes the signer key. Policy changes and administrative audit
records commit together under compare-and-swap. No receipt is returned when
signing, authentication, comparison or audit persistence fails.

Dispatch holds the repository work/group locks, validates immutable inputs,
checks approval with a fresh clock after validation, stages the existing native
work and group planners, and rechecks policy, membership and time after staging.
The host compare-and-commit serializes revocation against the authorization
commit. A protected host journal records the exact member/context, event,
telemetry, group and authorization evidence writes before repository mutation.
Process recovery replays that accepted write set; it never calls work begin a
second time. It refuses changed targets and unsafe paths before any replay.

# Alternatives

- Repository policy or CLI approval flags: agent-writable, so insufficient.
- Previously successful preflight as a reusable capability: stale policy,
  membership or expiry can invalidate it before dispatch.
- Begin first and repair group state later: exposes a started member without
  the approved group evidence and makes retries ambiguous.
- A new work-item implementation: duplicates the native protocol and loses
  existing telemetry, per-member evidence and checkpoint rules.

# Implementation and limits

The new infrastructure modules implement host ports, exact grant resolution,
issuance/revocation semantics, revalidation sequencing and a protected-journal
recovery primitive. Native staging invokes the existing work-begin and group
recording paths in an isolated Git repository and recovers their exact member
execution, telemetry and envelope writes. Direct and fallback-envelope begins
refuse declared ECIR members, sharing the membership-mutation lock.
Tests use isolated fake host authorities and temporary repositories. They do
not demonstrate OS protection, Fides integration, released-tool qualification
or a host-authorized CLI execution route.

The proposal remains unaccepted for execution activation. `execute-group`
continues to refuse ECIR. No production signer, private key, host deployment,
release qualification or real cohort execution was created by these tests.

# Acceptance before activation

Implement and review a concrete protected host adapter; qualify released Ordo
ECIR and Conditor metadata; connect native member staging and group recording under
one lock order; ensure every mutation/remote route recovers or refuses pending
host transactions; preserve actual member telemetry and checkpoint identity;
then run independent behavioral acceptance on a small cohort. Source-trace
Dokimos auditing alone is not behavioral acceptance.

Rollback consists of retaining the existing ECIR refusal. Existing non-ECIR
execution and work state are unaffected by these isolated modules.
