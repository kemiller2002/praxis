---
id: RQ-TEST-0002
title: Causa semantic color system (test fixture)
status: accepted
version: 1.0.0
---

# RQ-TEST-0002 — Causa semantic color system

Test fixture for the requirements-preservation gate (REQ-PRES-001). Carries
source requirement UI-016 of `BL-CAUSA-UI-TEST-001`.

| ID | Acceptance criterion |
|---|---|
| COL-01 | The interface presents a deliberate, visibly coloured Causa identity and is not an effectively monochrome black, gray and white presentation. |
| COL-02 | Named semantic Forma theme tokens define the canonical palette: deep navy `#14283F`, teal `#087F8C`, amber `#E9A23B`, coral `#D66B58` and soft ivory `#F5F3ED`; no colour is a scattered hardcoded CSS declaration. |
| COL-03 | Foreground and background pairs meet WCAG AA contrast: 4.5:1 for normal text, 3:1 for large text and 3:1 for relevant non-text indicators. |
| COL-04 | Lifecycle, confidence, danger, verification and sync status are never conveyed by color alone; each pairs color with text or an icon. |
| COL-05 | Print and Folio output remains legible in grayscale. |
| VIS-02 | Automated token presence and contrast checks, keyboard focus checks, reduced-motion checks and an independent visual review pass. |
| VIS-03 | A grayscale-only interface fails acceptance even when functional tests pass. |
