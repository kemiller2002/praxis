---
id: RQ-CAUSA-2026-0003
title: Investigation catalogue, switching and summary
status: accepted
version: 1.0.0
owners: [causa-rehearsal-003]
created: 2026-10-24
updated: 2026-10-24
priority: high
tags: [ui, catalogue, summary]
related_documents: [RQ-CAUSA-2026-0001, RQ-CAUSA-2026-0002, RQ-CAUSA-2026-0003, RQ-CAUSA-2026-0004, RQ-CAUSA-2026-0005, RQ-CAUSA-2026-0006, RQ-CAUSA-2026-0007, RQ-CAUSA-2026-0008]
provenance:
  contributions:
    EXE-20261024T072747754Z-a9afd8e7:
      operations: [created]
      at: 2026-10-24T07:28:43.885Z
      actor:
        kind: agent
        id: claude-code-lead
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Rehearsal 3 requirements package"
derived_from: [INPUT-ca94a3c778288076]
---

# RQ-CAUSA-2026-0003 — Catalogue, switching and summary

Source: brief §1 items 8–9, §7.1 "Investigation analysis"; mission test J; SCREEN-CONTRACTS S02.

| ID | Acceptance criterion |
|---|---|
| CAT-01 | The home view lists every catalog investigation (all three for `historical-v1`) with title, case key, expected lifecycle, evidence/source/claim counts and load status (not loaded, loading, verified, failed). |
| CAT-02 | Each investigation is individually openable by keyboard and pointer and has its own deep link (`#/investigation/<caseKey>`). Reloading a deep link restores the source and the view. |
| CAT-03 | The user can switch between investigations without reloading verified ones. State per investigation is cached for the session at the pinned commit. |
| SUM-01 | The summary shows the title, the original observation verbatim, the lifecycle with a plain-language explanation of what it means (draft/active/awaiting evidence/awaiting verification/resolved/closed/archived), version, creation time, and the verified state digest and commit. |
| SUM-02 | The summary shows the latest conclusion (highest sequence) with its strength, its residual uncertainty stated verbatim, and the causal assertions it cites. Apollo 11's conclusion states the mission **succeeded**; it is not presented as a failure. |
| SUM-03 | The summary shows counts for every entity collection, each linking to its view. |
