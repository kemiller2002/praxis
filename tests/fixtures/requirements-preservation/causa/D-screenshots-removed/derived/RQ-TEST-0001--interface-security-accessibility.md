---
id: RQ-TEST-0001
title: Interface, security and accessibility (test fixture)
status: accepted
version: 1.0.0
---

# RQ-TEST-0001 — Interface, security and accessibility

Test fixture for the requirements-preservation gate (REQ-PRES-001). Derived
from `BL-CAUSA-UI-TEST-001`.

| ID | Acceptance criterion |
|---|---|
| CMD-01 | The command bar renders only policy-authorized commands returned by the F# engine for the current version; the UI implements no parallel policy engine. |
| CMD-02 | Stale-version submissions are rejected with the domain fault shown safely, and duplicate submission is prevented. |
| EVI-01 | The evidence inspector never alters or overwrites the original evidence when an interpretation changes. |
| SEC-01 | Sanitize untrusted evidence text and URLs before rendering; missing provenance is labelled as such. |
| SYN-01 | Remote synchronization is claimed only after confirmed provider acknowledgement. |
| SYN-02 | On an expected-version conflict, local edits are preserved and the compare/retry flow never overwrites remote history. |
| SEC-02 | Never expose tokens in the interface, logs, URLs or reports. |
| A11Y-01 | Every view supports keyboard-only navigation, visible focus, a skip link, landmarks, status announcements, labelled controls and non-colour-only states. |
| A11Y-02 | Layouts reflow at 200% text on iPad portrait/landscape, mobile and desktop; reduced motion and screen reader paths are tested. |
| A11Y-03 | No essential operation requires drag-and-drop or hover. |
