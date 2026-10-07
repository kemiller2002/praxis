# Public site security and privacy review (PRAXIS-SITE-20)

Date: 2026-09-27. Scope: everything under `site/`, the generator
`site-tools/evidence.mjs`, the preview server `site-tools/serve.mjs`, and
the deployment workflow. Enforced by `site-tools/check.mjs`,
`tests/site/security.test.mjs` and `tests/site/evidence.test.mjs`.

## What the site is

A static page and one static JSON file. No server code, forms, accounts,
cookies, storage, analytics, or network calls from script. The one script
(`claim.js`) only toggles `hidden` on elements already in the page.

## Controls

| Concern | Control | Checked by |
| --- | --- | --- |
| Script injection | CSP (meta; Pages cannot set headers): `default-src 'none'`; scripts only from self plus one hashed inline handler; no `unsafe-inline`, `unsafe-eval` or wildcards; `base-uri 'none'`; `form-action 'none'` | `security.test.mjs`; rendered with no CSP violations |
| Third parties | Only Google Fonts (CSS and font files). No third-party script | `performance.test.mjs`, `security.test.mjs` |
| Referrer leakage | `strict-origin-when-cross-origin` | `security.test.mjs` |
| External links | https only, to github.com, raw.githubusercontent.com and Google Fonts; no `target="_blank"` | `security.test.mjs` |
| Operational interface | No loopback hosts, local ports, `/api/` paths or references outside `site/` | `check.mjs` boundary |
| Secrets | Credential patterns (`sk-`, `gh*_`, `github_pat_`, PEM blocks) rejected in every text file under `site/` | `check.mjs` boundary |
| Session identifiers | The generator copies no session, conversation or run IDs; UUIDs and `sessionId`-style fields are rejected | `evidence.test.mjs`, `check.mjs` |
| Local paths | `/home`, `/Users`, `/root`, `/tmp`, `/var` and Windows paths rejected | `check.mjs` boundary |
| Preview server | Loopback only, `site/` only, rejects traversal, not used for deployment | `site.test.mjs` |

Known limits of a meta CSP: `frame-ancestors`, `report-uri` and `sandbox` are
ignored in a meta element, so clickjacking protection is not provided. The page
has no state-changing interaction for framing to abuse.

## Privacy

- The site sets no cookies, uses no storage, and loads no analytics.
- With JavaScript on, the Google Fonts stylesheet and font files are fetched
  from Google, which sees the visitor's IP address and user agent. Without
  JavaScript, browsers may still download the stylesheet (it is marked
  `media="print"`) but do not apply it. The footer states that fonts come from
  Google Fonts. Self-hosting the fonts would remove this request. That was not
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
