---
id: RQ-ROS-2026-A023
title: A remote agent that can write to the repository but cannot dispatch GitHub Actions can still send praxis.remote requests, with no change to trust, validation or identity
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
priority: high
depends_on: [RQ-ROS-2026-A022, RQ-ROS-2026-A021]
evidence_ids: [EV-ROS-2026-A057]
related_documents:
  - EV-ROS-2026-A057
  - DF-ROS-2026-A041
  - DF-ROS-2026-A045
  - docs/remote-agent-contract.md
  - docs/remote-protocol.md
tags: [remote-execution, continuity, cross-provider, github-actions]
derived_from: [RQ-ROS-2026-A022]
provenance:
  contributions:
    EXE-20260929T161246994Z-10477f29:
      operations: [created]
      at: 2026-09-29T16:13:02.893Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-REMOTE-INBOX-01: capture the cross-provider continuation gap and its remedy (re-landed from claude/remote-inbox-relay-v2)"
---

# Requirement

`RQ-ROS-2026-A022` requires cross-provider continuation, and the remote
agent contract promises that an agent with GitHub access but no Praxis
runtime can take part. The only entry point today is `workflow_dispatch`
of `praxis-remote.yml`, which needs the GitHub Actions dispatch API.

Some agent integrations, such as a ChatGPT GitHub connector, can read and
commit repository contents but cannot dispatch workflows. Some also run in
sandboxes with no network. Such an agent can recover a handoff completely,
but it cannot continue it (`EV-ROS-2026-A057`).

## Normative requirements

- **INBOX-001** An agent that can create a branch and commit a file in this
  repository, without dispatching Actions, MUST be able to submit any
  `praxis.remote` request.
- **INBOX-002** The submitted document MUST be the unchanged
  `praxis.remote` request. Praxis MUST remain the only component that
  classifies, authorizes, binds (`expectedSha`), executes and journals it.
  The channel MUST NOT add operations, bypass capability grants, or
  interpret the request beyond routing it.
- **INBOX-003** The trust boundary MUST be no weaker than `workflow_dispatch`:
  - only a push by someone with write access to this repository may start
    the channel;
  - forks and pull requests MUST NOT be able to start it;
  - no secret may reach the request or Praxis.
- **INBOX-004** Submitting a request MUST NOT move the branch the request
  targets, so `repository.expectedSha` stays the commit the agent read.
- **INBOX-005** Identity MUST stay the requester's own assertion inside the
  request, exactly as with dispatch. The channel MUST record the submitting
  commit, branch and pusher alongside the run, and MUST never supply or
  alter an actor.
- **INBOX-006** Replays MUST keep Praxis's idempotency semantics.
  Resubmitting the same document with the same `requestId` MUST give the
  recorded result, never a second effect.
- **INBOX-007** Results MUST be readable without Actions access: the
  journal `.ros/remote/requests/<requestId>.json` on the target branch.
- **INBOX-008** The contract MUST document when to use this channel, and
  what to do when neither dispatch nor a contents write is available.

## Acceptance

- **Live channel test:** a read-only `praxis.describe` request, submitted
  only by committing it to an inbox branch, produces a `praxis-remote.yml`
  run for the named ref.
- **Continuation completes:** the `PRAXIS-XPROVIDER-PROOF-01` successor
  continues through the channel, and its own identity, its execution's
  parent, and the predecessor's `interrupted` disposition are all recorded
  by Praxis.
- **No bypass:** the relay's tests show that it passes the document through
  byte for byte, refuses a malformed submission without dispatching, and
  has no pull-request or fork trigger.

## Status (2026-09-29)

| Criterion | Status |
|---|---|
| Live channel test | Met. See the verification in `DF-ROS-2026-A045`: relay run `36571870365` dispatched the Praxis remote run `36571886619`. |
| Continuation completes | Open. It needs an actual OpenAI or ChatGPT execution to continue `PRAXIS-XPROVIDER-PROOF-01` through the inbox. It cannot be met on that executor's behalf. |
| No bypass | Met, by `tests/praxis-remote-inbox.test.mjs`. |
