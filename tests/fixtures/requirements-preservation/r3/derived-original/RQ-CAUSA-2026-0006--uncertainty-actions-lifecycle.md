---
id: RQ-CAUSA-2026-0006
title: Uncertainty, actions, corrections and lifecycle presentation
status: accepted
version: 1.0.0
owners: [causa-rehearsal-003]
created: 2026-10-24
updated: 2026-10-24
priority: high
tags: [ui, uncertainty, lifecycle]
related_documents: [RQ-CAUSA-2026-0001, RQ-CAUSA-2026-0002, RQ-CAUSA-2026-0003, RQ-CAUSA-2026-0004, RQ-CAUSA-2026-0005, RQ-CAUSA-2026-0006, RQ-CAUSA-2026-0007, RQ-CAUSA-2026-0008]
provenance:
  contributions:
    EXE-20261024T072747754Z-a9afd8e7:
      operations: [created]
      at: 2026-10-24T07:28:46.343Z
      actor:
        kind: agent
        id: claude-code-lead
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Rehearsal 3 requirements package"
derived_from: [INPUT-ca94a3c778288076]
---

# RQ-CAUSA-2026-0006 — Uncertainty, actions, corrections

Source: brief §7.1 "Contradictions and uncertainty", "Lifecycle state", "Historical context"; PRODUCT P5, P6, P8; SCREEN-CONTRACTS S08, S12, S13.

| ID | Acceptance criterion |
|---|---|
| UNC-01 | Unknowns, assumptions and contradictions are each listed with statement, materiality, state and basis. Contradictions link their cited evidence. Each list can be filtered by state. |
| UNC-02 | Negative knowledge (what was established *not* to be the case) is listed with scope and basis evidence. |
| ACT-01 | Investigation actions are listed with kind, purpose, targets and state, together with their findings (original result verbatim, interpretation). |
| COR-01 | Corrective actions are listed with kind, description, targeted causes and state, each with its criteria and verifications (outcome and evidence). |
| LIF-01 | Lifecycle-specific presentation: the summary explains what the lifecycle requires next, using only domain facts, for example `awaitingVerification` (Apollo 1: corrective actions awaiting verification of effect), `awaitingEvidence` (Apollo 11: open unknowns), `active` (Challenger). Blocking or material open items are surfaced. |
| LIF-02 | Historical context: observations are shown as a timeline ordered by observed time (mission time), separate from the event-recording time. |
