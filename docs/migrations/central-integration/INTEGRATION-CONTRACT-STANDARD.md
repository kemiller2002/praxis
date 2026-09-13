# Receiver-Owned Integration Contract Standard (WI-2)

This is the standard every `<Application>.Integration` package under the
ROS Central migration follows, starting with
`EchelonFoundry.Ros.Integration` (WI-3). It restates, as a concrete
checklist, the receiver-owned-contract rules already described in
`MIGRATION-PLAN.md`'s "Permanent governance additions" section — this
document is the operational standard those governance rules point back
to.

## 1. The receiver owns the type

The application that *receives* data owns the public type that describes
it. A sender depends on the receiver's published package; the receiver
never depends on a sender's package for data the sender pushes to it.
`EchelonFoundry.Ros.Integration` exists because ROS is the receiver of
externally-generated activity data — other applications will reference
it, ROS does not reference theirs for this purpose.

## 2. Two separate questions, two separate layers

An integration package answers exactly one question: **is this
structurally valid?** — are the required fields present, is the shape
well-formed, is the contract version supported. It never answers **is
this legal for this project in its current state?** — that is a ROS
domain decision (`Ros.Domain`), made only after a structurally valid
value has crossed the boundary. Mixing the two collapses a boundary this
migration depends on: a producer must be able to construct and validate
a payload without knowing anything about ROS's internal state rules, and
ROS's domain rules must be able to change without breaking every
producer's compile.

Concretely: `create` functions in an integration package return
`Result<'T, 'Error list>` where `'Error` is a **closed** union of
structural problems only (a missing required field, an internally
inconsistent value like an end before a start, an unsupported contract
version). They never reference a live project, repository, or work-item
state to decide validity.

## 3. The wire contract has its own version, separate from the package's semver

Every serialized payload carries an explicit version field
(`contractVersion`, starting at `"1"`) that is **not** the same number as
the NuGet package's own semantic version. The package can go from
`1.0.0` to `1.4.0` (additive changes) while every payload it produces and
accepts still says `contractVersion: "1"`. The wire version only changes
when the *shape of the data on the wire* changes in a way old consumers
cannot parse.

## 4. Old versions are adapted at the boundary, never carried into the domain

When a wire version changes, the package gains a new isolated parsing
module for that version (e.g. `module V1`, `module V2`) plus an
`Adapter` module (`fromV1`, `fromV2`) that maps each historical shape
into the current, single internal input shape. Domain-facing code
(`create`, and everything downstream of it) only ever sees the current
shape. A historical version's parsing logic is deleted only once no known
producer depends on it, confirmed by repository search and — once
telemetry exists — by observed traffic, and only after that removal is
explicitly approved and documented. This is why
`EchelonFoundry.Ros.Integration`'s `Serialization.fs` isolates its (for
now, single) wire-format parsing behind a private `V1` module and an
`Adapter` module from its very first commit, even though there is only
one version to adapt today — the seam exists before it is needed, not
retrofitted under pressure once a second version arrives.

## 5. Compatibility policy for the package's own semver

Ordinary semver applies to the package itself, constrained by what the
receiver-owned-contract rule above requires in practice:

- **Patch**: implementation-only changes (bug fixes, performance,
  internal refactors) with no change to any public type or accepted wire
  shape.
- **Minor**: additive, backward-compatible changes only — a new optional
  field, a new (still-optional) identifier type, a new accepted
  (additional) contract version alongside every version already
  supported. Existing producers must keep compiling and existing
  payloads must keep deserializing without change.
- **Major**: anything that removes or narrows something a producer or
  consumer could depend on — a required field's meaning changes, a
  previously-supported contract version is dropped, a public type's
  shape changes incompatibly. Prefer supporting the old and new contract
  versions simultaneously (via the V1/V2/Adapter pattern above) over a
  breaking major release wherever the two can coexist.

## 6. Zero dependency on anything application-specific

An integration package depends on nothing beyond `FSharp.Core` and the
.NET base class library (`System.*`). It never references `Ros.Domain`,
`Ros.Infrastructure`, any transport/framework library, or any other
application's package unless it is explicitly consuming that
application's own receiver-owned contract (in which case it depends on
that published package only, never that application's internals). This
is mechanically enforced in this repository by
`tests/Ros.Tests/ArchitectureTests.fs`, which reads every project's
`ProjectReference` list directly from its `.fsproj` and fails the build
if `Ros.Integration` gains one.

## Applies to

- `EchelonFoundry.Ros.Integration` (`src/Ros.Integration`) — the first
  package built to this standard (WI-3).
- Every future `<Application>.Integration` package this migration or a
  later one introduces (`EchelonFoundry.Chrona.Integration`,
  `Summa.Integration`, `Strata.Integration`, and so on), per
  `MIGRATION-PLAN.md` Phase 8–10.
