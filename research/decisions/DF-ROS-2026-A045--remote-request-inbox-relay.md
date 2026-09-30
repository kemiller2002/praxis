---
id: DF-ROS-2026-A045
title: A push-triggered inbox relay routes praxis.remote requests committed to praxis-inbox/** branches to praxis-remote.yml, unchanged
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A023]
supporting_evidence: [EV-ROS-2026-A057]
related_documents:
  - RQ-ROS-2026-A023
  - EV-ROS-2026-A057
  - DF-ROS-2026-A041
  - docs/remote-agent-contract.md
  - .github/workflows/praxis-remote.yml
  - .github/workflows/praxis-remote-inbox.yml
supersedes: []
superseded_by: []
tags: [remote-execution, github-actions, continuity, cross-provider]
confidence: medium
derived_from: [RQ-ROS-2026-A023, RQ-ROS-2026-A022]
provenance:
  contributions:
    EXE-20260929T162324008Z-39bbe593:
      operations: [created]
      at: 2026-09-29T16:23:38.842Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-REMOTE-INBOX-01: capture the cross-provider continuation gap and its remedy (re-landed from claude/remote-inbox-relay-v3)"
---

# Context

`praxis-remote.yml` starts only on `workflow_dispatch` or `workflow_call`.
An OpenAI successor for `PRAXIS-XPROVIDER-PROOF-01` could commit to the
repository but could not dispatch, and it had no network in its sandbox, so
it could not continue (`EV-ROS-2026-A057`). The architecture was not at
fault: Praxis's protocol, binding and identity rules would have handled the
continuation. What was missing was a way in for a writer that cannot call
the Actions API.

# Decision

1. **An inbox relay, not a second protocol.**
   `.github/workflows/praxis-remote-inbox.yml` runs on a `push` to a branch
   matching `praxis-inbox/**`, for files under `.praxis-inbox/`. For each
   `.praxis-inbox/*.json` added or modified by the push, it:
   - checks only what routing needs: the document is JSON with
     `protocol: praxis.remote`, a `requestId`, and a `repository.ref`
     (`refs/heads/NAME`, or none for a read, which then runs on the default
     branch);
   - dispatches `praxis-remote.yml` on that branch with the file's exact
     bytes as `request`, using `GITHUB_TOKEN` (dispatching is the one event
     a `GITHUB_TOKEN` may trigger);
   - records the inbox branch, commit, pusher and resulting run in its
     summary.

   Praxis still does everything else.
2. **The inbox lives on its own branch.** The request is committed to a
   `praxis-inbox/…` branch, never to the branch it targets. The target head
   does not move, so `expectedSha` stays valid, and Praxis needs no change
   and no new release.
3. **Trust equals dispatch.** Pushing a branch to this repository requires
   write access, which is what `workflow_dispatch` requires. Pushes to a
   fork run in the fork, never here, and there is no `pull_request` trigger.
   The relay holds only `actions: write` and `contents: read`, and runs no
   code from the request.
4. **Refusals are visible.** A file that cannot be routed is not
   dispatched. The relay reports it as an error annotation, and the
   relay run fails so the submitter can see it.
5. **Results as before.** The journal on the target branch is the durable
   result (`.ros/remote/requests/<requestId>.json`), readable with plain
   repository read access.

# Consequences

- A contents-only agent needs two capabilities: create a branch (or reuse
  one inbox branch) and commit a file. A standing inbox branch created from
  the default branch can be reused for any number of requests.
- Inbox files are transport, not repository work. `ros.json` lists
  `.praxis-inbox/**` among `workProtocol.ignoredPaths`, as it does
  `.ros/remote/**`, so an inbox commit never needs work-item attribution.
- Each request produces two runs, the relay and `praxis-remote`. The
  relay's summary links the submitting commit to the dispatched run, so the
  chain from submitter to journal is auditable.
- An inbox branch keeps the relay workflow version it was created with.
  Recreate it from the default branch to pick up relay changes.
- An agent with neither dispatch nor contents write still cannot take part.
  The contract says so, and that agent must hand off to one that can,
  rather than improvise.

# Verification (2026-09-29)

- **First live test, which found a defect.** Branch
  `praxis-inbox/claude-live-describe` at `e742e4f`, relay run `36570413074`:
  - the relay found no request, because the push event's commit list
    omitted the file for the push that created the branch;
  - the relay now diffs with Git: `before..HEAD`, or the merge base with the
    default branch for a new branch;
  - a regression test covers it.
- **Second live test, which passed.** Branch
  `praxis-inbox/claude-live-describe-2` at `53aeea4`:
  - a read-only `praxis.describe` under the submitter's own identity
    (`anthropic/claude-code`);
  - relay run `36571870365` succeeded and dispatched
    `praxis remote req-inbox-live-describe-02` (run `36571886619`,
    `workflow_dispatch` on `main`);
  - that run succeeded and printed Praxis's full operation catalog,
    including `work.continue` (protocol 1.3).
- **Not yet shown:** a mutation through the inbox by a successor from
  another provider. That is `RQ-ROS-2026-A023`'s second acceptance
  criterion, and only an actual OpenAI execution can meet it
  (`PRAXIS-XPROVIDER-PROOF-01`).

# Alternatives considered

- **Commit requests to the target branch.** Rejected: the submission
  commit moves the head and breaks `expectedSha` binding, so Praxis would
  need a special rule for it.
- **Issue or comment commands.** Rejected: anyone who can comment on a
  public repository could reach the workflow, so authorization would depend
  on author-association checks the relay would have to get right.
- **Only document the connector gap.** Rejected: it is true but not
  sufficient. `RQ-ROS-2026-A022` promises cross-provider continuation, and a
  common class of agent integrations cannot dispatch.
- **Put a GitHub token in the agent's sandbox.** Rejected: it moves a
  credential into an agent environment, which Praxis's remote model exists
  to avoid.
