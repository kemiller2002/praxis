---
id: EV-ROS-2026-A057
title: "Cross-provider continuation attempt 1: an OpenAI successor reconstructed the handoff from repository state but could not invoke Praxis"
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-29
updated: 2026-09-29
research_area: repository-operating-system
evidence_type: secondary
supports: [RQ-ROS-2026-A023]
related_documents:
  - RQ-ROS-2026-A022
  - RQ-ROS-2026-A023
  - DF-ROS-2026-A041
  - DF-ROS-2026-A045
  - docs/remote-agent-contract.md
  - .github/workflows/praxis-remote.yml
tags: [continuity, cross-provider, remote-execution, openai]
confidence: medium
derived_from: [RQ-ROS-2026-A022]
provenance:
  contributions:
    EXE-20260929T161246994Z-10477f29:
      operations: [created]
      at: 2026-09-29T16:13:02.304Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "PRAXIS-REMOTE-INBOX-01: capture the cross-provider continuation gap and its remedy (re-landed from claude/remote-inbox-relay-v2)"
---

# Cross-provider continuation attempt 1

## Setup (verified)

Work item `PRAXIS-XPROVIDER-PROOF-01` tests `RQ-ROS-2026-A022`'s
cross-provider requirement. A Claude Code execution (`agent:anthropic/claude-code`,
`EXE-20260929T121754955Z-09207ab8`) wrote part 1 of
`docs/proofs/cross-provider-continuation.md` on branch
`proof/chatgpt-continuation`. It recorded durable checkpoint
`6ee7e361d7e244e2be1c690e` at commit
`b64303a5f19d8b1b825adefaabd5e7521aab464b` (step `part-1`), then stopped.
The next action exists only in the checkpoint.

## What the OpenAI successor reported

This part is a secondary source: the successor's own report, relayed to
this repository by its owner. No record of that session exists in this
repository.

The successor worked from repository state only. It:

- identified the active work item, the predecessor execution and identity,
  checkpoint `b64303a…` and its step, summary and next action;
- found branch head `36020735d9cd5e4f258994f2bec6bbe605d496cd`, one commit
  beyond the checkpoint, and verified that this commit changes only
  Praxis-owned files;
- could not invoke Praxis:
  - `git clone` failed with `Could not resolve host: github.com`, because
    the sandbox had no network;
  - `praxis`, `ros` and `dotnet` were absent (Node and npm were present);
  - release assets could not be downloaded;
  - its GitHub connector had repository write access but exposed no
    `workflow_dispatch` operation, so it could not use `praxis-remote.yml`
    either;
- stopped without mutating anything. It did not hand-edit `.ros/`, did not
  edit the proof document before `work.continue`, did not reuse or claim
  the predecessor's execution, and did not claim success.

## What this repository shows (verified 2026-09-29, after the attempt)

- The branch head is still `36020735d9cd5e4f258994f2bec6bbe605d496cd`, locally
  and on the remote (`git ls-remote`).
- `git diff --name-only b64303a 3602073` lists only
  `.ros/context/current.json`, `.ros/events/events.jsonl` and
  `.ros/telemetry/executions/EXE-20260929T121754955Z-09207ab8.json`.
- The work item is `active`. Its only events are `work.started` and
  `work.checkpointed`, both by `anthropic/claude-code`. There is no
  `work.continued` event and no successor execution.
- `work context --text` reports freshness `current` and the checkpoint as
  recoverable now. `validate` passes.

## Findings

1. The durable handoff worked as designed. An independent executor from
   another provider recovered the exact checkpoint, its verification, what
   was completed and the next action, with no access to the predecessor.
2. Continuation failed only because that executor had no way to run
   Praxis:
   - `praxis-remote.yml` accepts requests only through `workflow_dispatch`
     or `workflow_call`;
   - the remote agent contract assumes every remote agent can dispatch a
     workflow;
   - an agent whose GitHub integration can commit to the repository but
     cannot dispatch Actions has no path at all.

   That gap is `RQ-ROS-2026-A023`.
3. The successor's refusal to improvise is the behaviour the governance
   requires. The attempt is recorded as a truthful failure, not as a partial
   success.
