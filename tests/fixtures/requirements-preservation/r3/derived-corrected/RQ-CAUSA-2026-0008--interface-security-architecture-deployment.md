---
id: RQ-CAUSA-2026-0008
title: Interface, security, architecture and deployment
status: accepted
version: 1.0.0
owners: [causa-rehearsal-003]
created: 2026-10-24
updated: 2026-10-24
priority: high
tags: [ui, security, architecture, deployment]
related_documents: [RQ-CAUSA-2026-0001, RQ-CAUSA-2026-0002, RQ-CAUSA-2026-0003, RQ-CAUSA-2026-0004, RQ-CAUSA-2026-0005, RQ-CAUSA-2026-0006, RQ-CAUSA-2026-0007, RQ-CAUSA-2026-0008]
provenance:
  contributions:
    EXE-20261024T072747754Z-a9afd8e7:
      operations: [created]
      at: 2026-10-24T07:28:48.012Z
      actor:
        kind: agent
        id: claude-code-lead
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "Rehearsal 3 requirements package"
derived_from: [INPUT-ca94a3c778288076]
---

# RQ-CAUSA-2026-0008 — Interface, security, architecture, deployment

Source: brief §2, §7.1 UI and Deployment, §8; mission tests K, L, M; WASM-LOADING-EXPERIENCE; R002 DC-006.

| ID | Acceptance criterion |
|---|---|
| UI-01 | The interface uses Forma 0.9.0 (`@echelon-foundry/design-system`) CSS and canonical pattern markup. No React, Vue or other UI framework is used. |
| UI-02 | Responsive from 320 px to desktop with no horizontal page scroll. Navigation collapses on narrow screens. |
| UI-03 | Accessible: landmarks, a skip link, headings in order, keyboard operation of every control, visible focus, and focus moved to the new view's heading on navigation. Status is never conveyed by colour alone. The Playwright axe scan reports no serious or critical violations on each main view. |
| UI-04 | Loading states: a WASM start screen with progress; a start failure or a hang ends in an explicit failure with retry within a bounded time (R002 DC-006); per-investigation loading shows which file is being fetched and validated. |
| UI-05 | Validation and connection errors are shown in Forma alert patterns with the error code, cause and recovery action. |
| ARC-01 | F# owns domain, application state and services. The browser boundary is Limen 0.9.0 (JS kernel + `EchelonFoundry.Limen.*` 0.9.0). Hand-written JavaScript is limited to the Limen bootstrap. No database and no backend. |
| ARC-02 | Layers are separated by project references, and the separation is checked by a test: kernel → application state (`Causa.App`) → investigation services (catalog discovery, GitHub adapter as data, validator, reconstruction) → domain (vendored engine). The domain and contract projects reference no Limen or browser assembly. |
| SEC-01 | No credential appears in source, build output, test snapshots, logs, URLs or reports. CI runs a credential scan over the repository and the built site. |
| SEC-02 | CSP: `connect-src 'self' https://api.github.com`; `script-src 'self' 'wasm-unsafe-eval'`; no inline script. |
| SEC-03 | Browser token disclosure is documented: a token supplied in a GitHub Pages application is visible to that browser session. Prefer a fine-grained, read-only, short-lived token; none is needed for the public catalog. |
| DEP-01 | Deployed to GitHub Pages by the repository's workflow from `main`. `build-info.json` and the visible footer stamp the source commit, the build time and the stack versions. |
| DEP-02 | Deployed acceptance: a real browser loads the deployed site, waits until `build-info.json` equals the commit under test, then loads all three investigations from real GitHub and verifies their state digests (test K). |
| DEP-03 | The built site contains no bundled catalog data, checked with `causa-data`'s `historical_catalog.py verify --app`. |
| TEL-01 | Engineering telemetry is captured with provenance (Praxis records, CI run data, transcript-derived token counts). Unavailable measurements are reported as unavailable (test M). |
