# Pacing hook contract fixtures

Hook input documents for `praxis pacing gate` (PRX-QUAL-011), one per
provider and hook event Praxis registers the gate for:

| File | Runtime | Event |
|---|---|---|
| `claude-pre-tool-use.json` | Claude Code | `PreToolUse` |
| `claude-stop.json` | Claude Code | `Stop` |
| `claude-transcript.jsonl` | Claude Code | session transcript the gate reads to recover the model |
| `codex-pre-tool-use.json` | Codex | `PreToolUse` |
| `codex-user-prompt-submit.json` | Codex | `UserPromptSubmit` |

The documents follow each runtime's hook input shape (the fields the gate
reads: `hook_event_name`, `model`, `transcript_path`, `agent_id`), with
identifiers anonymized. They were written to the runtimes' documented
input contracts, not captured from a live session; replace them with
captured payloads when a runtime changes its hook input.
`TRANSCRIPT_PATH` is replaced by the test with the transcript fixture's path.

`PacingHookContractTests` drives the real gate with each fixture and checks
that the model is resolved from the payload and that a held gate produces
the runtime's documented denial shape.
