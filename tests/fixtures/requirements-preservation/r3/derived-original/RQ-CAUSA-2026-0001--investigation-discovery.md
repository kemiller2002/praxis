---
id: RQ-CAUSA-2026-0001
title: Investigation discovery from a configurable GitHub repository
status: accepted
version: 1.0.0
owners: [causa-rehearsal-003]
created: 2026-10-24
updated: 2026-10-24
priority: high
tags: [discovery, github]
related_documents: [RQ-CAUSA-2026-0001, RQ-CAUSA-2026-0002, RQ-CAUSA-2026-0003, RQ-CAUSA-2026-0004, RQ-CAUSA-2026-0005, RQ-CAUSA-2026-0006, RQ-CAUSA-2026-0007, RQ-CAUSA-2026-0008]
provenance:
  contributions:
    EXE-20261024T072747754Z-a9afd8e7:
      operations: [created]
      at: 2026-10-24T07:28:42.191Z
      actor:
        kind: agent
        id: claude-code-lead
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Rehearsal 3 requirements package"
derived_from: [INPUT-ca94a3c778288076]
---

# RQ-CAUSA-2026-0001 — Investigation discovery

Source: brief §7.1 "Investigation discovery"; mission tests A, B, F, G; R002 EXT-DATA-003/006/007.

| ID | Acceptance criterion |
|---|---|
| DISC-01 | The data source is configurable as owner, repository and branch. The default is `kemiller2002/causa-data` @ `main`. The source is part of the URL (`#/?repo=owner/name&branch=b`), so a link reproduces the same source. |
| DISC-02 | The branch is resolved once to a commit SHA, and every subsequent read for that session uses that SHA. The commit is shown to the user. A reload re-resolves. (A pinned commit gives a consistent snapshot and makes reads immutable. This closes R002 DC-005's stale-read class for the read path.) |
| DISC-03 | `catalog/active.json` is read at the pinned commit and validated: canonical JSON (RFC 8785 + LF); fields `activeCatalog`, `catalogId`, `catalogVersion`, `manifest`, `workspace` present as strings; `catalogVersion` = `1.0.0`. Otherwise the state is **catalog invalid** or **catalog version unsupported**, naming the field. |
| DISC-04 | The catalog manifest named by the pointer is read, validated (canonical; `cases` array; every entry carries the fields the investigation loader needs) and enumerated. Exactly the listed investigations are shown, in manifest order, with title, case key, expected lifecycle and counts, without loading their event files. |
| DISC-05 | Investigation files are fetched lazily, only when an investigation is opened (or explicitly verified). The list remains usable while one investigation loads. |
| DISC-06 | Connection states are explicit and distinct: idle, resolving, **authentication failed** (401, or 403 without a rate-limit signal), **repository or branch not found** (404 on the repository or ref), **catalog unavailable** (404 on `catalog/active.json`), **catalog invalid**, **rate limited** (403/429 with `x-ratelimit-remaining: 0` or `retry-after`), **network failure**, and **outcome unknown** (timeout). Each state names its cause and offers retry. None is shown as an empty catalog. |
| DISC-07 | Access is anonymous by default (the data repository is public). An optional token can be supplied for private repositories or higher rate limits. It is held only in WebAssembly memory; it is never written to the URL, storage, logs, diagnostics or the DOM after submission; and it is sent only to `https://api.github.com`. Clearing it is one action. |
