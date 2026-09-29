# CI batching and agent execution

Git commits are durability events. CI runs are validation events. Praxis keeps
those cadences separate so agents can create recoverable incremental history
without paying the latency and compute cost of a full remote build after every
push.

## Default policy

Push-driven verification uses a **10-minute quiet-period debounce**. The
workflow is grouped by the relevant ref and configured to cancel a superseded
run. A new commit during the quiet period cancels the older waiting run and
starts a fresh ten-minute window. Only the newest eligible run proceeds into
the expensive build/test work.

Workflows with multiple expensive jobs use one lightweight gate job and make
the expensive jobs depend on it. Single-job workflows may put the wait at the
front of the job. The delay belongs before dependency setup, compilation, test
matrices, artifact assembly, or deployment.

## Agent behavior

Agents keep making cohesive incremental commits, pushes, and Praxis
checkpoints. They do not wait for GitHub Actions after every push. They
continue the next independent, in-scope slice and use local validation when it
is useful.

Remote build status is normally checked once, after the final implementation
push/checkpoint. An earlier check needs a concrete reason:

- its result determines the next implementation action;
- a high-risk boundary should be validated before more work is compounded;
- a merge, release, publication, or external gate requires the result.

Final success is never inferred. The final applicable run must be inspected
when accessible; if it is still queued, cancelled, or unavailable, that state
is reported rather than converted into success.

## Workflows that stay immediate

The quiet-period debounce is for commit-driven verification/deployment, not
command/control. Explicit manual releases, reusable workflow calls, Praxis
remote execution, the `praxis-inbox/**` relay, and explicit release-asset
backfills stay immediate unless their own contract says otherwise.

The general validation workflow excludes `praxis-inbox/**` pushes entirely.
Those branches transport typed Praxis requests; they are not source changes and
do not justify a full repository test matrix for every request.

## Operational tradeoff

GitHub Actions has no general repository-level pre-run debounce primitive. A
lightweight gate therefore occupies one hosted runner during the quiet period,
but prevents the much larger dependency/build/test matrix from starting for
commits that are quickly superseded. If GitHub adds a non-runner-backed
primitive with the same cancellation semantics, the implementation may change
without changing this policy.

## Acceptance behavior

- several commits to one ref inside ten minutes result in only the newest
  eligible run progressing beyond the quiet period;
- a quiet ref progresses after ten minutes;
- independent refs do not cancel each other;
- command/control and explicit manual workflows remain immediate;
- incremental commit/checkpoint discipline remains unchanged;
- remote CI polling is normally deferred until final validation.
