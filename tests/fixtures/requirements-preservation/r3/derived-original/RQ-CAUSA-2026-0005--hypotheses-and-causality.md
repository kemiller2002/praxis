---
id: RQ-CAUSA-2026-0005
title: Hypotheses, causal relationships and conclusions
status: accepted
version: 1.0.0
owners: [causa-rehearsal-003]
created: 2026-10-24
updated: 2026-10-24
priority: high
tags: [ui, causality]
related_documents: [RQ-CAUSA-2026-0001, RQ-CAUSA-2026-0002, RQ-CAUSA-2026-0003, RQ-CAUSA-2026-0004, RQ-CAUSA-2026-0005, RQ-CAUSA-2026-0006, RQ-CAUSA-2026-0007, RQ-CAUSA-2026-0008]
provenance:
  contributions:
    EXE-20261024T072747754Z-a9afd8e7:
      operations: [created]
      at: 2026-10-24T07:28:45.501Z
      actor:
        kind: agent
        id: claude-code-lead
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Rehearsal 3 requirements package"
derived_from: [INPUT-ca94a3c778288076]
---

# RQ-CAUSA-2026-0005 — Hypotheses and causality

Source: brief §7.1 "Claims, hypotheses, causal relationships", §7.1 UI "causal visualization"; mission tests D, E; PRODUCT P3, P7; SCREEN-CONTRACTS S03, S04, S11.

| ID | Acceptance criterion |
|---|---|
| HYP-01 | The hypotheses view lists every hypothesis with statement, mechanism and state, grouped by state. Each shows the number of supporting and contradicting evidence relationships. |
| HYP-02 | Hypothesis detail shows the full state history: from → to, command, reason verbatim, the cited evidence (linked) and the time. It also lists its predictions and the actions that target it. |
| CAU-01 | The causal view shows each causal assertion's outcome, state, mechanism and members, grouped by the domain's cause role (root cause, trigger, contributing factor, enabling condition). Each member links to its hypothesis and shows that hypothesis's state. |
| CAU-02 | A causal diagram (accessible SVG, with a text equivalent) shows the outcome, the members by role, and the count of supporting evidence per member. |
| CAU-03 | Challenger: the physical failure (root cause and trigger: field-joint design and cold O-ring seal failure) and the organizational causes (contributing factors and enabling condition: launch decision, management, safety organization, normalized erosion, schedule pressure) appear in distinct, labelled role groups and are never merged. |
| CAU-04 | Conclusions are listed with statement, strength, residual uncertainty and the causes they cite. |
