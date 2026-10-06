---
id: DF-ROS-2026-A052
title: Consume Dokimos and Ordo quality evidence at work completion
status: accepted
version: 1.1.0
owners: [repository-governance]
created: 2026-10-05
updated: 2026-10-06
research_area: repository-operating-system
decision_type: architecture
supports: []
related_documents:
  - requirements/CODE-QUALITY-HARDENING.md
  - docs/work-protocol.md
  - schemas/praxis-completion-readiness.schema.json
  - schemas/work-protocol.schema.json
supersedes: []
superseded_by: []
tags: [architecture, work-protocol, completion, quality, dokimos, ordo]
confidence: medium
provenance:
  contributions:
    EXE-20261005T105949929Z-9c503655:
      operations: [created]
      at: 2026-10-05T11:14:34.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Consume Dokimos and Ordo quality evidence at completion (PRX-QUAL-023)"
      evidence: [https://github.com/kemiller2002/praxis/issues/167]
    EXE-20261006T215449549Z-494ad502:
      operations: [modified]
      at: 2026-10-06T21:55:13.063Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Amend with risk obligations, evidence digests and release readiness (PRX-QUAL-020..023, PRAXIS-QUAL-07..10)"
---

# Decision

Praxis decides whether a work item may complete from quality evidence that
other tools produced; it does not produce that evidence (PRX-QUAL-023,
kemiller2002/praxis#167).

- **Consume, never reimplement.** Dokimos measures and judges code quality
  (`dokimos.ratchet` 1.0.0). Ordo detects boundary amplification
  (`ordo.boundary-amplification/1`). Praxis trusts each tool's own verdict and
  recommendation and checks only contract consistency (a verdict that
  contradicts its exit code, or a `pass` that counts unexcepted regressions,
  is unavailable). It never recomputes a ratchet or re-derives an Ordo
  recommendation.
- **Files, not processes.** Evidence arrives through the existing
  `work complete --evidence TYPE=PATH` mechanism, as types `dokimos-ratchet`
  and `ordo-boundary`. Praxis does not spawn Dokimos or Ordo in this slice, so
  completion stays deterministic and offline, and the evidence a decision was
  made from is a reviewable file.
- **Four independent facets.** Readiness is `implementation-complete`,
  `behavior-verified`, `architecture-verified` and `release-ready`, each
  `satisfied`, `not-satisfied`, `unavailable` or `not-required`.
  `architecture-verified` is satisfied only when Dokimos reports `pass` and
  Ordo does not demand `require-design-review` without `full` exception
  coverage. `release-ready` has no consumed contract yet, so requiring it is
  always unavailable.
- **Unavailable is never a pass.** A missing, ambiguous, malformed or
  unsupported document, a Dokimos `unavailable` verdict, a report measured
  against a baseline other than the pinned one, and an Ordo assessment of a
  different work item are all unavailable. Required + unavailable blocks.
  Failing evidence (`regression`, `invalid-exceptions`, design review without
  full coverage) blocks under `required` and `optional` alike.
- **Policy, opt-in.** `workProtocol.qualityEvidence` in `ros.json`
  (`dokimos`, `ordoBoundary`: `required|optional|off`; `dokimosBaseline`;
  `requiredFacets`; `workTypes`). Absent means the legacy behaviour: nothing is
  evaluated and nothing is recorded. A present but invalid policy fails closed.
- **Record additively.** A ready completion carries a
  `praxis.completion-readiness/1` record as `completionReadiness` on its
  `work.completed` event and on the completed work item. A refusal changes no
  state, prints the record (`outcome: refused`) and exits 3 (verification
  failed). The runtime-free envelope path applies the same gate.

# Why

- Duplicated analysis drifts: two judges of the same code eventually disagree,
  and neither can be trusted. One owner per judgement keeps ownership clear.
- Consuming files keeps Praxis free of tool installation, version and process
  management, and makes every completion decision reproducible from
  committed evidence.
- Separate facets stop "the tests pass" from standing in for "the
  architecture held" or "it can be released".
- An opt-in default keeps every existing repository's completion behaviour
  byte-identical until it chooses to adopt the gate.

# Alternatives rejected

- Running `dokimos ratchet check` from `work complete`: couples completion to
  tool installation and build logs, and hides the evidence inside a process.
- Recomputing "fully excepted regressions" from findings: Dokimos already
  folds exceptions into its verdict; recomputing it duplicates Dokimos.
- Treating `optional` as "ignore whatever is supplied": failing evidence that
  was supplied would be silently accepted.
- A required-by-default policy now: most repositories do not yet produce
  Dokimos reports, so every completion would fail.

# Consequences

- The migration bridge (off by default) is tracked debt. Retirement
  condition: once Conditor installs Dokimos by default, newly initialized
  repositories default to `dokimos: required`.
- Praxis must follow new major versions of either contract explicitly; until
  then such reports are unsupported, which blocks a required source.
- A named Dokimos *profile* is represented by its accepted baseline path
  (`dokimosBaseline`); the `dokimos.ratchet` 1.0.0 contract carries no other
  profile identity.
- The record does not yet include a digest of the consumed evidence file
  (resolved by the 2026-10-06 amendment).

# Revisit when

- Dokimos or Ordo publishes a new contract version or a profile identity.
- Release-readiness or verification-matrix evidence (PRX-QUAL-022) gets a
  consumable contract.
- Conditor installs Dokimos by default (retire the bridge).

# Amendment (2026-10-06): risk obligations, digests and release readiness

Implements PRX-QUAL-020..022 and the PRX-QUAL-023 leftovers through the same
facet mechanism (PRAXIS-QUAL-07..10, `GROUP-PRAXIS-COMPLETION-001`).

- **Risk metadata** (`praxis.work-risk/1`, set by `work update`) is declared
  by the work item, not inferred. Praxis derives completion obligations from
  it with one pure rule (`WorkRisk.obligations`): high or critical work owes a
  design-debt declaration; stateful control-plane, persistence,
  release/bootstrap, security or remote-execution work, or work declaring
  state, protocol or security impact, owes a verification matrix.
- **Two facets join the record:** `design-debt-declared` and
  `verification-matrix-satisfied`. Grouped-execution facets (PRX-GRP-133..135,
  `group-verified`) follow the same pattern.
- **Obligations bind without the policy.** An item whose risk carries
  obligations is gated even when `qualityEvidence` is off; this does not
  change any repository that declares no risk, so the migration bridge
  still holds for them.
- **Praxis-owned contracts** (`praxis.design-debt/1`,
  `praxis.verification-matrix/1`, `praxis.release-readiness/1`) are consumed
  like Dokimos and Ordo evidence: Praxis checks their consistency and never
  re-runs the verification they describe. Compilation or unit tests alone
  never satisfy a matrix.
- **Digests.** Every consumed evidence file is recorded with the SHA-256 of
  the bytes decoded, so the decision is reproducible from committed evidence.
- **Release readiness** is no longer always unavailable: a consistent
  `ready` document whose checks all passed satisfies it.

Alternatives rejected: inferring risk from changed paths (a guess, and Ordo
already owns boundary assessment); requiring a matrix for every change (it
would make low-risk work ceremonial and push executors to write empty
matrices).
