---
id: DF-ROS-2026-A068
title: Observe concrete pinned validator processes on exact Git candidates
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-11
updated: 2026-10-11
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents: [DF-ROS-2026-A064, DF-ROS-2026-A066, docs/local-validator-runners.md]
tags: [agents, validators, portability, evidence]
confidence: medium
derived_from: [DF-ROS-2026-A064, DF-ROS-2026-A066]
provenance:
  contributions:
    EXE-20261011T234058504Z-3b80355e:
      operations: [created]
      at: 2026-10-11T23:51:12.439Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
---

# Context and proposed decision

GH-235 continues the portable host toward actual use. The host foundation is
merged in PR #232, but process fixtures and callback-supplied acceptance are
not a concrete build/test/architecture runner. Introduce a portable F# adapter
using the existing bounded process mechanism rather than shell commands or a
provider SDK. The controller supplies literal commands, exact candidate and
explicit tool/dependency pins; the adapter observes Git before and after,
runs the actual command, captures fresh TRX test data where requested, and
durably archives the observation outside the candidate checkout.

Real Git probes exposed a short-lived-process race: a tool may exit before
`.NET Process.StartTime` is readable. Factor the shared bounded supervision
mechanism while keeping two distinct result types. Transient controller tools
observe their actual process handle without claiming a durable incarnation;
worker dispatch retains its mandatory incarnation check before stdin delivery.
No failed worker observation falls back to the tool path.

# Alternatives and consequences

Accepting a worker's pass statement or exit zero for tests would miss forged,
stale and zero-test outcomes. Parsing stdout totals is tool-specific and
permits ordinary prose to resemble evidence. Fresh bounded TRX snapshots allow
the runner to report positive executed tests with consistent result entries.
They still cannot prove useful coverage or prevent malicious same-principal
code from manufacturing files. Protected runner/input/evidence custody is a
controller obligation, not a property conferred by this record type.

Exact Git tree observations cover tracked inputs. Explicit pins cover declared
external dependencies; arbitrary dependency discovery is deliberately not
claimed. Fresh staging and all lifecycle/policy leases remain prerequisites
for activation. The new report is not a signing or native-completion endpoint.
Equivalent OS isolation remains deferred by user instruction.

# Validation and remaining uncertainty

Tests exercise real bounded subprocesses and Git checkouts, replaced pins,
candidate drift, TRX failures despite exit zero, missing/stale results, zero
tests, XML DTDs, timeout, cancellation, overflow and evidence digests. Fixture
workers are deterministic .NET programs, not model workers. Actual model
bridges, checklist mapping, prerequisite evidence custody, execution-envelope
serialization and native completion remain necessary before claiming usable
autonomous local execution. Cross-OS results require the new adapter's own CI.
