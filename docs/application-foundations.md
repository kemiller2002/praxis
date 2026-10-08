# Echelon application-foundation verification

Praxis owns the executable cross-capability verification contract for Echelon
applications.

An application declares applicability in `.echelon/foundations.json`. The
declaration is intentionally separate from implementation discovery: Praxis
must not infer that a capability is optional merely because an implementation
is currently missing.

Run:

```bash
./praxis foundations verify
./praxis foundations verify --json
```

Exit code `0` means every required capability passed. Exit code `3` means
the declaration is valid but one or more required foundations failed. Exit code
`2` means the verifier could not evaluate the repository, for example because
the declaration is missing or invalid.

For every required capability the verifier independently checks:

1. **installed/declared** - the canonical dependency or lifecycle component is
   present;
2. **pinned** - no floating application baseline is accepted;
3. **used** - source evidence shows the shared capability is actually consumed,
   so merely adding a package does not satisfy the contract;
4. **evidence/configuration** - required repository evidence exists, such as the
   Aegis boundary declaration or Limen configuration.

Stable finding codes use `ECHELON-FND-<CAPABILITY>-NNN`:

- `001`: required capability is absent;
- `002`: present but not pinned to the declared immutable baseline;
- `003`: installed/pinned but canonical usage is not present;
- `004`: implementation exists but required evidence/configuration is absent;
- `005` (routing only, severity `info`): a check that cannot run yet is
  pending. It is reported but never passes or fails anything.

Only `error` findings fail verification (exit code `3`).

A capability marked `required: false` is reported as `N/A`, not PASS. That
preserves the difference between "not applicable" and "implemented correctly."

## Routing: URL-addressable state (deep linking)

The `routing` capability verifies the parts of `SAF-URL-1..10` that a
repository can show. Those requirements are the portfolio-wide deep-linking
standard in
[`requirements/SHARED-APPLICATION-FOUNDATIONS.md`](../requirements/SHARED-APPLICATION-FOUNDATIONS.md).
Limen 0.9.0 implements them (`DF-LIMEN-2026-0006`, LCP-088..112), and
Praxis checks the inventory and its use without depending on Limen:

```json
"routing": {
  "required": true,
  "hosting": "static",
  "inventory": ".echelon/routes.json"
}
```

`hosting` is `static` (the default: files on GitHub Pages or similar) or
`server` (the application answers every path). `inventory` is one path, or
a list with one inventory per served surface.

- **installed/declared**: each declared inventory exists. Limen's
  `Inventory.render` (F#) or `renderRouteInventory` (TypeScript) writes it.
  Without Limen, start from
  [`templates/application-routes.json`](../templates/application-routes.json).
- **pinned**: the inventory declares `"schema": "echelon.routes/v1"`.
  [`schemas/echelon-routes-v1.schema.json`](../schemas/echelon-routes-v1.schema.json)
  is a byte-for-byte copy of Limen 0.9.0's published `contract/routes.schema.json`
  (LCP-108, `$id` kept). Validate a document's structure against it; the rules
  below are the meaning the verifier adds. A test checks that Limen 0.9.0's
  own rendered inventory (its url-state conformance vector) passes every rule.
- **evidence/configuration**: the inventory satisfies the contract, which
  mirrors Limen's route-table refusals:
  - `mode` is `hash` or `path`, and a `static` application uses `hash`
    (SAF-URL-6, `DF-LIMEN-2026-0006`);
  - `home`, `signIn` and `notFound` name declared routes;
  - route names are unique;
  - patterns are paths whose `{name}`, `{name:type}` or `{*name}`
    placeholders match the declared path parameters;
  - parameter locations and types are legal; path parameters have no
    default and use a path type (a `{*name}` wildcard may be optional); an enum lists values, values are
    unique, and an enum default is one of them;
  - no parameter uses a name Limen reserves for credentials (SAF-URL-5);
  - every legacy entry names a declared route in `to`, and each `{source}`
    in its `params` is a placeholder its `pattern` captures (SAF-URL-7).
- **used**: on Limen 0.9.0 or later, the application references
  `@echelon-foundry/limen/routing`, `EchelonFoundry.Limen.Routing` or
  `Limen.Routing` in source or in a project file (SAF-URL-9).

Finding codes:

| Code | Severity | Meaning |
|---|---|---|
| `ECHELON-FND-ROUTING-001` | error | routing is required but a declared route inventory is missing |
| `ECHELON-FND-ROUTING-002` | error | the inventory is not `echelon.routes/v1` |
| `ECHELON-FND-ROUTING-003` | error | the installed Limen ships routing but the application does not use it |
| `ECHELON-FND-ROUTING-004` | error | the inventory breaks the contract; the message lists every problem with its SAF-URL id |
| `ECHELON-FND-ROUTING-005` | info | Limen routing usage is **pending**; reported, never passed or failed |

Unlike the other capabilities, routing reports inventory problems (004) and
missing Limen usage (003) independently, because one does not imply the
other.

### Limen routing: pending until the application can have it

Limen routing applies only to an application that requires Limen. For any
other application the usage check is N/A. For an application that requires
Limen, the installed version is read from `.echelon/limen.json`
`installedVersion`, or from the declared `limen.version` when the manifest
does not record one. Below 0.9.0 (published 2026-10-08), the usage check is `ECHELON-FND-ROUTING-005`. It is also 005 when
the version is unknown. An info finding never fails verification: the
summary reads `passed (1 note(s))` and the exit code stays `0`. Once the
application moves to Limen 0.9.0, the check is enforced.

If Limen ships the module in another release or under another name, the
declaration can say so: `"limenRoutingVersion": "X.Y.Z"` names the first
release, and `"limenRoutingModule": "<name>"` names the module source must
reference. Neither needs a new Praxis release.

## Ownership

- **Praxis** owns verification semantics, finding codes, and the executable
  command.
- **Conditor** writes/updates the declaration during project initialization and
  invokes Praxis verification after installation.
- **Applications** own applicability and application-specific evidence.
- **Aegis, Forma, Folio, Limen, and Ordo** remain authoritative for their own
  implementation contracts. Praxis verifies consumption; it does not duplicate
  their behavior.

The JSON schema is `schemas/echelon-foundations-v1.schema.json`, and a starting
manifest is `templates/application-foundations.json`.

## Praxis's own foundations

Praxis is itself an Echelon application and is bound by
[`requirements/SHARED-APPLICATION-FOUNDATIONS.md`](../requirements/SHARED-APPLICATION-FOUNDATIONS.md).

### Aegis (SAF-AEGIS-1..6)

- One boundary module, `src/Praxis.Infrastructure/Boundary/AegisBoundary.fs`,
  configures Aegis (released `EchelonFoundry.Aegis.Core` 1.0.0, blocking
  delivery, standard-error sink) and classifies every escaped failure.
- The command line runs each command inside one capture. A failure is
  attributed to the boundary that raised it: thrown inside
  `Praxis.Infrastructure.Git` is `PRAXIS.GIT.FAILURE`; network, process,
  filesystem and repository-state failures are classified by type; anything
  else is `PRAXIS.CLI.UNEXPECTED`.
- `praxis web serve` and `praxis hub serve` capture each request and each
  connection (`PRAXIS.WEB.FAILURE` when nothing more specific applies) and
  answer HTTP 500 with the safe message and an `AG-` reference. The
  exception text never reaches the browser.
- Expected outcomes (refused transitions, failed verification, policy
  refusals) stay typed results with documented exit codes (SAF-AEGIS-2).
  Programming defects and cancellation are re-raised, not disguised.
- Redaction (SAF-AEGIS-5): Aegis's credential rules plus
  `praxis-repository-data` and `praxis-work-content` key rules; credentials
  in exception text (URL user-info, GitHub tokens, bearer/basic headers,
  `token=`/`password=`/`secret=` values) are replaced before the fault is
  recorded; only command words are public context and the repository root is
  masked.
- [`aegis-boundaries.json`](../aegis-boundaries.json) declares the codes;
  `tests/Praxis.Tests/AegisBoundaryTests.fs` drives the boundary with a
  replaceable collector sink and asserts the manifest equals the module
  (SAF-AEGIS-6).

### Requirement status

| Requirement | Status | Evidence or open item |
|---|---|---|
| SAF-AEGIS-1 | Met | Boundary module; CLI, web and hub captures; `AegisBoundaryTests` |
| SAF-AEGIS-2 | Met | Typed exit codes unchanged; "a completed command keeps its exit code and records nothing" |
| SAF-AEGIS-3 | Met | Shell launchers return non-zero diagnostics (no JavaScript edges, RQ-ROS-2026-A024) |
| SAF-AEGIS-4 | Met | Idempotent remote and reconcile paths (RemoteProtocolTests, WorkReconciliationTests) |
| SAF-AEGIS-5 | Met | Redaction rules and scrubber; "credentials, repository data and work content never reach a sink" |
| SAF-AEGIS-6 | Met | Released 1.0.0 pin; collector-sink tests |
| SAF-FORMA-1 | Met | `vendor/forma/forma.lock` pins the Forma 0.4.1 release tarball by URL and sha256; "forma: the vendored tarball is the pinned immutable release artifact" |
| SAF-FORMA-2 | Met | Web and hub markup use Forma patterns; shipped `styles.css` files carry no rules (guarded by test) |
| SAF-FORMA-3 | Met | No Forma CSS is copied; the stylesheet is extracted at runtime from the verified release tarball |
| SAF-FORMA-4 | Met | Native forms, tables, landmarks; Forma only presents |
| SAF-FORMA-5 | Met | Forma skip link, focus, responsive data grids; status lozenges carry the status word, not color alone |
| SAF-FORMA-6 | Met | Faults use Forma's inline fault and fault-banner patterns with the Aegis reference |
| PRX-UI-030 | Met | As SAF-FORMA-1..6 |
| PRX-UI-031 | Not applicable (decision) | Script-free, server-rendered UI has no browser runtime for Limen; recorded in `docs/web-interface.md` and `.echelon/foundations.json` |
| SAF-FOLIO-1..3 | Not applicable | No printable or PDF surface exists (conditional requirement) |
| SAF-URL-1 | Met | Every GET page is resolved from its path and query alone by `Praxis.Application.Web.UrlState` (Limen.Routing) before rendering; no authentication, so a cold load needs no sign-in round trip |
| SAF-URL-2 | Met | Limen.Routing's canonical form: empty GET-form fields are dropped, free-text or repeated `tag` values become one sorted set, and any non-canonical location (including `/index.html` and undeclared parameters) answers 303 to the canonical URL; "url state: canonical locations ..." and "web serve: a non-canonical page URL redirects ..." |
| SAF-URL-3 | Partial | Links and post/redirect/get push; Back, Forward and reload show the URL's view, and canonicalization replaces via redirect. A script-free filter submit still pushes rather than replaces; replacing needs browser script, escalated to the owner (`DF-ROS-2026-A057` item 6, WI-0082) |
| SAF-URL-4 | Met | Typed refusals with a way back: unknown page 404 "Not found", invalid parameter 400 "Invalid link", denied view 403 "Not permitted"; an unknown work item keeps its 404 page linking to the queue |
| SAF-URL-5 | Met | URLs carry only ids and filters; post/redirect/get feedback moved from `?notice=`/`?error=` to a 60-second HttpOnly, SameSite=Strict flash cookie cleared once shown |
| SAF-URL-6 | Not applicable | Server-rendered on localhost, not statically hosted (`"hosting": "server"`, inventories in `path` mode) |
| SAF-URL-7 | Met | `/index.html` is a legacy route of both tables and redirects to `/`; no route has been renamed |
| SAF-URL-8 | Met | `.echelon/routes.json` and `.echelon/routes.hub.json` are `Inventory.render` of the two tables, held byte-equal by "url state: .echelon/routes.json and routes.hub.json are Inventory.render ..."; `foundations verify` checks both |
| SAF-URL-9 | Met | `RouteTable.define` tables and typed `RouteCodec`s; `UrlState.format` is the route formatter; "url state: format then resolve is the view, for generated views" is the round-trip property test |
| SAF-URL-10 | Partial | Every page ends with "Link to this view": its canonical URL as a link and a selectable absolute address, copied with the browser's Copy Link. One-action clipboard copy needs browser script, escalated with SAF-URL-3 (`DF-ROS-2026-A057`, WI-0082) |
| SAF-DEP-1 | Met | `.echelon/foundations.json` declares Aegis 1.0.0 (NuGet), Forma 0.4.1 (release tarball lock) and Ordo.Core 1.5.0 (release nupkg lock); every pin is a released version or immutable artifact |
| SAF-DEP-2 | Met | "foundation verifier: Praxis's own repository passes its declared foundations" runs `foundations verify` on this repository in the suite; Aegis boundary tests and Forma presentation tests are the behaviour evidence |

### Forma (SAF-FORMA-1..6)

`praxis web serve` and `praxis hub serve` consume the pinned Forma release
in `vendor/forma/` (see [`web-interface.md`](web-interface.md)). Praxis has no
npm, so the verifier accepts that lock as Forma's pin when the tarball's
sha256 matches it.

### Declaration

`.echelon/foundations.json` requires Aegis, Forma, Ordo and routing. Folio is not
applicable: Praxis has no printable or PDF surface (SAF-FOLIO-1 is
conditional). Limen is not applicable: the web and hub UIs are script-free
and server-rendered (PRX-UI-031, see `web-interface.md`). Routing is required:
`.echelon/routes.json` and `.echelon/routes.hub.json` are the inventories of the web and hub UIs,
rendered by Limen.Routing. The UIs consume `EchelonFoundry.Limen.Routing`
0.9.0 as a pinned engine library (`vendor/nuget/limen-routing.lock`,
`DF-ROS-2026-A057`); the `limen` capability, which is the browser runtime,
stays N/A, so the verifier reports Limen routing usage as N/A. Praxis is not
required of itself: this repository is Praxis's source and runs its own
build. The suite runs `foundations verify` against this repository, so a
missing pin, unused dependency or absent boundary manifest fails CI.
