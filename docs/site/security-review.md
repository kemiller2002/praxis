# Public site security and privacy review (PRAXIS-SITE-20)

Date: 2026-09-27, updated 2026-09-28 for the removal of the page's script and
the port of the site tooling to F#. Scope: everything under `site/`, the
generator (`praxis-site evidence`, `site-tools/Evidence.fs`), the preview
server (`praxis-site serve`, `site-tools/Serve.fs`), and the deployment
workflow. Enforced by `praxis-site check`, `tests/Site.Tests/SecurityTests.fs`
and `tests/Site.Tests/EvidenceTests.fs`.

## What the site is

A static page and one static JSON file. No server code, forms, accounts,
cookies, storage, analytics, or script. The page's one script (`claim.js`,
which only toggled `hidden` on elements already in the page) was removed on
2026-09-28.

## Controls

| Concern | Control | Checked by |
| --- | --- | --- |
| Script injection | CSP (meta; Pages cannot set headers): `default-src 'none'`; `script-src 'none'`, so no script and no inline handler can run; no `unsafe-inline`, `unsafe-eval`, hashes or wildcards; `base-uri 'none'`; `form-action 'none'` | `SecurityTests.fs`; rendered with no CSP violations (2026-09-27, before the script was removed; not re-rendered) |
| Third parties | Only Google Fonts (CSS and font files). No script at all | `PerformanceTests.fs`, `SecurityTests.fs` |
| Referrer leakage | `strict-origin-when-cross-origin` | `SecurityTests.fs` |
| External links | https only, to github.com, raw.githubusercontent.com and Google Fonts; no `target="_blank"` | `SecurityTests.fs` |
| Operational interface | No loopback hosts, local ports, `/api/` paths or references outside `site/` | `praxis-site check` boundary |
| Secrets | Credential patterns (`sk-`, `gh*_`, `github_pat_`, PEM blocks) rejected in every text file under `site/` | `praxis-site check` boundary |
| Session identifiers | The generator copies no session, conversation or run IDs; UUIDs and `sessionId`-style fields are rejected | `EvidenceTests.fs`, `praxis-site check` |
| Local paths | `/home`, `/Users`, `/root`, `/tmp`, `/var` and Windows paths rejected | `praxis-site check` boundary |
| Preview server | Loopback only, `site/` only, rejects traversal, not used for deployment | `SiteTests.fs` |

Known limits of a meta CSP: `frame-ancestors`, `report-uri` and `sandbox` are
ignored in a meta element, so clickjacking protection is not provided. The page
has no state-changing interaction for framing to abuse.

## Privacy

- The site sets no cookies, uses no storage, and loads no analytics.
- The Google Fonts stylesheet and font files are fetched from Google, which
  sees the visitor's IP address and user agent. The footer states that fonts
  come from Google Fonts. Self-hosting the fonts would remove this request. That was not
  done because the specification says not to commit font binaries.

## Public/private evidence boundary

What the snapshot may carry is documented in
[`../public-site.md`](../public-site.md#public-and-private-evidence). Each
field is copied deliberately; nothing is passed through wholesale.

## Finding outside this change (for maintainers)

The repository's own execution records under `.ros/telemetry/executions/`
are committed, and 102 of the 105 present on 2026-09-27 carry the runtime's
raw `identity.sessionId` (for example a Claude Code session ID). The repository
is public, so those IDs are already public. The site does not republish them.
Whether the protocol should keep committing raw session IDs, or hash them, is a
product decision for the Praxis maintainers. It is not changed here.
