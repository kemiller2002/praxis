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

_Not yet written. The successor adds this section._
