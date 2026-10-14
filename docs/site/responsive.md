# Public site responsive evidence (PRAXIS-SITE-18)

Date: 2026-09-27. Chromium via Playwright, fallback fonts (web fonts were not
reachable from the build container). One page, five widths, each measured and
inspected as a full-page render.

| Width | Class | Horizontal overflow | Nav rows | `h1` size | Page height |
| --- | --- | --- | --- | --- | --- |
| 320px | narrow mobile | 0px | 3 | 44px | 31,626px |
| 375px | mobile | 0px | 3 | 44px | 28,887px |
| 768px | tablet | 0px | 1 | 81px | 19,802px |
| 1280px | desktop | 0px | 1 | 124px | 18,044px |
| 1920px | wide | 0px | 1 | 124px | 18,049px |

## Layout intent

- **Narrow and mobile (below 30rem / 48rem).** Single column. Navigation wraps
  onto several rows of 44px targets; there is no script-driven menu. Execution
  records stack label over value, lifecycle statuses drop below their step,
  and the Git comparison table reflows to labelled blocks.
- **Tablet (48rem).** Two-column grids for the execution chain, agent
  properties, record properties and current-versus-direction panels. Claim
  answers go to a three-column question, answer, status row.
- **Desktop (60rem, 72rem).** Section headings take a 14rem label column; the
  hero body moves beside the headline; the chain goes to three columns; the
  GH-84 executions sit side by side with the parent link between them.
- **Wide.** Content stops at a 76rem measure and centres; body text keeps its
  own 38-44rem measure.

## Found and fixed while checking

- At 768px the three-column execution chain squeezed each link to about
  150px of text. It now uses two columns from 48rem and three from 72rem.
- The tablet render exposed a garbled sentence in the reconciliation
  introduction, left by an earlier shell edit in which `sed` treated `&` as
  the match. The sentence was repaired, and the content tests (now `tests/Site.Tests/ContentTests.fs`)
  fails on entities missing their ampersand, duplicated sentences and
  run-together words.

## Wide content

Two elements are allowed to be wider than a phone: the record-location tree
and the GH-84 ledger table (min-width 34rem). Each scrolls inside its own
focusable, named region; the page itself never scrolls sideways.
`tests/Site.Tests/ResponsiveTests.fs` fails if any other fixed width over 320px
appears.
