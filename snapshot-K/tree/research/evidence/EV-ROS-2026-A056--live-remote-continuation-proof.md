---
id: EV-ROS-2026-A056
title: "Live proof: a runtime-less successor continues active work with praxis.remote 1.3 work.continue on Praxis 3.6.0"
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
evidence_type: primary
supports: [RQ-ROS-2026-A022, RQ-ROS-2026-A021]
related_documents:
  - https://github.com/kemiller2002/praxis/issues/90
  - RQ-ROS-2026-A022
  - RQ-ROS-2026-A021
  - DF-ROS-2026-A042
  - DF-ROS-2026-A041
  - EV-ROS-2026-A054
  - docs/remote-agent-contract.md
  - docs/remote-protocol.md
tags: [remote-execution, e2e, continuity, checkpoint, provenance, telemetry, gh-90]
confidence: high
derived_from: [RQ-ROS-2026-A022]
provenance:
  contributions:
    EXE-20260929T120718709Z-8c483346:
      operations: [created]
      at: 2026-09-29T12:08:01.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Live proof of praxis.remote 1.3 executor continuation (PRAXIS-REMOTE-12-CONTINUE-PROOF), verified against main and the GitHub Actions API"
      evidence: [https://github.com/kemiller2002/praxis/issues/90]
---

# Live proof of executor continuation over praxis.remote 1.3

## What was run

On 2026-09-29, work item `PRAXIS-REMOTE-12-CONTINUE-PROOF` was started by
one cloud-agent session and taken over by another with `work.continue`.
Both sessions had only GitHub access and were instructed not to install
.NET, F# or Praxis. Every transition ran on a GitHub-hosted runner through
`.github/workflows/praxis-remote.yml` on `main` of
`kemiller2002/praxis`.

[`EV-ROS-2026-A054`](EV-ROS-2026-A054--live-remote-execution-proof.md)
proved a [record] on Praxis 3.5.0 (protocol 1.2), where the successor had to
block and then resume the item. This record covers the path that
[`DF-ROS-2026-A042`](../decisions/DF-ROS-2026-A042--durable-work-checkpoints-and-executor-continuation.md)
section 7 defines instead. The predecessor leaves a durable checkpoint and
the item stays `active`. The successor continues it under its own
execution, and no block or resume takes place.

Everything below was checked against the repository (`main` at `b04b485`)
and the GitHub Actions API. It was not taken from the agents' own reports.

## Release and pin

- `v3.6.0` was published by `native-release.yml`. The one-click `Release`
  workflow dispatched it (run 36558705902) after bumping the version as
  work item `RELEASE-3-6-0`.
- `praxis-linux-x64.tar.gz` matches `native-checksums.txt`, with SHA-256
  `c4a5b1f3a64aa32474aba6464f9978886dbf6f3cf78fc421ce50cda1074dee71`.
- The bundle reports `ros-fs 3.6.0`, and its help lists `work checkpoint`
  and `work continue`.
- `.echelon/toolchain.json` pins `praxis` 3.6.0 (PR #105, work item
  `REMOTE-ENABLE-3-6-0`).
- Every journal entry below records `praxisVersion` 3.6.0.
- **Earlier refusal on 3.5.0.** A `work.resume` of an active item was
  refused there as `domain-rejected` (run 36524551943). A `work.continue`
  sent after the pin (run 36561504815) was refused only because
  `PRAXIS-REMOTE-11-PROOF` was already complete, with refusal code
  `work-item-not-active`.

## Sequence

| Request ID | Session | Run | Result | Commit on `main` |
|---|---|---|---|---|
| `req-cont-a-start-1` | A | 36562151420 | succeeded | `834a262` |
| `req-cont-a-batch-1`: step start, telemetry, step complete | A | 36562293832 | succeeded | `023eeec` |
| `req-cont-a-checkpoint-1` | A | 36562405770 | succeeded; checkpoint `00815d508da07d99f37731fa` | `4138e88` |
| `req-cont-b-context-1` | B | 36563401290 | succeeded (read) | none |
| `req-cont-b-continue-1` | B | 36563476060 | succeeded; `status: continued` | `9097a22` |
| `req-cont-b-batch-1`: step start, telemetry, step complete | B | 36563637526 | succeeded | `f2e36f5` |
| `req-cont-b-checkpoint-1` | B | 36563761021 | succeeded; checkpoint `c18e54e9104edeb12da69080` | `fd9317c` |
| `req-cont-b-complete-1` | B | 36563893186 | succeeded; item `complete` | `b04b485` |
| `req-cont-b-validate-1` | B | 36564025425 | succeeded (read) | none |

For every mutation, the journal entry `.ros/remote/requests/<requestId>.json`
on `main` holds the full response. In each of the 13 journalled responses
(batch constituents included), `repository.expectedSha` equals
`observedSha`.

## Identity, lineage and continuity

- **Sessions.** Agent A is `<session redacted>`. Agent B is
  `<session redacted>`. Every journal entry records the
  requester as `asserted-by-request` (agent `anthropic/claude-code`, model
  `unknown`) and the executor as `observed-by-executor`.
- **Executions.**
  - `EXE-20260929T113043360Z-32d06972` was created for session A, with no
    parent.
  - `EXE-20260929T114329995Z-cebed9a0` was created for session B. Its
    `identity.parentExecutionId` is A's execution.
  - Both executions are finalized.
- **Continuation.** The `work.continued` event (11:43:28Z) names A's
  execution as the predecessor, with disposition `interrupted` and
  `dispositionSource: observed-by-successor`. The `work.continue` response
  also returned the predecessor's step, `continue-proof-a` (`completed`).
  A's own execution record was not edited to say it was interrupted.
- **Event history.** The item's events are `work.started`,
  `work.checkpointed` (A), `work.continued`, `work.checkpointed` (B) and
  `work.completed`. There is no `work.blocked` or `work.resumed`.
- **Checkpoints.**
  - A's checkpoint is on `023eeec` and B's is on `f2e36f5`. Both are
    `verified` by `git-remote-observation`.
  - B's checkpoint request and its step requests name B's own execution.
    No request from session B names A's execution.
- **Completion guard.** Completion succeeded with
  `workProtocol.continuity.requireDurableCheckpoint` set to true.

## Telemetry and evidence quality

- Each execution carries one step-scoped `tool.calls` measurement:
  - 11, for step `continue-proof-a`;
  - 18, for step `continue-proof-b`.
- Both measurements have `source.type` `agent-report`. They are
  self-counted and not independently observed.
- No token metrics were recorded, because the agents cannot observe them.

## Gaps the proof found

1. **Conclusions are dropped.** Session B's `work.complete` request carried
   a `conclusion`. The completed item on `main` has no `conclusion`, because
   Praxis 3.6.0 persists it only for research items. This is the defect
   already recorded as PRAXIS-REMOTE-16. It is being fixed separately, and
   that fix is not part of this record.

## Limits of this evidence

- **The no-.NET claim is not in the repository.** The callers' lack of a
  local runtime rests on their session instructions and their own reports.
  Because of gap 1, B's recorded environment check was not persisted.
- **The agents were not independent products.** Both agents were the same
  product (`anthropic/claude-code`) in different sessions, and both were
  launched by one orchestrating session. They are distinguished by
  session, not by provider.
- **The predecessor was not really lost.** A stopped by instruction rather
  than by losing its executor. `interrupted` is the successor's
  observation, exactly as the DF-ROS-2026-A042 model defines it.
