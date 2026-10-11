---
id: DF-ROS-2026-A066
title: Bind local worker admission to current native assignment records
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-11
updated: 2026-10-11
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents: [DF-ROS-2026-A064, DF-ROS-2026-A065, docs/local-worker-host.md]
tags: [agents, portability, native-state, admission]
confidence: medium
derived_from: [DF-ROS-2026-A064, DF-ROS-2026-A065]
provenance:
  contributions:
    EXE-20261011T115701257Z-3ebd7686:
      operations: [created]
      at: 2026-10-11T18:26:30.503Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Record concrete native assignment admission, exact current execution/lineage checks and Windows spool sealing correction"
---

# Context and proposed decision

The portable host accepts a mandatory native-observation callback, but that
callback alone does not provide a concrete native assignment implementation.
Signed delegation authenticates a packet; it cannot prove its member execution
IDs still match repository state. Portability remains the current user priority;
equivalent isolation is deferred.

Always run `FileLocalWorkerAssignment.validate` under the host's existing native
leases, after transaction recovery and before any additional host validator.
Repeat at admission, reservation and launch intent. Observe native group/home
and exact membership, one native group execution linking the assigned members,
active live items, current telemetry links, exactly one active telemetry record
per member, and active member execution envelopes with the approved baseline.
Historical finalized context links may remain; duplicate links or extra active
executions refuse. A group run may have ended its dispatch without completing
its members, so group `EndedAt` does not imply member terminal state.

Read coordinator and worker envelopes independently: active states, exact IDs,
repository-qualified work identity, worker parent, role, actor and source
baseline must match the packet. Resolve the exact source commit in actual Git
and require it to be an ancestor of current HEAD. Reads reject links, malformed
or duplicate JSON fields, crosswired telemetry filenames and unavailable facts.
Bound each JSON read to 16 MiB and the telemetry inventory to 4096 files.

Cross-repository groups and successor-mapped member executions remain refused
until a separately specified observer can bind them. Semantic prerequisite
evidence, validator results, worker worktree/dependency closure and result
acceptance remain separate host obligations. Repository state is observed
evidence, not new signing authority or equivalent worker isolation.

# Alternatives and consequences

Relying entirely on a host callback permits an accidentally permissive callback
to bypass all native assignment checks. Trusting the signed member IDs misses
replacement and lifecycle changes. Reusing tolerant query APIs can skip unknown
states or choose the last matching identity. Exact current observations retain
refusal on ambiguity without introducing a second lifecycle writer.

The first implementation scans a bounded telemetry inventory at each boundary.
Large repositories over the bound refuse explicitly; a future indexed reader
must preserve ambiguity detection. Envelope/schema defaults follow the native
reader, with independent identity/state checks. This proposal adds no persistent
format, execution command, acceptance marker or work completion bypass.
Execution-envelope mutation does not yet share the work/group lease protocol
across all writers. Repeated reads detect divergence at these boundaries;
they do not establish serialization of every native lifecycle writer. The
operational controller must coordinate lifecycle writes and recheck at intake
and integration. Completing that serialization remains a recorded follow-up.

# Verification and related fix

Use actual Git commits, native group and envelope serializers, and disposable
state fixtures. Challenge membership, lineage, states, links, ambiguity,
baseline substitution, source availability, malformed/duplicate JSON and path
identities. Run the integrated host with fresh native checks and refuse member
changes after adapter preparation. Synthetic signing keys and process observers
remain explicit; this is not a model-worker pilot.

Windows CI exposed a spool sharing conflict: sealing opened readers denying
writes while writer handles remained open. Flush and close both writers before
reading and verifying bytes. Failed verification retains a closed unsealed
spool. Include durable spool checks in the three-OS matrix; do not weaken reader
sharing or permit writes after verification.
