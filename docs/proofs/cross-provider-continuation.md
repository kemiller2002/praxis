# Cross-provider continuation proof

Live test of `RQ-ROS-2026-A022`: an executor session is disposable, and
repository state plus Praxis state is the continuity boundary. One provider
starts work item `PRAXIS-XPROVIDER-PROOF-01` and stops. A different provider
finishes it using only this repository and its Praxis state, with no access
to the first executor's machine or conversation.

## Part 1 — predecessor

- Executor: Claude Code (Anthropic), recorded by Praxis as
  `agent:anthropic/claude-code`.
- Wrote this section, committed and pushed it to branch
  `proof/chatgpt-continuation`, recorded a durable checkpoint, and stopped.
- Left the next action in the checkpoint, not in chat.

## Part 2 — successor

- Executor: ChatGPT (OpenAI), recorded by Praxis as `agent:openai/chatgpt`,
  provider `openai`, model `gpt-5.6-sol`, runtime `chatgpt`, in successor
  execution `EXE-20260929T173543602Z-75ccd1b9`.
- Recovered predecessor execution `EXE-20260929T121754955Z-09207ab8` and
  durable checkpoint commit `b64303a5f19d8b1b825adefaabd5e7521aab464b`
  from repository and Praxis state alone.
- Verified the checkpoint against the remote before takeover. GitHub showed
  the checkpoint commit as the branch merge base/ancestor, with one later
  Praxis-state-only commit, and `work.continue` reported the checkpoint
  recoverable and verified with status `contained-without-meaningful-change`.
- Took over with `work.continue` under the successor's own OpenAI/ChatGPT
  identity. Praxis created the successor execution and marked the predecessor
  execution interrupted.
- Wrote this section from the durable repository handoff without access to
  the predecessor's machine or conversation.
