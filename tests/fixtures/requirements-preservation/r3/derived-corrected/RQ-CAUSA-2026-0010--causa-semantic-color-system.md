---
id: RQ-CAUSA-2026-0010
title: Causa semantic color system (UI-016)
status: draft
version: 1.0.0
owners: [causa-rehearsal-003]
created: 2026-10-24
updated: 2026-10-24
priority: high
tags: [ui, visual-identity, accessibility]
related_documents: [RQ-CAUSA-2026-0008]
---

# RQ-CAUSA-2026-0010 — Causa semantic color system

Test fixture authored for the Praxis requirements-preservation gate
(REQ-PRES-001): the corrective requirement Rehearsal 3 lacked. Source:
kemiller2002/indy-init `docs/CAUSA-UI-EXPERIENCE-REQUIREMENTS.md` UI-016 at
`fc5df1c9fb268db61ee96e59523826d72f824492`. It is not a record of the
Rehearsal 3 repository.

| ID | Acceptance criterion |
|---|---|
| COL-01 | The interface MUST have a deliberate, visibly colored Causa identity, not an effectively monochrome black/gray/white presentation. |
| COL-02 | Named, semantic Forma theme tokens carry the canonical palette: deep navy `#14283F`, teal `#087F8C`, amber `#E9A23B`, coral `#D66B58`, soft ivory `#F5F3ED`; no scattered hardcoded CSS color declarations. |
| COL-03 | Measured WCAG AA contrast: 4.5:1 for normal text, 3:1 for large text and 3:1 for relevant non-text indicators. |
| COL-04 | Lifecycle, confidence, danger, verification and sync status are never conveyed through color alone; color is paired with text and/or icons. |
| COL-05 | Print/Folio output remains legible in grayscale. |
| COL-06 | Acceptance includes actual screenshots from the deployed exact SHA at desktop, iPad portrait, iPad landscape and mobile sizes. |
| COL-07 | Acceptance includes automated token presence and contrast checks, keyboard focus checks, reduced-motion checks and independent visual review. |
| COL-08 | A grayscale-only interface is a failed acceptance even if functional tests pass. |
