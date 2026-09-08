---
id: HY-ROS-2026-A023
title: Shadow migration preserves required ROS contracts
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

Active and untested at preregistration. Exact human wording is informative but
is not declared equivalent to structured finding identity unless existing
callers prove it is a contract.
