---
id: HY-ROS-2026-A021
title: Typed semantic authority reduces duplicated ROS rule sites
research_area: repository-operating-system
status: active
confidence: low
created: 2026-09-07
author_agent: openai-codex
supporting_evidence: [EV-ROS-2026-A018]
contradicting_evidence: []
related_theories: []
supersedes: []
superseded_by: []
tags: [fsharp, migration, semantic-authority, duplication]
---

# Hypothesis

## Statement

For a migrated ROS capability, assigning each stable semantic rule to one typed
F# authority will reduce independently maintained rule sites by at least 30%
without increasing adapter-owned decisions.

## Mechanism

Pure domain decisions and typed outcomes become the one implementation consumed
by CLI and infrastructure adapters. Legacy duplicates remain only as temporary
oracles until an authority switch.

## Predictions and measures

- The artifact slice falls from Node + Python + unenforced schema accounts to
  one typed decision path plus explicit wire declarations.
- A semantic rule change touches fewer independent implementation sites.
- Retained adapters contain environment translation, not artifact decisions.

Count rule sites before/after from symbol-level inspection. Do not use raw LOC
as the primary measure.

## Falsification

The hypothesis is weakened if duplicate rule sites do not fall, F# and adapters
make competing decisions, or a normal semantic change still requires editing
the same number of independent authorities.

## Current assessment

Active and untested at preregistration. Baseline duplication is observed in
`EV-ROS-2026-A018`; no F# treatment existed when this record was created.
