---
id: HY-ROS-2026-A022
title: Typed states and outcomes reduce invalid-state defects
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
tags: [fsharp, types, state, defects]
---

# Hypothesis

## Statement

Explicit F# states, validated value objects, and exhaustive result handling will
move a meaningful share of invalid artifact/work/execution outcomes to compiler
or boundary detection without increasing persisted invalid states or repair
loops.

## Predictions and measures

Record defects by detector: compiler/type system, boundary test, behavior test,
integration, mutation/adversarial check, self-review, independent review, or
escaped runtime. Count persisted invalid states and repair loops separately.

## Falsification

The hypothesis is weakened if the same invalid values remain representable in
the core, adapter parsing silently restores sentinel/null behavior, persisted
invalid states or repair loops are unchanged/worse, or tests merely restate
implementation.

## Current assessment

Active and untested. The first artifact slice tests validated identifiers,
kind/status combinations, and explicit findings; it does not generalize to the
future work/telemetry state machines.
