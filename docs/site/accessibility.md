# Public site accessibility evidence (PRAXIS-SITE-17)

Accessibility is a release requirement for `site/`. This file records what was
checked, how, and what was found and fixed. Target: WCAG 2.2 AA.

Date: 2026-09-27. Browser: Chromium (Playwright 1.56, headless). Page served by
`node scripts/site/serve.mjs`. Web fonts were unreachable from the build
container, so every rendered check ran on the fallback font stacks. That is the
harder case for layout, but it means the Google Fonts rendering was not
inspected.

## Automated, in the repository (runs in CI)

`npm run site:check` runs:

- `scripts/site/check.mjs`: `lang`, a title, the four landmarks, one `main`,
  one `h1`, no skipped heading levels, unique ids, in-page links and
  `aria-labelledby` targets that exist, image alternatives, accessible names
  for links and buttons, meaningful link text, and a skip link to `#main`.
- `tests/site/design.test.mjs`: WCAG contrast for every text/background token
  pair the stylesheet uses (4.5:1 text, 3:1 focus indicator), square geometry,
  visible focus, reduced motion.
- `tests/site/accessibility.test.mjs`: scrollable regions are focusable named
  regions, `aria-label` only where the role allows it, no hover-only changes,
  44px targets for buttons and navigation, reduced motion removes motion,
  the claim enhancement uses a status live region and a real `button`.
- `tests/site/content.test.mjs`: the "Done is a claim" component is complete
  in the static HTML (nothing hidden without JavaScript).

## Rendered checks (run for this change; tooling kept out of the repository)

axe-core 4.13.0 was injected into the rendered page with rule sets `wcag2a`,
`wcag2aa`, `wcag21a`, `wcag21aa`, `wcag22aa` and `best-practice`.

| Run | Violations before fixes | Violations after |
| --- | --- | --- |
| 1280px, JavaScript on | 2 rules (6 nodes) | 0 (48 rules passed) |
| 320px, enhancement script blocked | 2 rules (6 nodes) | 0 (46 rules passed) |
| 1280px, reduced motion, claim fully interrogated | 2 rules (6 nodes) | 0 (48 rules passed) |

Found and fixed:

1. `color-contrast`, 5 nodes, 1.11:1. A stone-section rule set status labels to
   charcoal and also reached into the dark illustrative execution record. The
   rule is now scoped to the outcomes list.
2. `scrollable-region-focusable`, 1 node. The record-location tree scrolls
   horizontally on narrow screens but could not be reached by keyboard. It is
   now a focusable named region, as is the ledger table's scroll container.
3. `aria-prohibited-attr` (reported as needs-review, 2 nodes): `aria-label` on
   `pre`. Replaced by `figure` with a visually hidden `figcaption`.

Keyboard traversal (Tab through the whole page, reduced motion so scrolling is
immediate), at 1280px and 320px:

- 35 focus stops, in reading order, starting with "Skip to content".
- Every stop showed an outline of at least 2px and was scrolled into view.
- The skip link is visible when focused and moves focus to `#main`.
- No non-inline control is smaller than 24 by 24 CSS pixels; buttons,
  navigation and footer links are at least 44px tall.
- The claim control works with Enter; progress is announced through a
  `role="status"` region ("3 of 8 questions answered", then "All 8 questions
  answered.").

Found and fixed: the reduced-motion rule set `transition-duration: 0.01ms` on
every element, which gave every property change an (imperceptible) transition
and made the skip link's first frame lag. It now sets `transition: none` and
`animation: none`.

Text spacing (WCAG 1.4.12: line height 1.5, letter spacing 0.12em, word
spacing 0.16em, paragraph spacing 2em) at 320px: no horizontal page scroll.
Three lifecycle timestamps in the execution records overflowed their column;
they now wrap, and the status moves below the step on narrow screens. After
the fix, no element is clipped.

Reflow: no horizontal page scroll at 320px (equivalent to 400% zoom at 1280px).
Wide content (the file tree, the ledger table) scrolls inside its own
focusable region.

## Reasoned review

- **Headings.** One `h1` (the hero claim); one `h2` per section in narrative
  order; `h3` within sections. Screen reader heading navigation reads as the
  page's argument.
- **Status is never colour alone.** Every status has a text label and a
  symbol: check for verified or evidence, filled circle for recorded or
  derived, question mark for unknown or unavailable, open circle for ready,
  ellipsis for active or pending, cross for unresolved.
- **Tables.** The Git comparison and the ledger are real tables with captions
  and `scope`d headers. On narrow screens the comparison reflows to labelled
  blocks without losing header association in the markup.
- **No JavaScript.** Everything, including the full claim interrogation, is
  present without JavaScript. The script only hides answers until the visitor
  asks, and it never moves focus.
- **Motion.** The only animation is the reveal of claim answers, which is
  opt-in (after a button press) and removed under reduced motion.
- **Language and naming.** `lang="en"`; external links are named by their
  destination; the GitHub navigation link's arrow is hidden from assistive
  technology.

## Not verified

- Screen reader output was reasoned from the accessibility tree, not heard with
  VoiceOver, NVDA or JAWS.
- Windows High Contrast / forced colours was not rendered.
- Rendering with the actual web fonts was not inspected (fonts blocked in the
  build container).
