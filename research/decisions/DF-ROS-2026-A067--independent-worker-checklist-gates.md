---
id: DF-ROS-2026-A067
title: Worker checklists require independent candidate-bound evidence at local admission boundaries
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-11
updated: 2026-10-11
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A029]
related_documents:
  - DF-ROS-2026-A060
  - DF-ROS-2026-A061
  - docs/worker-checklists.md
tags: [agents, checklist, evidence, quality, handoff]
confidence: medium
derived_from: [DF-ROS-2026-A060, DF-ROS-2026-A061]
provenance:
  contributions:
    EXE-20261011T204811145Z-23f864b2:
      operations: [created]
      at: 2026-10-11T20:57:55.235Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Bind generated checks to independently observed candidates and controller policy"
---

# Context

The user authorized implementing an enforced per-agent checklist. Existing
completion facets include presence-only implementation/test attestations;
existing handoff contracts bind requirement acceptance but leave concrete
independent verification to protected host ports. A checked box supplied by
the worker is not evidence that a required check ran on current inputs.

# Decision

Generate mandatory build, tests, architecture and scope rows for every member,
plus the packet's requirement-linked acceptance obligations. Require a complete
host-selected system-validator map. Bind independently observed results to
packet identity, candidate commit, input manifest, policy revision and validator
input closure. Preserve diagnostic compilation; gate accepted handoff,
integration and completion rather than withholding compiler diagnostics.

Enforce at local submission intake, on staged integration candidates (including
prepared recovery), and when planning native completion after integration.
Read-only handoff inspection displays requirements as unobserved. It cannot
create an approval. Standalone native CLI completion-policy adoption is a
follow-up rather than an undocumented global migration.

# Alternatives and consequences

Prompt-only checklists organize work but can be self-approved incorrectly.
Preventing compilation withholds evidence needed to satisfy build and behavior
checks. Accepting checks only on worker output misses interactions introduced
by integration. One blanket group pass permits member evidence substitution.

Per-member system rows may reuse a legitimately shared build/test observation
only when the protected host binds every required row to its precise candidate
and test selection. Reusing a sibling's row identity is refused. Cache reuse
requires the exact current identity, not a historical pass.

Typed host observations do not authenticate themselves. Concrete runner
execution, protected evidence custody and cooperative lifecycle locking must
be implemented and qualified before the runtime is enabled. Input manifests
must cover the complete relevant closure; missing inputs are refusal, not an
empty digest. Existing portability and deferred-isolation decisions remain.

# Evidence and validation

`WorkerChecklistTests` exercises coverage, mandatory validators, stale inputs,
zero executed tests, duplicate/missing/substituted evidence, clocks, expiry and
observer failure. Submission, integration, completion and CLI tests exercise
boundary enforcement. Exact executed results belong in the durable checkpoint
and PR; these source references alone do not claim tests passed.

# Reversibility and follow-up

The local library port gains a mandatory checklist provider. Hosts must supply
independent observations; no permissive default is provided. There is no new
persisted work-state or worker transport migration. Finish concrete runners and
evidence storage, policy/input lifecycle serialization, native CLI adoption and
a coordinator/two-worker pilot under GH-234. Use AST/compiler-derived tier
analysis as a later architecture validator, not as a model-supplied verdict.
