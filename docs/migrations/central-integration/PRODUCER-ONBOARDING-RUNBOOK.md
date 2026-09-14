# Producer Onboarding Runbook (WI-20)

Concrete, actionable steps for connecting an additional repository to a
running `ros-central` instance, once one exists. This is the mechanism
WI-20 ("incrementally onboard additional producer repositories") needs;
what it cannot supply is a second real repository or a live deployment
target to onboard against — see
`EXECUTION-STATUS.md`'s WI-14/WI-15/WI-20 sections and backlog item
`WI-0057` for that honest gap. Everything below has already been proven
to work, end to end, using this repository itself as the producer (see
`EXECUTION-STATUS.md`'s WI-14/WI-15 trial log) — onboarding a genuinely
different repository is the same steps, just run from that repository
instead of this one.

## Prerequisites

1. A running `ros-central` instance (`src/Ros.Host`), reachable over
   HTTP from the producer repository, with `ROS_EXTERNAL_ACTIVITY_ENABLED=true`
   set on the host.
2. The producer repository has network access to that instance's URL.
3. A decision on the producer's identity string (the `source` value
   Central will key idempotency on) — a stable name for that repository
   or team, not something that changes between runs.

## Steps

1. **Copy the connector.** `tools/ros_central_client.mjs` has zero
   dependencies beyond Node's built-in `fetch`; copy it into the
   producer repository's own tooling directory, or (once
   `EchelonFoundry.Ros.Integration` is actually published per WI-13 —
   still blocked, see `WI-0056`) depend on the published package
   directly instead of hand-copying a script.
2. **Set the feature flag.** `ROS_CENTRAL_ENABLED=true` in whatever
   environment invokes the connector. Per `AGENTS.md`'s Integration
   Architecture Rules, this must never be on by default — the producer
   repository's normal local workflow (`./ros validate`/`build`/`test`)
   must keep working identically whether or not this flag is set.
3. **Construct the activity payload** as a JSON file matching
   `EchelonFoundry.Ros.Integration`'s wire contract (see
   `tests/contracts/activity-observation-v1.json` for the canonical
   shape): `contractVersion`, a stable producer-generated `activityId`,
   `organizationId`, `projectId`, and whatever optional fields apply.
4. **Report it**:
   ```bash
   ROS_CENTRAL_ENABLED=true node ros_central_client.mjs \
     https://<central-host> path/to/activity.json <producer-name>
   ```
5. **Verify.** A `202` response with `{"activityId": "...", "state": "accepted"}`
   means Central recorded it (or, on a retried `activityId`, recognized
   it as already recorded — both cases return the same shape,
   deliberately, per the idempotency guarantee `tests/Ros.Host.Tests`
   proves). A `400`/`422` means the payload itself needs fixing before
   retrying, not a Central-side problem.
6. **Do not wire this into the producer's own CI gate.** Reporting to
   Central is additive telemetry, never a precondition for that
   repository's own `validate`/`build`/`test` passing — onboarding a
   producer must never make Central a single point of failure for that
   repository's local development.

## What onboarding a second repository would additionally prove

Beyond what this repository's own trial already showed (an idempotent
retry producing zero duplicates), a genuinely separate second repository
onboarding for real would be the first opportunity to observe:

- Whether two different repositories' `source` identities correctly
  keep their activity ids from colliding (already unit-tested in
  `tests/Ros.Persistence.Tests` with synthetic sources, but not yet
  observed from two real, independent producers).
- Real, organic activity volume and timing, which is what
  `MIGRATION-PLAN.md` Phase 7's shadow-mode comparison actually needs —
  a single session's synthetic trial (`EXECUTION-STATUS.md`) is a
  mechanism proof, not a substitute for this.

## Status

Not yet exercised against a genuinely separate repository. Tracked as
backlog item `WI-0057` in this repository's own work queue
(`.ros/work/queue.json`) pending a second real repository and a
deployment target for `ros-central`.
