---
id: EV-ROS-2026-A011
title: ROS execution-telemetry architecture and provider capability survey
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-05
updated: 2026-09-05
supersedes: []
superseded_by: []
research_area: repository-operating-system
evidence_type: primary
supports:
  - DF-ROS-2026-A010
related_documents: [docs/development-telemetry.md]
tags: [telemetry, provider-survey, repository-archaeology, data-quality]
confidence: high
---

# Evidence

## Repository observations

Inspected on `main` at starting commit `aa06e6be9da363db2da33308634bbf5268b759e1` with pre-existing uncommitted Project Administration Hub work preserved.

- `tools/ros_cli.mjs` was the executable transition and validation seam. It already derived dirty paths with Git, required work evidence, wrote `.ros/context/current.json`, and appended small idempotent `.ros/events/events.jsonl` records.
- `lib/bootstrap.mjs` and the two starter manifests install a frozen, dependency-free Node CLI and governance snapshot into consuming repositories. A capability absent from those manifests would not become normal ROS behavior.
- Existing schemas covered artifacts, work protocol, and external adapters. No execution/session schema, token/cost metric, runtime capability state, provider adapter, or aggregation rule existed.
- Existing agent behavior was inherited primarily from root `AGENTS.md` and `BOOTSTRAP.md`. `prompts/` contains bounded/historical prompts rather than a shared runtime contract. Claude, Gemini, and Copilot router files were absent.
- `.ros/events` is appropriate for small semantic publication events, but mixing potentially numerous provider snapshots into it would weaken its current contract and create one growing log.
- The work context predates telemetry and contains completed history. Requiring retroactive execution records would fabricate evidence and destroy backward compatibility.
- Git cannot attribute execution-specific line/file changes from only a commit and status summary when the worktree starts dirty. Treating the entire end diff as this run's work would include pre-existing user changes.

## Current runtime/provider observations

Official sources inspected 2026-09-05:

- OpenAI Codex defines `turn.completed` usage fields for input, cached input, cache write, output, and reasoning output tokens in its JSON execution event source: https://github.com/openai/codex/blob/main/codex-rs/exec/src/exec_events.rs. The current Codex desktop repository process exposed whitelisted session/thread environment identifiers but no token or cost counters.
- Claude Code status-line JSON exposes session/runtime/model, estimated session cost, wall/API duration, line changes, context size/utilization, and current input/output/cache usage: https://code.claude.com/docs/en/statusline. Claude hooks expose session, compaction, tool, permission, task, and subagent lifecycle events: https://code.claude.com/docs/en/hooks. Claude monitoring exposes token/cache counts, request IDs, retries, active time, time-to-first-token, cost, and tool outcome/duration while redacting content by default: https://code.claude.com/docs/en/monitoring-usage.
- Gemini CLI hooks receive session/lifecycle/tool JSON: https://github.com/google-gemini/gemini-cli/blob/main/docs/hooks/reference.md. Gemini OpenTelemetry documents input/output/thought/cache/tool tokens, file and line operations, compression, routing, agent turns/duration, tool latency/outcome, memory, CPU, and GenAI semantic attributes: https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/telemetry.md.
- GitHub Copilot CLI/cloud agent hooks cover session, prompt, tool, error, main-agent, and subagent events: https://docs.github.com/en/copilot/reference/hooks-reference. Copilot OpenTelemetry provides traces, token metrics, and events while content is excluded by default: https://docs.github.com/en/copilot/concepts/agents/opentelemetry.

## Interpretation and limits

The evidence supports a stable execution envelope with replaceable provider adapters, not one universal fixed field list. All surveyed providers can expose more through configured structured output, hooks, or OTel than a repository process can assume automatically. Provider metrics differ in scope: Codex JSON is per turn, Claude status-line token fields are current-context gauges, and some cost values are client estimates or cumulative session values. Raw content options create avoidable privacy risk and are unnecessary for the analytical goals.

Confidence is high for the inspected repository boundaries and documented provider surfaces, medium for long-term field stability, and low for exact comparability across providers without matched real-run experiments. No claim is made that undocumented internal telemetry is accessible or that configured/requested model identity proves the provider-served model.
