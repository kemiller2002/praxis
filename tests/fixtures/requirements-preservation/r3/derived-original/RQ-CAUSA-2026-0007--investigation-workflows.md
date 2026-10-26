---
id: RQ-CAUSA-2026-0007
title: Investigation workflows: history time travel and counterfactual sandbox
status: accepted
version: 1.0.0
owners: [causa-rehearsal-003]
created: 2026-10-24
updated: 2026-10-24
priority: high
tags: [workflow, domain]
related_documents: [RQ-CAUSA-2026-0001, RQ-CAUSA-2026-0002, RQ-CAUSA-2026-0003, RQ-CAUSA-2026-0004, RQ-CAUSA-2026-0005, RQ-CAUSA-2026-0006, RQ-CAUSA-2026-0007, RQ-CAUSA-2026-0008]
provenance:
  contributions:
    EXE-20261024T072747754Z-a9afd8e7:
      operations: [created]
      at: 2026-10-24T07:28:47.176Z
      actor:
        kind: agent
        id: claude-code-lead
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Rehearsal 3 requirements package"
derived_from: [INPUT-ca94a3c778288076]
---

# RQ-CAUSA-2026-0007 — Investigation workflows

Source: brief §7.1 "Investigation workflows" ("Preserve the workflows defined by the approved Causa requirements. Do not invent new domain transitions."); SCREEN-CONTRACTS S14 (History / Time Travel), S16 (Counterfactual Sandbox); PRODUCT P1.6 (immutable historical meaning).

| ID | Acceptance criterion |
|---|---|
| WF-01 | History: the event history is listed (sequence, type, time, actor). The user can view the investigation **as of** any version. That state is computed by `Evolve.replay` over the event prefix, and every view (summary, counts, lifecycle) reflects it. Returning to the latest is one action, and the as-of version is in the URL. |
| WF-02 | Sandbox: the user can open a sandbox copy of a verified investigation and issue domain commands. Each command is executed by the engine's `Decide.execute` against the sandbox state. Accepted events are applied with `Evolve`, and the engine's domain errors are shown verbatim. The UI offers commands but never decides legality. |
| WF-03 | Sandbox commands offered: hypothesis transitions (mark plausible/supported/weakened, falsify, supersede, reopen, with reason and cited evidence), unknown transitions, contradiction transitions, and investigation lifecycle transitions. Each is offered only for entities that exist; the engine decides whether it is legal. |
| WF-04 | The sandbox is unmistakably labelled as not persisted. It never issues a network write, never changes the verified investigation, and can be discarded. The verified view and the sandbox view can be told apart at all times. |
| WF-05 | Persisted writes (capturing new evidence into a workspace) are out of scope for Rehearsal 3, because there is no approved write credential (decision `DF-CAUSA-2026-0001`). The UI does not offer them. |
