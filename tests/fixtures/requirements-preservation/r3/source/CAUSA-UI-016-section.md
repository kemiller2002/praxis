## UI-016: Causa semantic color system [P0]
The interface MUST have a deliberate, visibly colored Causa identity, not an effectively monochrome black/gray/white presentation. Use these initial canonical palette values:

| Token role | Color | Hex | Intended use |
| --- | --- | --- | --- |
| Deep navy | Deep navy | `#14283F` | Primary navigation, masthead, structural emphasis |
| Teal | Teal | `#087F8C` | Primary investigation actions, active selection and positive workflow emphasis |
| Amber | Amber | `#E9A23B` | Unresolved unknowns, pending review and attention |
| Coral | Coral | `#D66B58` | Contradictions, material conflicts and critical investigation alerts |
| Soft ivory | Soft ivory | `#F5F3ED` | Main workspace surface and light backgrounds |

All colors MUST be mapped to named, semantic Forma theme tokens, not scattered hardcoded CSS declarations. Use Forma components for surfaces, controls, states and focus treatment. Define derived foreground, hover, disabled, border and dark-mode tokens only after measuring contrast; the five hex values are starting identity colors, not a guarantee of accessible foreground/background combinations. Ensure WCAG AA contrast for normal text (4.5:1), large text (3:1), and relevant non-text indicators (3:1). Do not convey lifecycle, confidence, danger, verification or sync status through color alone: pair color with text and/or icons, and distinguish causal semantics from decorative accents. Use the same token semantics across dashboard, investigation views, timeline, causal graph, evidence inspector, report and startup shell. Print/Folio output MUST remain legible in grayscale.

Acceptance MUST include actual screenshots from the deployed exact SHA at desktop, iPad portrait/landscape and mobile sizes, plus automated token presence and contrast checks, keyboard focus checks, reduced-motion checks and independent visual review. Verify that navy, teal, amber, coral and ivory appear in appropriate populated states; absence of data must not force a misleading status color. A grayscale-only interface is a failed UI-016 acceptance even if functional tests pass.

