---
id: RQ-ROS-2026-A028
title: One local coordinator delegates bounded work to local agents without required external services
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-10
updated: 2026-10-10
research_area: repository-operating-system
priority: high
related_documents:
  - DF-ROS-2026-A060
  - RQ-ROS-2026-A022
  - docs/local-agent-coordination.md
  - docs/ecir-host-integration.md
tags: [ecir, agents, local, coordination, handoff, offline, recovery]
provenance:
  contributions:
    EXE-20261010T091205536Z-2fa1bdc8:
      operations: [created, modified]
      at: 2026-10-10T12:33:03.712Z
      last: 2026-10-10T15:02:30.258Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "User requires one local coordinator delegating to workers with no external services; record acceptance before implementation"
---

# Requirement

One coordinating agent MUST ingest requirements, propose a complete blueprint,
organize existing Praxis work groups, delegate bounded assignments to local
worker agents, collect results, and request independently verified integration.
The coordination, delegation, approval-policy enforcement, state persistence,
validation and recovery path MUST require no external service.

User direction: 2026-10-10, one agent passes work to other agents with no
external services. Tracks Praxis issues #220 and #226. This record specifies
the target; it does not claim the coordinator loop is implemented.

# Acceptance criteria

| ID | Required behavior |
| --- | --- |
| PRX-LOCAL-001 | One locally invoked controller owns a coordinator session and bounded worker sessions. No mandatory server, hosted queue, Fides, Arca, cloud database, GitHub Actions, remote Git or hosted approval endpoint participates in the local execution path. |
| PRX-LOCAL-002 | Strict offline mode runs with outbound networking denied, a preinstalled local model backend and preinstalled pinned validators/build tools. Network-dependent model adapters declare their dependency and cannot pass offline qualification. Missing dependencies refuse startup; no hidden download or remote fallback occurs. |
| PRX-LOCAL-003 | Every intake requirement has its original source identity and an explicit disposition in the frozen blueprint. Every assigned member maps to those sources and required decisions. Grouping retains existing Praxis work items, individual states, evidence and ownership. |
| PRX-LOCAL-004 | The coordinator passes a versioned machine-readable packet with exact source commit and digests, cohort/member identities, dependency prerequisites, permitted mutations, acceptance obligations, parent/child execution lineage, dispatch/attempt identities and budgets. Prose supplements the packet and never substitutes for identity or scope. |
| PRX-LOCAL-005 | A deterministic local controller checks the packet against operator-delegated scope and current receipt/policy evidence. The coordinating model and workers cannot self-approve a new architecture, rewrite trust policy, broaden scope or treat a JSON approved bit as authority. Decisions outside delegated authority suspend affected work for local operator resolution. |
| PRX-LOCAL-006 | Local authority is established by actual OS/host protections, outside worker write and credential authority. No externally hosted identity service is required. Self-reported actor flags are provenance only; ordinary filesystem paths and Git worktrees are not security isolation. An unrestricted developer fixture is labeled nonqualifying. |
| PRX-LOCAL-007 | Workers use the existing native execution/work/evidence kernels and isolated worktrees. A host-controlled executable and explicit argv select the installed adapter; model-generated text cannot become a shell command, executable selector or credential. |
| PRX-LOCAL-008 | Dispatch obeys dependencies and bounded concurrency. Overlapping writes and shared interfaces are serialized unless a verified isolation/integration rule exists. Mandatory compilation occurs at interface boundaries and scoped/final acceptance remains per member. Batch size is configured and measured, never assumed optimal at 50 or 100. |
| PRX-LOCAL-009 | Worker results name the original dispatch, attempt, child execution, source/blueprint revision, output commit, changed resources, evidence references and outcome. The controller observes commits, digests, ancestry, resource bounds and validator results itself. Exit zero and a worker success claim cannot complete members. |
| PRX-LOCAL-010 | Integration is serialized, idempotent and conditional on the expected destination baseline. Stale, duplicate, late, wrong-worker, out-of-scope or conflicting results are retained with reasons. No worker merges into the coordinator's integration branch or completes the entire cohort. |
| PRX-LOCAL-011 | Durable dispatch/result journals survive coordinator or worker failure. Startup recovers or refuses pending native and ECIR transactions before new mutation. An uncertain spawn is reconciled or refused, not blindly retried. Exactly-once process spawning is not assumed; idempotent reservation and result/integration identities prevent duplicate accepted work. |
| PRX-LOCAL-012 | A local checkpoint profile records its local Git commit, journal identity and verification mechanism without requiring a remote. It is explicitly distinct from remote-verified durability. Existing durable-checkpoint and upstream-sync rules remain unchanged until the new profile is implemented and accepted. |
| PRX-LOCAL-013 | Local stop/revoke cancels new dispatch, records active worker outcomes and revokes authority for subsequent actions. Scope/revision changes invalidate affected packets and produce a new impact/replan record; no historical packet is overwritten. |
| PRX-LOCAL-014 | Telemetry distinguishes coordinator, worker, validation, integration, waiting and rework, preserves parent-child lineage and records unavailable model usage as unavailable. Parallel wall time is not summed as elapsed critical-path time. |
| PRX-LOCAL-015 | A read-only explanation names assignments, original source coverage, dependencies, conflicts, authority limits, budgets and blocking reasons. It starts no process and mutates no work state. |

# Verification

First prove one coordinator and two actual worker sessions on a small cohort,
with a local model backend, outbound network denied and no remote Git configured.
Retain host-observed evidence for every member, dependency and interface compile.
Terminate the coordinator after reservation, during worker execution, after
result arrival and during integration; recover without duplicate integration.

Adversarial coverage MUST include malformed/duplicated identities, omitted
source IDs, dependency cycles, unauthorized decisions, policy/receipt expiry or
revocation, source/blueprint revision changes, path/link escape, forged success,
command injection, overlapping changes, wrong worker/attempt, stale lease,
late/duplicate results, crash during journal preparation and interrupted starts.

An offline deterministic launcher fixture may validate scheduling/recovery
mechanics, but MUST NOT be reported as the actual-agent/local-model pilot.
No arbitrary local model throughput or quality claim substitutes for the pilot.

# Implementation order

1. Typed delegation, worker packet/result contracts and read-only explanation.
2. Explicit local checkpoint/recovery profile and protected controller adapter.
3. Bounded local dispatch through existing execution/worktree/launcher ports.
4. Artifact result intake, independent validation and serialized integration.
5. Failure/revocation tests and a measured one-coordinator/two-worker offline pilot.

The installed local model/runtime adapter is an unresolved implementation
choice. Neither a provider CLI nor a fully offline backend has been qualified
by this record. Existing ECIR execution remains refused until the local
controller, release qualification and independent acceptance are complete.

# Implementation status

The typed packet/result slice implements strict decoding, canonical identity,
original requirement coverage, per-member native execution and file attribution,
fresh authority/prerequisite checks and independent observed result checks.
It is partial progress toward PRX-LOCAL-003/004/005/009, not acceptance of those
criteria as a complete runtime. Its controller observations remain inputs to a
future authenticated adapter. No local worker process, protected authority,
complete dispatch/recovery adapter, local checkpoint profile or offline pilot is delivered
by this slice. Read-only `handoff explain` now reports packet identity, member
source coverage, lineage, dependencies, cross-packet conflicts and blocking
reasons, with real CLI tests proving unchanged file inventories without Git.
This is partial PRX-LOCAL-015: it inspects supplied proposals and does not yet
observe complete intake coverage or protected controller facts.


The isolated dispatch journal adds partial PRX-LOCAL-011 coverage: exclusive
immutable reservations, session-bound intent, strict monotonic observations,
concurrent-writer refusal and retained partial-file errors. Reopened-store and
injected-failure fixtures do not establish actual process reconciliation,
protected authority, power-loss durability or the local checkpoint profile.
No launcher/native work transitions or real offline worker pilot are delivered.


The isolated [process supervision mechanism](../../docs/local-worker-supervision.md)
adds partial bounded-launch coverage using explicit host argv/environment,
executable pins, output budgets, cancellation and root-exit observation. Actual
subprocess fixtures use a synthetic incarnation observer; OS incarnation and
network sandbox probes remain unqualified in this environment. The mechanism
has no CLI route, journal wiring, native work transition or local-model pilot.


[Journaled application dispatch](../../docs/local-journaled-dispatch.md) now
connects isolated reservation/intent and bounded process ports. Exact canonical
stdin and delegated budgets are required. Existing assignments, failed writes,
uncertain start, repeated/inconsistent observations and changed controller
incarnation require refusal or reconciliation. Exited/NoStart close only process
tracking. This is partial launch/recovery coverage in fixtures; protected host
qualification, durable results, integration and the offline agent pilot remain
pending.


[Durable worker submissions](../../docs/local-worker-submissions.md) add the draft
`praxis.local-worker-submission/1` archival shape and isolated dispatch/capture
composition. Bounded unchanged UTF-8 stdout, checksum, exact packet and process
identity survive successful save and fixture interruption; duplicate records
cannot replace original claims. Fresh recovery, independent observations and
authority/dependency/time checks are required on every intake. Stored claims
never supply approval, accepted-result state, integration or completion. The
pre-archive crash interval, power-loss metadata durability, qualified protected
host and integration journaling remain pending. This is partial PRX-LOCAL-009/011
coverage, not acceptance of the complete runtime or offline pilot.
