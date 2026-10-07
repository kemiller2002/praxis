---
id: JR-ROS-2026-A016
title: ROS operational inventory and F# migration architecture investigation
status: complete
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-07
updated: 2026-09-07
supersedes: []
superseded_by: []
related_documents:
  - EV-ROS-2026-A015
  - RP-ROS-2026-A017
tags: [architecture, inventory, fsharp, migration, journal]
confidence: high
---

# Objective

Discover every ROS script and script-equivalent operational component,
reconstruct the system behavior distributed among them, and design a staged F#
migration that preserves current meaning without mechanically translating the
current file structure.

# Chronology

1. Read root governance, the governance index, constitution, agent operating
   manual, REP specification, engineering standard, work protocol, and adaptive
   telemetry contract. Observed a dirty `main` worktree and preserved it.
2. Found no ready work item, captured `WI-0009`, marked it ready, and began it as
   research. ROS created execution `EXE-20260907T085853369Z-9449d0c0`.
3. Enumerated all tracked, untracked, ignored, hidden, shebang-bearing, and
   executable files; all file extensions; all package scripts; all workflow
   steps; and all manifest-installed sources. Confirmed the absence of authored
   Bash, PowerShell, F#, C#, Make/Just/Task, composite-action, and Git-hook code.
4. Read every authored operational Node, Python, and TypeScript source plus all
   workflows, package scripts, manifests, ROS configurations, schemas, metric
   registry, relevant HTML, tests, accepted decisions, and operational docs.
5. Traced imports, exported functions, child processes, environment variables,
   Git commands, state reads/writes, generated projections, HTTP routes,
   provider adapters, and test callers. Distinguished current runtime code,
   installed templates, generated browser outputs, independent legacy tools,
   and methodology inputs.
6. Ran the full baseline test suite: 84 Node and 7 Python tests passed in about
   13.1 seconds. Ran `./ros validate` and `./ros registry check`; both passed.
   Measured source/test/workflow lines, manifest entry counts, local package
   contents, and available runtimes.
7. Reconstructed nine current flows: bootstrap installation; canonical artifact
   projection; local backlog/work execution; telemetry ingestion/finalization;
   local web access; project-administration aggregation; CI validation; npm
   publication; and file-adapter publication.
8. Challenged the initial “four F# projects plus adapters” design. The first
   challenge removed project-administration and time-entry concepts from the
   stable generic domain because repository evidence makes them separate or
   not-yet-implemented concerns.
9. Challenged provider integration ownership. Provider mappings were moved out
   of the durable semantic model into replaceable edge adapters that emit a
   provider-neutral observation batch; raw and extension data remain
   round-trippable.
10. Challenged the assumption that F# types should generate every wire format.
    The revision retains explicit, versioned JSON contracts and requires custom
    boundary codecs/golden tests so host-language serialization cannot silently
    redefine interoperability.
11. Challenged a full ASP.NET/web rewrite and a database/event-store migration.
    Neither is supported by current scale or failure evidence. The revised plan
    keeps the TypeScript clients and thin HTTP adapter initially, and preserves
    transparent file storage while first fixing locking, atomicity, and recovery
    contracts.
12. Challenged moving all GitHub logic into F#. The final boundary keeps GitHub
    triggers, permissions, checkout, tool installation, artifact upload, and npm
    publication in YAML. Only ROS-specific validation and release-plan decisions
    move behind CLI commands.
13. Challenged starting with the largest or most central script. Telemetry and
    work transitions have higher domain importance, but the first vertical slice
    is deterministic artifact validation/registry projection: it eliminates the
    existing Python/Node duplicate, establishes contracts and packaging, and can
    be compared byte-for-byte before any authoritative state mutation moves.
14. Performed a final failure-oriented review across partial writes, concurrent
    writers, Git unavailability/renames, retries, unknown external outcomes,
    schema evolution, package portability, workflow reruns, unauthenticated HTTP,
    malicious multipart filenames, provider growth, and AI-agent operation
    boundaries. Remaining changes were backlog refinements rather than target
    boundary changes, satisfying the diminishing-returns stop condition.

# Important observations

- The current Node code is a coherent but accidental application kernel.
- Existing file formats and CLI exit/output behavior are public contracts, not
  incidental serialization details.
- Stable rules are suitable for F# discriminated unions and pure transitions;
  metrics, provider fields, R&D context, and future administrative observations
  need extensible boundaries.
- The largest immediate architecture risk is not language choice but competing
  write disciplines and non-transactional multi-file operations.
- The best migration establishes contracts, pure decisions, and persistence
  ports before moving high-churn telemetry adapters or speculative downstream
  systems.

# Failed assumptions and revisions

- **Assumption:** every named future capability belongs in `Ros.Domain`.
  **Revision:** only observed stable ROS semantics do; project administration is
  a separate bounded context and time entry begins as an external projection.
- **Assumption:** a single F# executable should replace every Node surface.
  **Revision:** Node/TypeScript remain appropriate for npm bootstrapping and the
  browser edge until measured distribution or maintenance evidence supports a
  replacement.
- **Assumption:** a new database or append-only store is required for durability.
  **Revision:** current scale does not justify it. Define a file transaction and
  recovery protocol first; revisit only on measured contention/size thresholds.
- **Assumption:** telemetry should be the first migrated vertical slice because
  it is largest. **Revision:** its concepts are newer and more experimental. A
  deterministic artifact-validation slice provides a safer architecture proof.

# Files changed

- `research/evidence/EV-ROS-2026-A015--operational-architecture-inventory-baseline.md`
- `research/journals/JR-ROS-2026-A016--fsharp-migration-architecture-investigation.md`
- `research/packages/RP-ROS-2026-A017--operational-architecture-and-fsharp-migration-plan.md`
- generated registries and ROS work/telemetry state during required closeout

# Highest-value next step

Accept or revise the proposed migration boundary, then execute backlog item
`MIG-00` from `RP-ROS-2026-A017`: freeze the compatibility fixture corpus and
pre-register the SDE experiment before adding the F# solution skeleton.
