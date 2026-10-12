---
id: DF-ROS-2026-A070
title: Activate the local coordinator/worker runtime through a signed, checklist-enforcing entry point
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-12
updated: 2026-10-12
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents: [DF-ROS-2026-A063, DF-ROS-2026-A065, DF-ROS-2026-A066, DF-ROS-2026-A067, DF-ROS-2026-A068, docs/local-runtime.md, docs/worker-checklists.md]
tags: [agents, validators, runtime, authority, recovery]
confidence: medium
derived_from: [DF-ROS-2026-A067, DF-ROS-2026-A068]
---

# Context

GH-234 and GH-235 asked for a usable local workflow: a coordinator delegates
exact packets to actual workers, independently observed checklists gate the
result, an accepted candidate integrates with an expected-head update, and
native work completes legally. The library slices (PR #236, #237, #240)
supplied the gates but no production composition or entry point.

# Decision

1. **Entry point.** `praxis local run` composes signed delegation, worker
   dispatch, concrete validators, Git staging and the existing native
   completion effect. A host configuration (`praxis.local-host/1`) outside
   the repository selects mechanisms only. Authority remains the protected
   host policy plus an operator signature over each exact packet.
2. **No private keys in Praxis.** Operator commands write the exact signing
   payload and verify an externally produced ECDSA P-256 signature (DER from
   `openssl dgst -sha256 -sign`, or P1363) against the protected signer before
   applying a grant or emitting a delegation. Key generation and custody stay
   with the operator's own tools, preserving the earlier stance that Praxis
   implements no production signing. Grants are added through a new audited
   `granted` policy action from a trusted signer for a group the policy does
   not yet name.
3. **Relocatable validator pins.** Input identity v2 binds the candidate
   checkout and TRX output by role (`{candidate}`, `{trx}`), so one signed
   acceptance pin covers the worker's output, the staged integration
   candidate, retries and reopened sessions. Every run still needs a fresh
   checkout of the exact commit and a fresh TRX path; all tool, argv,
   environment, budget and dependency inputs remain pinned.
4. **Staging.** Candidates are staged with Git's tree-only merge without a
   working tree; conflicts refuse. A staged candidate must differ from the
   observed target only by the worker's observed paths, carrying exactly the
   worker's committed contents. The target moves only through
   `git update-ref REF NEW OLD`.
5. **Completion policy migration.** `workProtocol.workerChecklists.delegatedTypes`
   names native work types that complete only through the runtime. Standalone
   `work complete` and runtime-free envelope reconciliation refuse them; the
   runtime refuses members of any other type. Repositories without the
   setting keep the previous readiness policy, which is documented rather
   than silent.
6. **Lifecycle serialization (WI-0087).** Execution-envelope writers acquire
   `work-protocol` then `work-groups`; external-process verbs take the lease
   per write.
7. **Interrupted completion.** A member still active whose single linked
   execution is finalized, with the host integration journal for that exact
   dispatch applied, is resumable; every authority, time, candidate,
   checklist, target and evidence check repeats. This is reconciliation of an
   observed partial effect, not a new authority.

# Alternatives

Embedding signing in Praxis would put operator keys within reach of any
same-account process that can run Praxis, and contradict the existing
signing stance. Repository-configured validators or workers would let a worker
change its own acceptance. Overwriting stale TRX or reusing a path-bound pin
across checkouts would accept evidence from a different run. Leaving
standalone completion unchanged would leave an undocumented checklist bypass.

# Consequences and limits

The runtime is functionally portable; equivalent OS isolation remains
deferred. A same-account worker, operator key or configuration is protected
only by the deployment, not by these checks. Multi-member packets require a
bridge that can attribute changes per member; the shipped bridge blocks them.
An interrupted completion whose delegation has since expired cannot be
resumed under that delegation and needs a new attempt or an explicit,
recorded abandonment.

# Validation

Real-CLI tests drive two workers (fast-forward, then merge-staged with a
verified prerequisite), a failing worker, a forged delegation, replay, a
post-exit refusal resumed without relaunch, and an interrupted completion
resumed without a second target update. A live pilot used a locally served
open-weights model through the shipped bridge; see `docs/local-runtime.md`.
