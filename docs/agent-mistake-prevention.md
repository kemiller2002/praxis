# Preventing agent mistakes by construction

Tracked by [Praxis #228](https://github.com/kemiller2002/praxis/issues/228),
extending #226/#220 and [Ordo #52](https://github.com/kemiller2002/ordo/issues/52)
and [#59](https://github.com/kemiller2002/ordo/issues/59).

The objective is to eliminate specific invalid actions before effects occur.
This is not a claim that requirements, modeling choices or generated behavior
are always correct. Cryptographic receipts establish exact approval scope,
not the truth of the approved design.

## First implemented boundary

`LocalJournaledDispatch.run` previously called `SelectSpec` with untrusted
transport before native recovery, packet/authority/prerequisite checks.
Worker preparation now requires an opaque `CheckedLocalWorkerPacket` created
only by successful `LocalAgentHandoff.checkPacket`. Admission runs before
adapter selection, journal reservation or process launch. The constructor is
private and the inspected packet is immutable. The wire format is unchanged.

Reservation and launch intent still re-read current observations. A snapshot
cannot survive revocation as permission to act. The optional spooled path uses
the same typed selector and dispatch composition. This changes the isolated
application selector API: adapters use `LocalAgentHandoff.checkedPacket` to
inspect their input. Existing selectors ignoring input need no logic change.

The pure validator accepts supplied observations; synthetic callers can supply
synthetic authority. This type prevents accidental bypass in ordinary typed
code, not malicious reflection, null/unchecked casts or a compromised host.
Protected locks, authenticated observations, process containment and qualified
runtime wiring remain required. No CLI execution gate is enabled by this work.

## Prevention inventory and implementation order

| Mistake | Prevention mechanism | Owner and next acceptance |
| --- | --- | --- |
| Preparing an invalid or undelegated assignment | Opaque admission value; validation before selection | Praxis: first slice implemented; refusal causes zero selector/reservation/launch effects. |
| Dropping requirements or implementing unresolved decisions | Compile the pinned semantic blueprint into a private ready-cohort value | Ordo #59: preserve every source disposition; unresolved blockers produce no ready value. Praxis consumes exact pinned qualification. |
| Conflicting interface or ownership assumptions | Blueprint assigns contracts, member ownership and permitted operations before dispatch | Ordo/Praxis: duplicate ownership, cycles and incompatible contracts refuse construction; explanation alone is advisory. |
| Editing outside an assignment or altering validation/policy | Host-issued operation capabilities and isolated writable resources | Praxis: deny writes at attempt time; result path checks remain defense in depth. Prove with actual denied-write probes. |
| Inconsistent repetitive wiring | Deterministic construction from typed contracts | Ordo/construction adapters: identical inputs yield identical output; unsupported operations yield no output. |
| Acting on stale authorization or retrying uncertain effects | Fresh checks at effect boundaries; journaled transitions and expected-head updates | Praxis: retain existing recovery/refusal behavior while qualifying protected host adapters. |
| Incorrect business interpretation | Explicit decisions and independent original-requirement acceptance | Ordo/Dokimos: preserve uncertainty; require human/domain evidence where the specification cannot decide. |

Types should carry established facts and constrain available operations. A new
wrapper with a public constructor, a boolean `validated`, or a worker-editable
policy file is not prevention. Avoid a parallel validator: the smart constructor
uses the existing packet rules. Next, place pinned semantic readiness and
permitted edits behind equivalent construction boundaries, then qualify the
local coordinator/two-worker runtime.

## Validation and remaining work

Validation results are recorded in the PR and tracked work checkpoint. Tests
exercise malformed source/acceptance/resources/budgets, unavailable recovery,
disabled or stale authority, missing prerequisites, expiry, and revocation
between admission, reservation and intent. Compiler probes separately verify
that callers cannot invoke the private constructor or substitute raw transport
where a checked assignment is required.

This slice does not supply the protected host adapter, local model, behavioral
proof, complete blueprint compiler or generated editing API. PR #227 and its
#225 dependency remain prerequisites for the runtime work.
