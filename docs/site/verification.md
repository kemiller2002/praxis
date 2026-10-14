# Public site final verification (PRAXIS-SITE-26)

Date: 2026-09-27, branch `claude/gh-84`, pull request #85. Everything below
was run for this record. Nothing is carried over from earlier runs unless it
says so.

## Repository checks

| Check | Result |
| --- | --- |
| `node site-tools/verify.mjs` (site check, evidence check, 99 site tests) | passed; 99/99 |
| `node site-tools/assemble.mjs _site` (artifact assembled and re-checked) | passed |
| `./ros validate` | validation passed |
| Repository Node tests (`echelon-doctor`, `npm-bootstrap`, `lifecycle-package`, `ros-fs-launcher`, `ros-server`, `ros-hub`, `artifact-compatibility`) | 94/94 |
| Repository Python tests | 7/7 (OK) |
| `site.yml`, `site-pages.yml` parse | ok (`check`; `build`, `deploy`) |
| Fresh clone, site checks and assembly (run at PRAXIS-SITE-21) | 90/90 then; not re-run after the tooling move, which CI covers |

The F# test suite (`npm run test:fsharp`) was not run locally. CI's
`ROS validation` workflow runs `npm run test:all` on every push.

## GitHub Actions on this branch

All completed runs of `Public site`, `ROS validation` and
`Native Praxis release` (a pull-request build that never publishes) concluded
`success`. The only exception is `Native Praxis release` runs cancelled when a
newer push superseded them; that workflow cancels in-progress pull-request
runs by design. The `Public site` check job passed on GitHub in about 7
seconds with Node only. The runs on the last commits were still in progress
when this was written; the pull request shows their outcome.

## Rendered checks (Chromium, fallback fonts)

| Check | Result |
| --- | --- |
| axe-core 4.13, WCAG 2.2 AA + best practice: 1280px with JS, 320px without enhancement, 1280px after full interrogation | 0 violations, 0 needing review, in all three |
| Keyboard traversal, 1280px and 320px | 35 stops each; all outlined; all scrolled into view; skip link reaches `#main` |
| Horizontal overflow at 320, 375, 768, 1280, 1920px | 0px at every width |
| "Done is a claim": JS / reduced motion / no JS | 9 of 9 answers shown in each; progress announced; no page errors |
| Transfer (gzip -9): HTML / CSS / JS | 12,429 / 6,088 / 983 bytes |

Details and what each check found and fixed:

- `accessibility.md`
- `responsive.md`
- `performance.md`
- `security-review.md`
- `claim-audit.md`
- `get-started-verification.md`

## Evidence chain

`site/data/gh-84.json` was regenerated from the `.ros` records at the start
of this item (as of 2026-09-27T08:39Z): 25 of 26 site items complete, this
one active, 28 executions, and GH-84 blocked. The page's ledger and every
`data-evidence` value were re-rendered from it, and `evidence.mjs --check`
confirms the page matches the snapshot and the snapshot matches the records.
Completing this item happens after the snapshot, so the published ledger
shows PRAXIS-SITE-26 as active. That is the truth as of the snapshot, not an
error.

## GH-84 is not complete

GH-84 was **blocked**, not completed, with the reason recorded:

> Site built and verified (PRAXIS-SITE-01..25); awaiting human review and
> merge of PR #85 and the one-time Pages setting (docs/site/site-deployment.md).
> Deployment is not yet captured, so GH-84 is not complete.

The issue asks for the site's deployment to be captured by Praxis. Its
acceptance ("a developer understands within seconds…") is a human judgement.
Neither can be evidenced from this branch.

## Deployment

**Not deployed.** `site-pages.yml` has never run: it runs only on `main`.
Whether Pages is enabled is unknown; the build environment could not reach the
Pages API or `github.io`. See `site-deployment.md`.

## Later change (2026-09-28)

This record describes the Node-based tooling of 2026-09-27. The site tooling
and tests have since been ported to F# (`site-tools/SiteTools.fsproj`,
`tests/Site.Tests/Site.Tests.fsproj`), and the page's only script (`claim.js`)
was removed. The equivalent checks are now
`dotnet run --project site-tools/SiteTools.fsproj -c Release -- verify` and
`-- assemble _site`. The results above were not re-run with the new tooling
except where another document says so.
