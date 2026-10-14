---
id: DF-ROS-2026-A065
title: Compose portable worker dispatch under native and host policy leases
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-11
updated: 2026-10-11
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents: [DF-ROS-2026-A060, DF-ROS-2026-A063, DF-ROS-2026-A064, docs/local-worker-host.md]
tags: [agents, portability, coordination, persistence]
confidence: medium
derived_from: [DF-ROS-2026-A060, DF-ROS-2026-A063, DF-ROS-2026-A064]
provenance:
  contributions:
    EXE-20261011T115701257Z-3ebd7686:
      operations: [created]
      at: 2026-10-11T16:25:47.139Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Record portable coordinator lease scope, signed dispatch composition, mandatory independent native observations and deferred equivalent isolation"
---

# Context and proposal

Portable worker selection, signed delegation, native recovery, immutable launch
journals, output spooling and submission capture exist as separate mechanisms.
A host composition must provide their lock scope rather than relying on a
caller remembering the locking order. User instruction prioritizes portability
and defers equivalent OS isolation.

Add `FileLocalWorkerHost.run`, acquiring native work-protocol, work-groups and
host policy leases in that order. Hold them across fresh authorization, adapter
selection, reservation, launch intent, bounded subprocess observation, spool
sealing, exit recording and immutable submission archival. Use asynchronous
policy lease waiting; reuse native recovery under the held leases. Policy reads
inside this scope must not recursively acquire its lease. No persistent format
changes or automatic retries are introduced.

Bindings are provisioned by the host: repository identity, external store roots,
worker catalog, process platform, current signed envelope, independent native
execution/dependency observations and controller incarnation. The policy store's
clock is the sole source for authorization, events and capture; non-UTC clocks
and clocks preceding policy audit refuse. Native
validation is mandatory and is repeated at each admission/reservation/intent
boundary. This composition does not implement those semantic observers or
enable an execution CLI. Observed process exit and saved worker output grant
neither result acceptance nor integration nor completion.

# Alternatives and consequences

Acquiring a policy lease only during initial admission permits policy mutation
between checking and launch. Reusing public policy Load under an already-held
lease deadlocks. Leaving lease composition to application callers retains a
protocol requirement without a concrete implementation. An infrastructure
composition with private in-scope readers keeps shared application algorithms
and the portable process platform unchanged.

The native leases serialize repository dispatch for the duration of the worker.
Policy issuance/revocation must wait or refuse while this scope runs. This is
an intentional conservative first composition, not a claim of parallel worker
throughput. A future finer-grained controller needs its own race analysis.
Callbacks must be bounded, host-owned and non-reentrant. Portable development
file access does not establish isolation against a hostile same-account worker.
Equivalent containment remains explicitly deferred.

# Acceptance and follow-up

Exercise actual subprocess, journal, spool and submission files; show native
and policy leases held during host observations; refuse malformed delegation,
native observations, expired intent, invalid store roots and cancelled dispatch;
retain uncertain starts without confirmed archival; prove duplicate dispatch
never launches twice and exceptions release leases. Fixtures use synthetic
native/incarnation observations and signing keys. Include the host tests in the
existing three-OS functional portability CI filter. Follow with independently
bound native observers, concrete vendor bridges and intake/integration wiring.
