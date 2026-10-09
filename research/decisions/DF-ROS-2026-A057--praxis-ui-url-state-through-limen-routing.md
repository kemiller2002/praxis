---
id: DF-ROS-2026-A057
title: Praxis's web and hub UIs keep their URL state through the pinned Limen.Routing engine library, stay script-free, and escalate the two SAF-URL behaviours that need browser script
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-08
updated: 2026-10-08
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A024]
related_documents:
  - requirements/SHARED-APPLICATION-FOUNDATIONS.md
  - docs/application-foundations.md
  - docs/web-interface.md
  - docs/project-administration-hub.md
  - RQ-ROS-2026-A024
tags: [foundations, deep-linking, routing, web, decision]
confidence: high
derived_from: []
provenance:
  contributions:
    EXE-20261008T231313875Z-08e02d5e:
      operations: [created]
      at: 2026-10-08T23:33:41.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: claude-opus-5-5
        runtime: claude-code
      reason: "WI-0078: Praxis UI URL state through Limen.Routing; script-dependent SAF-URL behaviours escalated"
---

# Context

`SAF-URL-1..10` (`requirements/SHARED-APPLICATION-FOUNDATIONS.md`) binds
Praxis's own `praxis web serve` and `praxis hub serve`. Before WI-0078 they
met it in part:

- the GET filter forms left empty parameters in the URL;
- `tag` could be repeated;
- an unknown page path answered a bare plain-text 404;
- no formatter existed, and no link-sharing action;
- post/redirect/get feedback travelled as `?notice=`/`?error=`.

Limen 0.9.0 publishes `EchelonFoundry.Limen.Routing`, the pure F# engine
library behind the standard. It depends on nothing but FSharp.Core and the
BCL. It is not on nuget.org; it is a GitHub release asset with a
build-provenance attestation, recorded in echelon-registry as `limen-fsharp`
0.9.0.

Two constraints shape what the UIs may do:

- `PRX-UI-031` (`docs/web-interface.md`) keeps the UIs script-free and
  server-rendered. The Limen browser runtime is therefore not applicable, and
  `.echelon/foundations.json` keeps `limen` N/A.
- `RQ-ROS-2026-A024` prohibits repository-owned JavaScript execution unless an
  approved exception, citing an accepted decision, names it.

# Decision

1. **Limen.Routing owns the UIs' URL space.**
   - `Praxis.Application.Web.UrlState` defines both route tables with
     `RouteTable.define`, in path mode, because the servers answer every
     path. It maps them to typed views with `RouteCodec`.
   - Every GET page resolves to a canonical view, a canonical redirect or a
     typed refusal (`RouteError`), before anything is rendered.
   - `.echelon/routes.json` and `.echelon/routes.hub.json` are the tables'
     `Inventory.render` output, held byte-equal by a test.
   - The package is vendored like Ordo.Core: `vendor/nuget`, a lock, and a
     NuGet.config mapping to that source only. It is a pinned engine library,
     not the Limen browser runtime, so `PRX-UI-031` stands.
2. **Canonical URLs (SAF-URL-2).**
   - A small pure adapter first drops the empty fields an HTML GET form
     submits and merges free-text or repeated tags into one sorted set.
   - Any location that is not canonical is answered with 303 to the canonical
     one. That includes empty fields, reordered or undeclared parameters, and
     the `/index.html` alias.
   - The comparison is of decoded meaning, not encoding, so a canonical
     location can never redirect again.
3. **Typed outcomes (SAF-URL-4).**
   - An unknown page is 404 "Not found". An invalid parameter value is 400
     "Invalid link", naming the value and what was expected. A denied view
     would be 403 "Not permitted".
   - Each has a way back. An unknown work item keeps its existing 404 page
     with a link to the queue.
4. **Transient feedback leaves the URL (SAF-URL-5).**
   - Post/redirect/get feedback travels in a 60-second `HttpOnly`,
     `SameSite=Strict` flash cookie.
   - The page that shows it clears it.
5. **Link to this view (SAF-URL-10, script-free).**
   - Every page ends with its canonical URL as a link, plus a read-only field
     holding the absolute URL.
   - A person copies it with the browser's own Copy Link, or by selecting the
     field.
6. **Escalated, not decided here: the two behaviours that need browser script.**
   - *Refinement replaces history (SAF-URL-3).* A script-free GET form
     submission always pushes a history entry. HTTP has no response that
     replaces one, and only `history.replaceState` can.
   - *One-action clipboard copy (SAF-URL-10).* Writing the clipboard needs
     `navigator.clipboard`.

   Either one is repository-owned JavaScript. This decision does not approve
   it: an exception to `RQ-ROS-2026-A024` and `PRX-UI-031` is the owner's
   call. The options are:

   - **(a)** approve one CSP-hashed inline enhancement script (replace on
     filter submit, copy on click) as a named exception, recorded in
     `ros.json` `implementationPolicy.exceptions`;
   - **(b)** accept the script-free UI's pushes on refinement, and
     copy-by-browser, as a documented deviation.

   Until the owner decides, SAF-URL-3 and the one-action part of SAF-URL-10
   are Partial for Praxis's own UIs. The question is a captured backlog item.

# Consequences

- Praxis gains its first vendored Echelon engine library besides Ordo.Core.
  Upgrading it means replacing the package, the lock and the PackageReference
  together, as for Ordo.
- The UIs' inventories are generated, not hand-written, so they cannot drift
  from the routes the servers resolve.
- The CLI adapter stays within its PRX-ARCH-002 ratchet. The URL logic lives
  in `Praxis.Application.Web`, and pure header parsing moved there too.
