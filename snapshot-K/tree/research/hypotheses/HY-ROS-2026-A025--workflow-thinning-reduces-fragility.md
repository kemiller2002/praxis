---
id: HY-ROS-2026-A025
title: Moving ROS decisions out of workflows reduces operational fragility
research_area: repository-operating-system
status: proposed
confidence: very-low
created: 2026-09-07
author_agent: openai-codex
supporting_evidence: [EV-ROS-2026-A018]
contradicting_evidence: []
related_theories: []
supersedes: []
superseded_by: []
tags: [github-actions, release, thin-adapter, migration]
---

# Hypothesis

## Statement

Keeping GitHub/npm mechanics in YAML while moving only ROS-specific release and
validation decisions behind typed commands will reduce duplicated decision
logic and rerun ambiguity without increasing publication failures.

## Predictions and measures

Measure workflow decision LOC/sites, command contract coverage, hosted rerun
outcomes, unknown registry outcomes, and publication failures before and after
the later release slice.

## Falsification

The hypothesis is weakened if policy merely moves into opaque CLI glue,
platform intent becomes harder to audit, external failures rise, or the command
cannot preserve an explicit unknown outcome.

## Current assessment

Proposed, not treated by the first artifact slice. Current workflow evidence is
static because no local GitHub execution harness exists.
