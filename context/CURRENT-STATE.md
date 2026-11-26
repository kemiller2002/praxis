# Current State

## Repository status

ROS governance, executable artifact validation, portable bootstrap, provider-neutral work protocol 1.0, external work-adapter contract 1.0, adaptive execution telemetry 1.0, and the Ordo observation/handoff layer are implemented. ROS can ingest executable Ordo resolution observations, attach provenance-bearing retrospective assessments, preserve scoped negative-search and unknown-effect facts, derive effective-current projections from immutable history, and emit revision-bound structured handoffs. Work protocol support includes context, legal transitions, configurable evidence, meaningful-change attribution, automatic per-execution telemetry start/finalization, normalized and sanitized raw runtime data, deterministic Git/clock metrics, CI validation, and an idempotent adapter conformance seam.

Remote execution for agents without a local runtime (GH-90, `RQ-ROS-2026-A021`, `DF-ROS-2026-A041`) is implemented on branch `claude/remote-execution-capability-2i5s9u` (PR #91) and verified locally and in CI. It includes the `praxis.remote` 1.2 protocol, `praxis remote execute|classify|describe`, the request journal, expected-SHA binding, identity roles, steps and usage, the verified bootstrap, and the GitHub Actions adapter. The live end-to-end proof, PRAXIS-REMOTE-11, is blocked on merging the PR, publishing an attested release that contains the remote commands, and pinning it.

Enforceable governance (WI-0116, RQ-ROS-2026-A039, `docs/governance/`) is implemented on branch `claude/praxis-verified-completion-eu4ydu`: platform governance inspection with a readiness ladder and drift, an integration audit that reproduces the echelon-assurance PR #3 incident, a keyless completion authority (GitHub Actions OIDC workload identity) with a secure one-merge bootstrap, `praxis merge authorize` with receipt bindings, standing authorization, exceptions and elevated approvals, and a protected merge-gate workflow template. It is not yet enforced anywhere: no Echelon repository, including Praxis, has branch rules or a provisioned trust root; each needs one owner merge and one ruleset import (`docs/governance/rollout.md`).

## Active research streams

Real-repository adoption measurement, external adapter consumer feedback, and calibration-history collection from observed Ordo outcomes.

## Highest-confidence areas

Artifact validation, portable initialization, and the minimal work-state transition core.

## Lowest-confidence areas

Production transport authentication/authorization, central telemetry publication, authoritative provider billing data, interactive-runtime usage extraction, credential handling, and cross-repository base-range behavior beyond GitHub pull-request CI.

## Largest remaining unknown

Accumulate enough real observation/outcome history to evaluate calibration and research-recommendation hypotheses without prematurely automating routing or research policy.

## Recently invalidated ideas

None registered.

## Priority changes

None registered.
