---
id: HY-ROS-2026-A024
title: Structural locality reduces ROS discovery expansion
research_area: repository-operating-system
status: active
confidence: very-low
created: 2026-09-07
author_agent: openai-codex
supporting_evidence: [EV-ROS-2026-A018]
contradicting_evidence: []
related_theories: []
supersedes: []
superseded_by: []
tags: [sde, navigation, structural-locality, migration]
---

# Hypothesis

## Statement

A small semantic map, feature manifests, and feature-local typed authorities
will reduce unexpected navigation and cross-area edits for later comparable ROS
changes without hiding real dependencies.

## Predictions and measures

Prospectively record declared context, files actually needed, context expansion,
undeclared dependencies, manifest usage, and files touched for later migration
slices. Compare only reasonably matched work; do not optimize the observations
during collection.

## Falsification

The hypothesis is weakened if manifests are bypassed or stale, declared context
does not predict actual dependencies, cross-project navigation increases, or
maintenance duration/rework rises without a quality benefit.

## Current assessment

Very Low confidence. No project `SDE-MAP.md` or feature manifest existed at T0,
so baseline CER/Discovery Expansion is missing rather than retrospectively
manufactured. This mission can establish instrumentation, not a causal result.
