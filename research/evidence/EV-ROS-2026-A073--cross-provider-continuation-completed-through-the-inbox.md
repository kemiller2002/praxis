---
id: EV-ROS-2026-A073
title: "Cross-provider continuation attempt 2: an OpenAI/ChatGPT successor continued and completed PRAXIS-XPROVIDER-PROOF-01 through the remote inbox"
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-06
updated: 2026-10-06
research_area: repository-operating-system
evidence_type: primary
supports: [RQ-ROS-2026-A023, RQ-ROS-2026-A022]
related_documents:
  - RQ-ROS-2026-A022
  - RQ-ROS-2026-A023
  - DF-ROS-2026-A045
  - EV-ROS-2026-A057
  - docs/remote-agent-contract.md
  - .github/workflows/praxis-remote-inbox.yml
  - .github/workflows/praxis-remote.yml
tags: [continuity, cross-provider, remote-execution, inbox, openai]
confidence: medium
derived_from: [EV-ROS-2026-A057]
provenance:
  contributions:
    EXE-20261006T204120763Z-b0001e2e:
      operations: [created]
      at: 2026-10-06T20:42:22.721Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-MISC-07: record the completed cross-provider continuation proof from branch proof/chatgpt-continuation"
---

# Cross-provider continuation attempt 2

Follow-up to `EV-ROS-2026-A057`, where an OpenAI successor recovered the
handoff but could not invoke Praxis. After the inbox relay
(`DF-ROS-2026-A045`) landed, a second attempt continued and completed the
same work item. Its Praxis state lives on branch
`proof/chatgpt-continuation` and was never merged into `main`. This record
brings the result to `main` without copying that branch's `.ros` state.

## What was verified (2026-10-06, `PRAXIS-MISC-07`)

Everything below was read from the repository with `git` and from the
GitHub REST API on 2026-10-06. Nothing was re-run.

**Branches and commits** (`git ls-remote origin`):

- `proof/chatgpt-continuation` head `b2becae0f1e966f0968bca98faf2e4251840de87`.
- `praxis-inbox/chatgpt-xprovider-proof-01` head `b6b6cf4d6159fe191cdde58ef4d2aa187acf9410`.
- Predecessor commits `b64303a5` (part 1) and `36020735` (Praxis-state-only
  checkpoint commit), then `eb26cd4d` (`work.continue`), `131add71` (part-2
  begin batch), `599eb663` (part 2 of `docs/proofs/cross-provider-continuation.md`)
  and `b2becae0` (finish batch).

**Inbox submissions.** The inbox branch carries three request commits, each
adding one `.praxis-inbox/<requestId>.json`: `25bb80e3`
(`req-chatgpt-xprovider-continue-01`), `a2feae56` (part-2 begin) and
`b6b6cf4d` (`req-chatgpt-xprovider-finish-01`). The `Praxis remote inbox`
relay ran on each push and succeeded: runs `36604696840`, `36606135380` and
`36677647667` (`event: push`, branch `praxis-inbox/chatgpt-xprovider-proof-01`).

**Remote executions.** `praxis-remote.yml` runs dispatched by the relay
(`event: workflow_dispatch`, triggering actor `github-actions[bot]`, branch
`proof/chatgpt-continuation`), all `success`:

| Request | Operation | `expectedSha` | Run |
|---|---|---|---|
| `req-chatgpt-xprovider-continue-01` | `work.continue` (protocol 1.3) | `36020735` | `36605336068` |
| `req-chatgpt-xprovider-part2-begin-01` | batch | `eb26cd4d` | `36607681292` |
| `req-chatgpt-xprovider-part2-step-start-01` | `step.start` | `eb26cd4d` | `36607681292` |
| `req-chatgpt-xprovider-finish-01` | batch | `599eb663` | `36677659037` |
| `req-chatgpt-xprovider-part2-step-complete-01` | `step.complete` | `599eb663` | `36677659037` |
| `req-chatgpt-xprovider-checkpoint-01` | `work.checkpoint` | `599eb663` | `36677659037` |
| `req-chatgpt-xprovider-complete-01` | `work.complete` | `599eb663` | `36677659037` |

Each journal is `.ros/remote/requests/<requestId>.json` on the proof branch,
with `outcome: succeeded`, `executor.kind: github-actions`,
`executor.assurance: observed-by-executor` and Praxis `3.6.0`.

**What Praxis recorded** (proof branch, `.ros/events/events.jsonl` and
`.ros/telemetry/executions/`):

- `work.continued` for `PRAXIS-XPROVIDER-PROOF-01`, actor
  `agent:openai/chatgpt` (provider `openai`, model `gpt-5.6-sol`, runtime
  `chatgpt`), successor execution `EXE-20260929T173543602Z-75ccd1b9`, from
  checkpoint `6ee7e361d7e244e2be1c690e`, with
  `predecessor: {executionId: EXE-20260929T121754955Z-09207ab8, disposition: interrupted, dispositionSource: observed-by-successor}`.
- The successor execution's identity has
  `parentExecutionId: EXE-20260929T121754955Z-09207ab8` and
  `assurance: asserted-by-request`.
- A second durable checkpoint `2e95ff60252dc7a617c5fec2` (step `part-2`,
  commit `599eb663`, verification `verified` by `git-remote-observation`),
  then `work.completed` by the same OpenAI actor. Both executions are
  finalized. The predecessor's events stay attributed to
  `agent:anthropic/claude-code`.

## Assurance and limits

- **Asserted, not authenticated.** The requester identity
  `openai/chatgpt` is the request's own assertion (`INBOX-005`). The inbox
  commits and the part-2 document commit `599eb663` were pushed under the
  repository owner's GitHub account, which is how the successor's GitHub
  connector wrote to the repository. Praxis recorded that identity as
  `asserted-by-request` and the executor as observed; it did not and cannot
  prove which model wrote the request.
- **Not on `main`.** The work item, its executions and its journals exist
  only on `proof/chatgpt-continuation`. Merging that branch's `.ros` state
  would need conflict resolution of tool-owned files, so this record cites
  it by commit instead. The branch must not be deleted while this record
  relies on it.
- The run `36605336068` was created at `17:29:47Z`, five minutes after the
  relay run for `25bb80e3`; the journal records execution at `17:35:45Z`.
  The relay-to-dispatch link is by request ID, run name and branch, not by
  a recorded parent run ID.

## Findings

1. `RQ-ROS-2026-A023`'s "Continuation completes" criterion is met: a
   successor from another provider continued `PRAXIS-XPROVIDER-PROOF-01`
   through the inbox channel, and Praxis recorded the successor's own
   identity, its execution's parent and the predecessor's `interrupted`
   disposition.
2. `RQ-ROS-2026-A022`'s remark that protocol 1.3 had not run live through
   GitHub Actions is out of date: `work.continue` and `work.checkpoint`
   (protocol 1.3) ran live in runs `36605336068` and `36677659037`.
3. The gap `EV-ROS-2026-A057` found (no path for an agent that can commit
   but cannot dispatch) is closed for this case.
