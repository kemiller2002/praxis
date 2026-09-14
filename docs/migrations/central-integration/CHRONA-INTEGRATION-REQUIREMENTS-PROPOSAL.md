# `Chrona.Integration` — ROS-Side Requirements Proposal (WI-16 input)

This is ROS's opening position for defining `Chrona.Integration`, written
without input from Chrona's own team or repository (neither is available
to this session). It is a proposal to react to, not an agreement — WI-16
is only complete once Chrona's side has reviewed it and both sides have
converged on an actual contract. Everything below follows the same
receiver-owned-contract standard ROS itself follows (see
`INTEGRATION-CONTRACT-STANDARD.md`), applied in the direction the
migration spec requires: **Chrona receives time-entry data from ROS, so
Chrona owns the contract type** — ROS depends on
`EchelonFoundry.Chrona.Integration` once Chrona publishes it, not the
other way around.

## What ROS needs to send

ROS owns *activity*, not time. What ROS can honestly hand to Chrona is an
observation that some activity happened — never a finished, authoritative
time-log entry. Concretely, ROS would want to send something shaped like:

```
TimeObservationCandidate
    ContractVersion   -- Chrona's own wire version, independent of ROS's
    ActivityId        -- ROS's own activity id (see EchelonFoundry.Ros.Integration),
                         so Chrona can correlate a later ROS-side correction
                         or retraction to the same observation
    OrganizationId
    ProjectId
    ActorId option     -- who Chrona should attribute the time to, if known
    StartedAt
    EndedAt
    Description option
    Evidence           -- same Evidence { Kind; Reference } shape ROS
                          already defines, so a reviewer can trace a time
                          entry back to the commit/PR/CI-run that justified it
```

**ROS must not create Chrona's final authoritative time entry.** Whatever
Chrona's own `Chrona.Integration` type turns out to be, it needs to leave
room for Chrona's own reconciliation/dedup step to run before anything
becomes an authoritative entry — per the spec's own Phase 9 acceptance
criteria: "one observation → one candidate; repeat Chrona load → still
one candidate; repeat ROS delivery → still one candidate." That
idempotency guarantee has to be something Chrona's contract makes
possible, most likely by Chrona's own type carrying an explicit candidate
identity keyed on ROS's `ActivityId`, so a re-delivered observation
collapses to the same candidate rather than creating a duplicate.

## What ROS needs from Chrona's contract, structurally

1. **A public NuGet package**, `EchelonFoundry.Chrona.Integration` (or
   whatever name Chrona's team prefers, mirroring the naming pattern this
   migration already established for `EchelonFoundry.Ros.Integration`),
   with the same zero-application-dependency posture: no dependency on
   Chrona's own internal domain, database, or WASM UI code, only
   `FSharp.Core`/`System.*` (or the .NET-idiomatic equivalent if Chrona's
   team prefers C#).
2. **An explicit `create`-style constructor** returning a `Result` with a
   closed, structural-only error type — mirroring
   `ActivityObservation.create`'s split between "is this well-formed" and
   "is this legal," so ROS never has to guess which layer rejected a
   value.
3. **A serialized contract version independent of the package's own
   semver**, exactly as `EchelonFoundry.Ros.Integration` does, since
   Chrona's GitHub-datastore-based storage format is itself something
   ROS needs to write to durably across releases of both packages.
4. **A description of where in Chrona's GitHub datastore ROS should
   write this payload** — a path convention, a file-naming scheme, and
   whatever locking/append semantics prevent ROS's write from racing a
   concurrent Chrona-side write. This is the one requirement genuinely
   specific to Chrona having no server API: ROS's adapter
   (`Ros.Integrations.GitHub`, not yet built — see `MIGRATION-PLAN.md`
   Phase 8) needs an agreed target, not just an agreed payload shape.

## What ROS commits to

- ROS will not add a server API to Chrona's side solely to make this
  integration easier — the spec is explicit that Chrona's WASM-UI /
  GitHub-datastore architecture is not something this migration should
  push Chrona to change.
- ROS will treat `Chrona.Integration`'s published version like any other
  pinned dependency (`<PackageReference Include="EchelonFoundry.Chrona.Integration"
  Version="x.y.z" />`, never a floating version), with the same
  deliberate upgrade flow (bump → PR → build/test/compat check → merge)
  this migration already applies to its own package.
- ROS will run any real delivery in shadow mode first (Phase 9), proving
  the idempotency property above before any observation is allowed to
  become a real Chrona time entry.

## Open questions for Chrona's team

1. Does Chrona want candidate identity keyed on ROS's `ActivityId`
   directly, or does Chrona need its own separately-minted candidate id
   with a mapping table back to `ActivityId`?
2. What GitHub datastore write conflict does Chrona's own reconciliation
   already handle, and does ROS's adapter need to pre-empt any of it (for
   example, by reading before writing) or can it write blindly and let
   Chrona's own load-time reconciliation absorb duplicates?
3. Does Chrona want ROS's `Evidence` list passed through verbatim, or
   does Chrona's own review UI expect a different shape for provenance?

This document does not commit Chrona to anything — it exists so a real
conversation with Chrona's team has a concrete starting draft instead of
starting from nothing.
