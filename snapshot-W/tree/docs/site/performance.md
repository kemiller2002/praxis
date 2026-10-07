# Public site performance and resilience evidence (PRAXIS-SITE-19)

Date: 2026-09-27. Measured on the build container against
`node site-tools/serve.mjs` (loopback, no network latency), Chromium via
Playwright. Loopback timings show relative behaviour, not what a visitor on a
real network will see.

## What ships

| File | Bytes | gzip -9 |
| --- | --- | --- |
| `index.html` | 57,874 | 11,899 |
| `assets/css/site.css` | 32,585 | 6,086 |
| `assets/js/claim.js` | 2,459 | 983 |
| `data/gh-84.json` (linked, not loaded by the page) | ~27,500 | ~2,800 |

First-party requests on load: three (HTML, CSS, one deferred script). No
images, no web-font files, no framework, no bundle, no source maps, no
analytics, no cookies, no storage. Budgets are enforced by
`tests/site/performance.test.mjs` (gzip: HTML 20 KB, CSS 10 KB, JS 2 KB).

## Third party

Only Google Fonts (Newsreader, Manrope, IBM Plex Mono, `display=swap`). No font
binaries are committed.

**Found and fixed.** The font stylesheet was render-blocking: with the font host
artificially delayed by 4 s, the page could not paint until it answered. It is
now loaded with `media="print"` and switched on by an `onload` handler, and the
`<noscript>` blocking fallback was removed.

After the fix, with the font host delayed by 4 s:

| JavaScript | DOMContentLoaded | First contentful paint | `load` event |
| --- | --- | --- | --- |
| on | 34 ms | ~200 ms (first sample taken) | 4,031 ms (waits for the font host) |
| off | 19 ms | not measurable without JS; render not blocked | 4,024 ms |

Without JavaScript the browser still downloads the font stylesheet at low
priority (it is a print stylesheet), but never applies it, so the page renders
in the local fallback stacks. The `load` event waiting on the font host
does not delay reading.

## Resilience

- **Without web fonts.** Every rendered check for this site (accessibility,
  responsive, screenshots) ran with Google Fonts unreachable. The layout and
  hierarchy hold on Georgia/system sans/system monospace.
- **Without JavaScript.** All content, including the full "Done is a claim"
  interrogation and every execution record, is in the HTML. The script only
  adds the step-by-step reveal.
- **Without the evidence snapshot.** The page does not fetch
  `data/gh-84.json`; it links to it. Values are rendered into the HTML at
  build time by `site-tools/evidence.mjs --render` and verified by
  `--check`.
- **Reduced motion.** Motion is removed, not shortened.

## Not measured

- Real-network loading (no throttled or field measurements; no Lighthouse run).
- Rendering and file sizes of the Google Fonts themselves.
