---
id: RQ-CAUSA-2026-0004
title: Evidence listing, detail and provenance
status: accepted
version: 1.0.0
owners: [causa-rehearsal-003]
created: 2026-10-24
updated: 2026-10-24
priority: high
tags: [ui, evidence, provenance]
related_documents: [RQ-CAUSA-2026-0001, RQ-CAUSA-2026-0002, RQ-CAUSA-2026-0003, RQ-CAUSA-2026-0004, RQ-CAUSA-2026-0005, RQ-CAUSA-2026-0006, RQ-CAUSA-2026-0007, RQ-CAUSA-2026-0008]
provenance:
  contributions:
    EXE-20261024T072747754Z-a9afd8e7:
      operations: [created]
      at: 2026-10-24T07:28:44.693Z
      actor:
        kind: agent
        id: claude-code-lead
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Rehearsal 3 requirements package"
derived_from: [INPUT-ca94a3c778288076]
---

# RQ-CAUSA-2026-0004 — Evidence and provenance

Source: brief §7.1 "Evidence listing/detail/provenance"; PRODUCT P2, P4; SCREEN-CONTRACTS S06, S07.

| ID | Acceptance criterion |
|---|---|
| EVD-01 | The evidence view lists every evidence item with category, verification status, source, captured time (historical time where present) and supersession status. It can be filtered by category and by verification status, and filters are reflected in the URL. |
| EVD-02 | Evidence detail shows the content verbatim (text, or artifact URL as a link), category, verification, source, captured time, admitting event sequence, recording actor and time, and the item that supersedes it (or that it supersedes). |
| EVD-03 | Evidence detail shows the research provenance from `catalog/historical-v1/provenance/<case>.json`: claim id, source id, source title, issuer, locator, source class (primary/secondary), research verification, topic, claim kind, and the source URL as an external link. The provenance index is loaded at the pinned commit and digest-checked (INT-02). |
| EVD-04 | Evidence detail lists every relationship to a hypothesis (supports, contradicts, discriminates, insufficient) with its rationale. Each relationship links to the hypothesis. |
| EVD-05 | Evidence detail lists the contradictions, verifications, negative knowledge and hypothesis transitions that cite it. |
