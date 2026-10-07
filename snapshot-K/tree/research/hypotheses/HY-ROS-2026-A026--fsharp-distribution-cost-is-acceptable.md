---
id: HY-ROS-2026-A026
title: F# distribution cost is acceptable for ROS consumers
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
tags: [fsharp, dotnet, distribution, portability]
---

# Hypothesis

## Statement

A future F# ROS runtime can meet supported consumer installation, cold/warm
startup, package-size, bootstrap-duration, offline/update, and rollback needs
without materially harming the current npm-centered workflow.

## Predictions and measures

Measure framework-dependent, tool, self-contained, and hybrid acquisition on
explicit macOS/Linux/Windows targets before selecting production distribution.
Record runtime presence, bytes, latency distributions, package integrity,
upgrade behavior, and failures.

## Falsification

The hypothesis is weakened if common consumers require an unjustified runtime,
install/upgrade reliability falls, size or latency exceeds a preregistered
ceiling, or Node fallback becomes routine.

## Current assessment

Proposed and intentionally unresolved. This mission authorizes a
framework-dependent `.NET 10` repository-local shadow executable only; it is not
evidence of consumer distribution acceptability.
