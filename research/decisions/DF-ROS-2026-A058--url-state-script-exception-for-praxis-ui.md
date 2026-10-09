---
id: DF-ROS-2026-A058
title: Praxis's web and hub UIs may load one approved browser script, url-state.js, for replace-on-refine and one-action Copy link, as a progressive enhancement under a strict CSP
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-09
updated: 2026-10-09
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A024]
related_documents:
  - research/decisions/DF-ROS-2026-A057--praxis-ui-url-state-through-limen-routing.md
  - requirements/SHARED-APPLICATION-FOUNDATIONS.md
  - docs/web-interface.md
  - docs/application-foundations.md
  - RQ-ROS-2026-A024
tags: [foundations, deep-linking, web, security, exception, decision]
confidence: high
derived_from: [DF-ROS-2026-A057]
provenance:
  contributions:
    EXE-20261009T012610078Z-26160b5b:
      operations: [created]
      at: 2026-10-09T01:37:15.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "WI-0082: records the owner's approval (relayed by the coordinating session) of the url-state.js exception and its limits"
---

# Context

`DF-ROS-2026-A057` brought Praxis's `web serve` and `hub serve` up to
`SAF-URL-1..10` through `EchelonFoundry.Limen.Routing`, script-free. It left
two behaviours Partial, because neither can be done without browser script:

- **SAF-URL-3.** A filter refinement should replace the history entry. A plain
  GET form submission always pushes one, and HTTP has no response that
  replaces one.
- **SAF-URL-10.** "Link to this view" should copy in one action. Only
  `navigator.clipboard` can write the clipboard.

Script needs an exception. `RQ-ROS-2026-A024` prohibits repository-owned
JavaScript unless an accepted decision names it, and `PRX-UI-031` kept the UIs
script-free. `DF-ROS-2026-A057` item 6 escalated the question to the owner as
WI-0082.

The owner chose option (a): approve one small script as a named exception,
under the conditions below. The coordinating session relayed the decision on
2026-10-09.

# Decision

1. **One named exception.** The exception covers exactly one file,
   `src/Praxis.Application/Web/url-state.js`. `ros.json`
   `implementationPolicy.exceptions` lists that exact path (wildcards are
   refused) and cites this decision. No other JavaScript is permitted.

2. **It does two things and nothing else.**
   - *Refinement replaces.* A filter form marked `data-refine` (the web
     queue's and the hub's) is submitted with `location.replace` to the same
     action, with empty fields left out. Replacement is a navigation, so the
     history entry is replaced, not pushed. A non-canonical spelling still
     meets the server's 303, which also replaces.
     - `location.replace` is used rather than `history.replaceState`.
       `replaceState` alone changes only the address bar, and loading the new
       view without navigating would need `fetch`, which this decision rules
       out. `location.replace` is the navigating form of the same replace
       semantics.
   - *Copy link.* The "Link to this view" section carries a `Copy link`
     button, rendered `hidden`. The script reveals the button only when
     `navigator.clipboard` exists. A click writes the field's absolute URL to
     the clipboard and announces the result in a `role="status"` region. If
     the write is refused, the script selects the field so a person can copy
     it by keyboard.

3. **Progressive enhancement.** With JavaScript off, every page works exactly
   as under `DF-ROS-2026-A057`:
   - forms submit and push history;
   - the hidden button never appears;
   - the read-only address field can be selected by hand.

   No page depends on the script for content or for any operation.

4. **Delivery and CSP.**
   - The script is embedded in `Praxis.Application` and served at
     `/url-state.js` from the UI's own origin.
   - Every page loads it with `<script src="/url-state.js"
     integrity="sha256-…" defer>`. The integrity value is computed from the
     embedded bytes at build time, so the page pins exactly the bytes the
     server serves.
   - Every response carries this policy:

     ```text
     default-src 'self'; script-src 'self'; connect-src 'none';
     img-src 'self' data:; font-src 'self' data:; object-src 'none';
     base-uri 'none'; form-action 'self'; frame-ancestors 'none';
     require-trusted-types-for 'script'; trusted-types 'none'
     ```

   - The policy grants no `'unsafe-inline'`, `'unsafe-eval'` or
     `'unsafe-hashes'`.
   - `connect-src 'none'` makes network access impossible from script, and
     Trusted Types forbid string-to-DOM sinks.
   - Refusal pages carry the policy but no script.

5. **Forbidden.** The script may not use any of the following:
   - network access: `fetch`, XHR, WebSocket, EventSource, beacons;
   - storage: local or session storage, IndexedDB, Cache Storage, cookies;
   - code from text: `eval`, `Function`, string timers;
   - module imports, workers or messaging;
   - HTML injection;
   - history pushes;
   - clipboard reads.

6. **Guarded by tests.**
   - `UrlEnhancementTests` audits the script's identifiers against an exact
     allowlist, so any new API fails the build until this decision and the
     list are amended together.
   - It also names the forbidden capabilities, caps the file at 40 lines,
     checks that the integrity and the CSP have no unsafe grants, checks that
     `ros.json` excepts only this script, and checks that pages carry exactly
     the one pinned script.
   - The browser suite (`Praxis.Tests --suite browser`, a CI step on headless
     Chrome) proves both behaviours on the web UI and the hub, with no console,
     CSP or script errors.
   - The same suite proves the no-JS fallback with page script disabled.

# Consequences

- With this decision, SAF-URL-3 and SAF-URL-10 are Met for Praxis's own UIs.
- `PRX-UI-031` now reads "server-rendered, script-optional": the UIs never
  depend on script, and the only script they may load is this one.
  `.echelon/foundations.json` keeps Limen's browser runtime not applicable,
  because this script is not a Limen runtime.
- Changing what the script does means amending this decision, the allowlist
  in `UrlEnhancementTests` and the browser tests in the same change.
- The browser suite needs Chrome or Chromium. CI uses the runner's Chrome, and
  locally `PRAXIS_TEST_BROWSER` names one. The suite fails, rather than
  skipping, when no browser is found.
