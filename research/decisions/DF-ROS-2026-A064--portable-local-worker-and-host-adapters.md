---
id: DF-ROS-2026-A064
title: Separate portable local worker mechanisms from OS isolation qualification
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-11
updated: 2026-10-11
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents: [DF-ROS-2026-A060, DF-ROS-2026-A062, DF-ROS-2026-A063, docs/local-worker-portability.md]
tags: [agents, portability, adapters, isolation]
confidence: medium
derived_from: [DF-ROS-2026-A060, DF-ROS-2026-A062, DF-ROS-2026-A063]
provenance:
  contributions:
    EXE-20261011T115701257Z-3ebd7686:
      operations: [created]
      at: 2026-10-11T15:16:51.721Z
      actor:
        kind: agent
        id: openai/codex
        provider: openai
        model: unknown
        runtime: codex
      reason: "Record user-directed portability priority, worker/OS/file access adapters and explicitly deferred isolation equivalence"
---

# Context

The user explicitly prioritizes support for different workers and operating
systems now and defers equivalent isolation. Existing packet, authority,
journal and result contracts are shared F# code, but host file permissions and
process effects are embedded in concrete infrastructure. Functional support and
OS containment need separate acceptance criteria.

# Proposed decision

Add a host-provisioned worker adapter catalog with exact worker identity,
declared Linux/macOS/Windows support and allowed execution roles. Ambiguous
bindings and unsupported capabilities refuse before preparation. Command
bridges preserve canonical packet stdin and canonical result stdout. Vendor
prompt/result adaptation belongs in the vendor bridge, not the shared core.
Catalog declarations are configuration, not measured compatibility evidence.

Move process identity observation and stop/root-exit observation into a process
platform port. Keep the existing .NET mechanism as the default and preserve
legacy observer fixtures. Report observation/termination failures as uncertainty;
never infer successful descendant containment from root exit or a tree kill.

Extract the file access policy from shared atomic host persistence. Keep Unix
0700/0600 enforcement as the existing default. Add an explicitly named portable
development profile checking root/path shape and links without Unix modes or
Windows ACLs. Permit host-owned file access adapters through an injected port.
The persistence schema, bounded reads, CAS, audit, pending-write refusal, locks,
ECIR signatures, delegation and native commit gates remain unchanged.

Run functional adapter/storage/subprocess/delegation checks on Linux, Windows
and macOS in CI. Resolve test-only temporary directory ancestors so macOS's
/var alias does not force production code to accept symlinked host roots.

# Alternatives and consequences

Making Unix permission calls part of shared persistence prevents Windows
implementation without duplication. Weakening the default automatically would
silently change its protection behavior. A large universal sandbox interface or
requiring equal isolation before any portability work would prematurely couple
this milestone to work the user has explicitly deferred. OS-specific access and
process ports keep that future work separate and testable.

The development profile does not establish ownership, ACL restrictions, worker
credential separation, network isolation or resistance to hostile same-account
processes. Its profile name is descriptive, not authority. Registered worker
bridges may target different tools, but concrete vendor integrations and their
installed dependency closures need separate compatibility tests. No execution
CLI or qualified controller is enabled by this change.

# Validation and follow-up

Verify exact catalog dispatch, unsupported capabilities, malformed/ambiguous
registrations, canonical transport and budgets, injected failures, portable
policy persistence/CAS/pending refusal and actual bounded subprocess behavior.
Report each OS's CI results separately. Continue portable vendor bridges and
native runtime composition; track equivalent containment as deferred deployment
qualification rather than a blocker for this portability milestone.
