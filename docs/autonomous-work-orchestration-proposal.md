# Autonomous work orchestration
Status: proposed; documentation-only pilot.

## Objective
Maximize validated organizational output per unit of AI capacity while minimizing human interventions. Preserve existing Praxis work protocol, canonical records, provenance, telemetry and verification. Do not introduce a competing lifecycle or registry.

## Requirements
- AWO-01: Discover and reuse existing Praxis work records and identifiers. Extend only where a demonstrated gap exists.
- AWO-02: Work packages specify objective, authorized repository and paths, dependencies, priority, deliverables, acceptance evidence, risk, resource ceiling, stop conditions and escalation rules.
- AWO-03: Select highest-value eligible work; skip blocked work and continue until capacity, authorization or eligible work is exhausted.
- AWO-04: Claims must prevent concurrent conflicting execution. Record owner, execution ID, lease/checkpoint and recovery procedure. Git commits alone are not atomic distributed locks; pilot uses disjoint assignments or a single dispatcher.
- AWO-05: Delegate only where independent work adds value. Use bounded parallelism and risk-based independent review. Consolidate evidence and avoid duplicate context loading.
- AWO-06: Preserve durable checkpoints, provenance, source revisions, partial results and outstanding questions. Resume idempotently after interruption.
- AWO-07: Research and documentation are permitted within approved scope. No application implementation, policy overrides, spending, secrets exposure, or bypassing merge gates. Proposed new work is deduplicated and remains within delegated authority.
- AWO-08: Validate deliverables against acceptance criteria and existing Praxis gates before completion. Bound retries; escalate unresolved conflicts without blocking unrelated work.
- AWO-09: Maintain provider-neutral policies and isolate provider-specific execution instructions. Verify actual Claude plan, model, scheduling, session, concurrency, subagent and usage capabilities before relying on them. Never assume an unlimited session or automatic restart.
- AWO-10: Track observed usage where available, otherwise mark unavailable; distinguish estimates from measurements. Track validated deliverables, rework, intervention, duplicate effort and blocked time. Never infer token allowance from subscription price.
- AWO-11: Use least expensive suitable model where supported; reserve stronger models for difficult synthesis and high-risk review. Do not automatically incur API charges.
- AWO-12: Keep organizational priorities and master queue in the organizational administration repository; detailed work and evidence in owning repositories. Public records must exclude credentials, client data and confidential material.

## Pilot
1. Inventory existing Praxis capabilities and map each requirement to existing records or gaps.
2. Verify provider capabilities from official documentation and account-visible settings.
3. Define compatible work package template, dependency rules, recovery, authority and capacity policies.
4. Inventory authorized Echelon work, including Unbound, DAF landscape, SOC assurance and framework readiness. Deduplicate and rank by value, readiness and risk.
5. Prepare a backlog with enough alternatives for blocked work, plus a master execution prompt. Start with a single dispatcher and disjoint work packages; expand concurrency only after validating coordination.
6. Test interruption, stale claims, failed review, blocked dependencies and exhaustion using tabletop scenarios before enabling unattended operation.
7. Evaluate pilot results before proposing any new orchestration software.

## Acceptance
A fresh session can identify eligible work, execute within scope, record evidence, recover interrupted work and continue without conversation history. Existing Praxis validation remains authoritative. No production code is required for this pilot.
