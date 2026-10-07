---
id: DF-ROS-2026-A001
title: Canonical artifacts and generated registries
status: accepted
version: 1.0.0
created: 2026-07-24
updated: 2026-07-24
author_agent: codex
supersedes: []
superseded_by: []
related_documents: []
tags: [architecture, registries]
---

# Context

The scaffold instructs agents to update artifact files and JSON registries
manually. Dual writes drift and conflict under parallel Git work.

# Decision

Plain-text artifact files are canonical. JSON registries are deterministic,
replaceable indexes generated from artifact front matter. Curated state belongs
in explicit canonical records, not generated registry entries.

# Alternatives

- Manual registries: rejected because drift is inevitable.
- Registries as canonical databases: rejected because it duplicates artifact
  content and worsens merge conflicts.
- External database: rejected as unnecessary for the current scale.

# Consequences

`ros registry build` rewrites managed registries deterministically.
`ros registry check` and `ros validate` report stale registries. Registry
generation must never edit canonical artifact content.

# Reversibility and validation

The decision is reversible by migration because artifacts retain all canonical
metadata. Tests compare repeated builds byte-for-byte.
