# Design by Construction — praxis input

Status: proposed source input for Praxis; not yet ingested or implemented.
Initiative: Design by Construction (2026-10-15)

## Objective
Prevent known classes of invalid UI and print designs structurally, instead of depending primarily on post-hoc visual inspection and testing.

## Repository scope
Define agent governance for contract-conforming UI generation; discover design contract version; ingest repository-local requirements; require diagnostics and evidence for custom escape hatches; integrate work-item lineage.

## Shared requirements
1. Use a versioned machine-readable design contract with explicit compatibility rules.
2. Standard mode constrains semantic layout, alignment, overflow, typography, color, accessibility, motion and action semantics.
3. Extended mode permits documented semantic overrides; custom mode requires a reason and additional validation evidence.
4. Preserve ordinary HTML/CSS and accessible web semantics; do not make a proprietary designer format mandatory for consumers.
5. Reject structurally invalid compositions before code generation with actionable error messages.
6. Preserve backward compatibility or provide a documented, versioned migration path.
7. Ensure interactive controls can be driven by keyboard and machine tools such as Playwright, and that commands have observable meaningful behavior.
8. Treat runtime and visual testing as evidence for properties that contracts cannot guarantee.

## First end-to-end slice
A customer form containing two fields, email validation, a real save command, and responsive layout. The contract must prevent misaligned standard fields, unsupported layout overrides, missing action bindings, and invalid parent-child compositions. Validate narrow/mobile and desktop viewports and reduced-motion behavior.

## Acceptance evidence
- Versioned schema and documented supported/unsupported examples.
- Invalid fixtures fail deterministically with specific diagnostics.
- Valid fixtures generate or render correctly on desktop and mobile.
- Keyboard, screen reader semantics and Playwright operability demonstrated.
- Regression and migration tests, with measured escape defects and agent correction effort.
- Repository-local canonical work items created by Praxis with provenance linking to this input; do not use GitHub Issues.

## Dependencies and execution
Coordinate Forma contract first, then Forma Studio and Ordo integration, Praxis agent guidance, Visual Engineering evidence, and Folio print extensions. Prefer narrow changes with passing CI; do not claim complete until implementation evidence is committed.

## Additional requirements: expressiveness and flexibility (2026-10-15)

### Layout expressiveness
- Support composable stack, grid, split, dock, flow, canvas, overlay, page and repeater layout models where applicable to this repository; do not require all renderers to implement irrelevant models.
- Apply context-specific invariants: standard forms align fields, canvases permit deliberate free positioning/overlap, and print pages enforce pagination and safe areas.
- Allow nested compositions, responsive container-based adaptation, dense/comfortable modes, themes and white labeling.
- Do not hard-code one visual arrangement, navigation pattern, or form column count as universal.

### Behavioral expressiveness
- Support declarative data binding, computed values, validation, conditional visibility, state-dependent commands and asynchronous feedback.
- Support appropriate drag/drop, reordering, resizing, docking, virtualization, keyboard navigation and Playwright-operable interactions.
- Provide documented extension points for charts, diagrams, maps, photography and other specialized content without bypassing semantic or safety contracts.
- Distinguish hard structural constraints from subjective design guidance. Unsupported but legitimate designs must have a documented, auditable extension path.

### Reference-layout certification
- Maintain a cross-repository reference suite of at least 20 diverse layouts, including dashboard, complex form, mobile wizard, financial ledger, calendar, kanban board, visual workflow editor, marketing page, article, resume, menu, business card and journal publication.
- For each reference, record required capabilities, expected rendering modes, accessibility and machine-operability evidence, and whether custom overrides were needed.
- Acceptance target: 100% of reference layouts representable via documented APIs; 100% of defined invalid structural fixtures rejected; aim for fewer than 10% of reference layouts needing custom exceptions. Targets are proposed and must be validated, not claimed as achieved.
- Measure agent implementation effort, escape-hatch usage, regressions and visual quality separately. Visual taste and composition balance remain reviewable guidance, not arbitrary compile failures.

### Dependencies
- Contract/schema owners must agree on layout model taxonomy and compatibility rules before consumer enforcement.
- Visual Engineering owns the certification matrix and evidence methodology; Forma Studio must exercise both constrained authoring and permitted extensions; Ordo and Praxis must preserve the contract in generated work.
