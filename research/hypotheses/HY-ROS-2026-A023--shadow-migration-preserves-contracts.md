---
id: HY-ROS-2026-A023
title: Shadow migration preserves required ROS contracts
research_area: repository-operating-system
status: supported
confidence: low
created: 2026-09-07
author_agent: openai-codex
supporting_evidence: [EV-ROS-2026-A018, EV-ROS-2026-A028]
contradicting_evidence: []
related_theories: []
supersedes: []
superseded_by: []
tags: [fsharp, compatibility, differential-testing, rollback]
---

# Hypothesis

## Statement

Characterization fixtures, explicit codecs, and shadow execution can migrate a
ROS capability with byte-equivalent required outputs, explained diagnostics,
and a working rollback path before the production authority changes.

## Predictions and measures

Compare old Node, independent Python where applicable, and F# exit status,
structured findings, stdout/stderr categories, registry filenames and bytes,
canonical-file changes, and repeated-run behavior on identical controlled
repositories.

## Falsification

Any unexplained data loss/output drift, canonical input mutation, differing
required finding, unsafe repeat, or inability to fall back to unchanged Node
authority falsifies the per-slice claim.

## Current assessment

Supported for the bounded artifact slice: Node/F# registry bytes and the ten
preregistered path/field/message identities matched on controlled fixtures;
repeat build and current-repository smoke passed while Node remained rollback.
Exact human wording remains informative rather than generally equivalent;
see `EV-ROS-2026-A028`.
