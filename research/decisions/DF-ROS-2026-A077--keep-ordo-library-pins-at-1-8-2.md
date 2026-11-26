---
id: DF-ROS-2026-A077
title: Praxis keeps its vendored Ordo.Core and Ordo.Construction library pins at 1.8.2; echelon-current selects only the Ordo CLI
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-11-26
updated: 2026-11-26
research_area: repository-operating-system
decision_type: dependency
supports: []
related_documents:
  - vendor/nuget/ordo-core.lock
  - vendor/nuget/ordo-construction.lock
  - .echelon/foundations.json
  - docs/construction.md
tags: [ordo, dependency, pin, construction, cohort, decision]
confidence: high
derived_from: []
provenance:
  contributions:
    EXE-20261125T212905301Z-3ab94da5:
      operations: [created]
      at: 2026-11-26T03:18:18.012Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "WI-0112: keep Ordo library pins at 1.8.2 under owner-delegated decision"
---

# Context

WI-0112 asked whether Praxis's vendored Ordo library pins
(`EchelonFoundry.Ordo.Core` and `EchelonFoundry.Ordo.Construction` 1.8.2,
`vendor/nuget/*.lock`) should follow Ordo 1.20.0 when the echelon-current
release set moved to Ordo 1.20.0.

The echelon-current set selects the **Ordo CLI** a repository runs. It does
not select the Ordo *library* versions a tool compiles against. Praxis's
library pins are an independent dependency of Praxis's own build.

A trial on 2026-11-25 (recorded on WI-0112 when it was blocked) swapped the
pins to the v1.20.0 `ordo-core.nupkg` and `ordo-construction.nupkg`, whose
sha256 digests matched the release `native-checksums.txt`, and found:

1. **The build breaks.** Ordo.Construction 1.20 adds the vocabulary-3
   `ObligationKind` cases `ResolveUndecidedTransition` and `DecideExtension`.
   `ConstructionRouting.classify` and `ConstructionService.obligationNode`
   fail FS0025 (incomplete match; warnings are errors). Routing both as
   semantic decisions compiles, but the `praxis.construction-routing/1`
   semantic entries then carry an empty subject and question. Their wire
   shape (state, command, point), their handoff question kinds and the
   `docs/construction.md` routing table would need a design decision.
2. **Governance semantics change.** With that patch, 2507 of 2513 tests
   passed. Five local-runtime CLI tests failed because Ordo.Core 1.20 cohort
   readiness refuses `AuthorizationOutsideCohort` (Ordo DEC-B, DEC-C): an
   authorization set may no longer name decisions outside the selected
   cohort's dependency closure. Praxis passes one global approved-decision
   set to every cohort.
3. `.echelon/foundations.json` declares Ordo 1.8.2, and it would have to move
   with the locks.

# Decision

The owner delegated this call (2026-11-26). Option (b) is chosen:

1. Praxis keeps the Ordo.Core and Ordo.Construction **1.8.2** library pins.
   The locks, the `PackageReference` versions and the `ordo` entry in
   `.echelon/foundations.json` stay at 1.8.2.
2. Adopting echelon-current's Ordo CLI version does not oblige a library
   upgrade. The two are tracked separately.
3. Ordo 1.20 is not adopted by changing a version. It is a Praxis minor
   feature and needs its own work item, which must cover:
   - **Per-cohort authorization scoping.** Praxis must give each cohort only
     the approved decisions inside that cohort's dependency closure,
     instead of one global set, so 1.20's `AuthorizationOutsideCohort`
     check is met. The affected fixtures must be regenerated.
   - **Routing for `ResolveUndecidedTransition` and `DecideExtension`.**
     These need an explicit classification, a
     `praxis.construction-routing/1` wire shape (state, command, point),
     handoff question kinds and an updated `docs/construction.md` routing
     table.
   - Moving the locks, the `PackageReference` versions and
     `foundations.json` together.

# Rationale

- The 1.8.2 pins build. They are digest-verified and pass the full suite.
  Nothing in the current release set requires the 1.20 library API.
- Following 1.20 changes how Praxis governs authorizations (from global to
  per-cohort). That is a design change with consequences for fixtures and
  for consumers. A version bump is the wrong place to absorb it.
- The vocabulary-3 obligations have no Praxis routing semantics yet. A
  placeholder (semantic entries with an empty subject and question) would
  ship an under-specified wire shape. The Authority contract forbids
  inventing that policy.

# Alternatives considered

- **(a) Adopt Ordo 1.20 libraries now.** Rejected for this release because it
  requires the two designs above. It remains the intended direction once
  they are authorized and scoped as a feature work item.

# Consequences

- `praxis foundations verify` continues to declare and verify Ordo 1.8.2 for
  Praxis itself.
- Praxis can run alongside an Ordo 1.20 CLI. The libraries Praxis links are
  independent of the CLI binary a repository runs.
- WI-0112 is closed with this record as its evidence.

# Reversibility

High. Adopting 1.20 later is additive work under a new work item. This
decision changes no code.
