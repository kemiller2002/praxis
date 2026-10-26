# Causa UI Experience Requirements

## UI-004: Contextual command interface [P0]
Render only policy-authorized commands returned by the F# engine for the current version. For prohibited actions, show a clear reason when the engine supplies one, without implying the action is available. Capture required command inputs with validated forms and confirmation for destructive or irreversible operations. Reject stale-version submissions, show domain fault messages safely, and avoid duplicate submission. Keyboard invocation and touch targets are required. UI MUST NOT implement a parallel policy engine.

## UI-005: Evidence inspector [P0]
Display source/provenance, capture/record timestamps, original evidence content or safe reference, interpretation, reliability/limitations where defined, and explicit links to supporting and contradicting hypotheses. Never alter or overwrite the original evidence when interpretations change. Sanitize untrusted text/URLs; clearly label missing provenance. Enable navigation from graph, timeline, and hypothesis comparison.

## UI-006: Save, sync and conflict status [P0]
Expose distinct states: clean, unsaved, saving locally, saved locally, queued offline, syncing remotely, remotely synced, conflict, failed. Only claim remote synchronization after confirmed provider acknowledgement. Show last successful sync time when known. On expected-version conflict, preserve local edits and provide a recoverable compare/retry flow; do not overwrite remote history. Frozen fixtures MUST remain read-only. Never expose tokens.

## UI-011: Keyboard, accessibility and iPad usability [P0]
Support keyboard-only navigation, visible focus, skip-to-content, appropriate landmarks, semantic status announcements, labeled controls, non-color-only states, and minimum usable touch targets. Support iPad portrait/landscape, narrow mobile screens, desktop, 200% text reflow, reduced motion, and screen readers. No essential operation may require drag-and-drop or hover. Preserve focus and scroll context during transitions and WebAssembly handoff. Graphs require equivalent accessible textual interaction.

## UI-016: Causa semantic color system [P0]
The interface MUST have a deliberate, visibly colored Causa identity, not an effectively monochrome black/gray/white presentation. Use these initial canonical palette values:

| Token role | Color | Hex | Intended use |
| --- | --- | --- | --- |
| Deep navy | Deep navy | `#14283F` | Primary navigation, masthead, structural emphasis |
| Teal | Teal | `#0B7285` | Primary investigation actions, active selection and positive workflow emphasis |
| Amber | Amber | `#E9A23B` | Unresolved unknowns, pending review and attention |
| Coral | Coral | `#D66B58` | Contradictions, material conflicts and critical investigation alerts |
| Soft ivory | Soft ivory | `#F5F3ED` | Main workspace surface and light backgrounds |

All colors MUST be mapped to named, semantic Forma theme tokens, not scattered hardcoded CSS declarations. Use Forma components for surfaces, controls, states and focus treatment. Define derived foreground, hover, disabled, border and dark-mode tokens only after measuring contrast; the five hex values are starting identity colors, not a guarantee of accessible foreground/background combinations. Ensure WCAG AA contrast for normal text (4.5:1), large text (3:1), and relevant non-text indicators (3:1). Do not convey lifecycle, confidence, danger, verification or sync status through color alone: pair color with text and/or icons, and distinguish causal semantics from decorative accents. Use the same token semantics across dashboard, investigation views, timeline, causal graph, evidence inspector, report and startup shell. Print/Folio output MUST remain legible in grayscale.

Acceptance MUST include actual screenshots from the deployed exact SHA at desktop, iPad portrait/landscape and mobile sizes, plus automated token presence and contrast checks, keyboard focus checks, reduced-motion checks and independent visual review. Verify that navy, teal, amber, coral and ivory appear in appropriate populated states; absence of data must not force a misleading status color. A grayscale-only interface is a failed UI-016 acceptance even if functional tests pass.


## UI-017: Added after the baseline was pinned [P0]
Test fixture: a requirement the pinned inventory does not contain.
