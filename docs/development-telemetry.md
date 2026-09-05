# Adaptive development telemetry

ROS telemetry connects intent, execution, repository change, evidence, and result without making any agent vendor canonical. `./ros work begin` starts an execution record automatically and `./ros work complete` finalizes every active execution for that work item. One work item may therefore contain sequential, parallel, resumed, handed-off, human-assisted, or provider-mixed executions.

The canonical local record for each run is `.ros/telemetry/executions/<execution-id>.json`. Records are deliberately segmented rather than appended to one unbounded document. They are suitable for later publication, but this version does not implement a central telemetry service.

## Execution lifecycle

The standard lifecycle is:

```text
work begin
  -> execution start and runtime/capability discovery
  -> work classification and Git baseline
  -> optional runtime snapshots, metrics, events, scope, and quality signals
  -> work complete
  -> final Git snapshot and deterministic metrics
  -> execution finalization
  -> telemetry validation with normal ROS validation
```

`work block` and `work resume` add interruption events and allow ROS to derive blocked duration. A resumed item whose prior execution was already finalized receives a new child execution. An agent handoff or a parallel/subagent run can be represented explicitly with `telemetry start --parent-execution`, `--agent`, and `--subagent`.

Historical work records created before telemetry existed remain valid. The compatibility boundary is explicit: once a work item has `telemetryExecutionIds`, completed work requires those records to be finalized. ROS does not invent telemetry for older history.

## Capability states and zero

Each known normalized metric has a capability state for the execution:

| State | Meaning |
|---|---|
| `supported-observed` | the runtime supports the metric and supplied a value |
| `supported-unavailable` | the runtime family can expose it, but this run did not |
| `unsupported` | the runtime says it cannot provide the metric |
| `unknown` | ROS or the runtime has not mapped the capability |
| `derived` | ROS can calculate it from a named deterministic source |
| `estimated` | a value exists but is an estimate, with confidence |

An observed value of zero is a real metric. Unavailability is represented only by capability state and reason, never by a synthetic zero. Validation rejects nonnumeric values, negative counts, missing sources, incompatible units, estimates without confidence, and several impossible scope/aggregation combinations.

## Normalized and raw layers

The normalized vocabulary is data-driven by [`telemetry/metrics.json`](../telemetry/metrics.json). Every measurement records value, unit, quality (`observed`, `derived`, or `estimated`), aggregation rule, scope, source name/type/mechanism, collection time, schema version, optional dimensions, and currency/confidence where applicable.

Raw snapshots retain legitimate provider data that an adapter does not yet understand. ROS recursively redacts credentials, prompts, messages, content, command arguments/output, transcript paths, absolute working paths, and email fields; it truncates large strings and rejects snapshots over the configured byte limit. The sanitized snapshot lists unmapped leaf fields and redactions. Unknown fields do not fail ingestion.

For example, if a provider adds `usage.quantum_cache_tokens` tomorrow, the existing adapter stores the sanitized field under `rawTelemetry`, surfaces its path as an `unknown` capability, and increments `telemetry.unknown_fields`. Nothing is silently converted or discarded. A later compatible registry/adapter release can promote the field while old raw records remain readable.

## Commands

The automatic lifecycle is enough for deterministic baseline/final metrics. Runtime integrations can add higher-fidelity data:

```bash
# Start another execution for the same active work item.
./ros telemetry start FEAT-142 \
  --provider anthropic --runtime claude-code --session session-123 \
  --classification development --parent-execution EXE-...

# Ingest provider output, hook JSON, a status-line snapshot, or a generic envelope.
./ros telemetry ingest FEAT-142 --adapter openai-codex --input codex-events.jsonl
./ros telemetry ingest FEAT-142 --adapter anthropic-claude-statusline --input status.json
./ros telemetry ingest FEAT-142 --adapter google-gemini-otel --input otel.jsonl
./ros telemetry ingest FEAT-142 --adapter github-copilot-hook --input - --quiet

# Record an explicitly sourced metric. Estimates require --confidence.
./ros telemetry record FEAT-142 --metric tests.passed --value 84 \
  --source-type external-tool --source-name node-test --mechanism tap-summary

# Apply a multi-valued classification and factual R&D context.
./ros telemetry classify FEAT-142 \
  --classification research-development --classification experiment \
  --rd-context rd-context.json --evidence-link EX-ROS-2026-A001

./ros telemetry show FEAT-142
./ros telemetry summary FEAT-142
./ros telemetry adapters
```

`--input -` reads JSON from standard input and `--quiet` keeps hook stdout clean. Input may be one JSON value or JSON Lines. The `generic` adapter accepts this provider-neutral envelope:

```json
{
  "schemaVersion": "1.0.0",
  "snapshotId": "provider-run-42-final",
  "collectedAt": "2026-09-05T01:02:03Z",
  "identity": {"provider": "future-ai", "runtime": "future-cli", "sessionId": "s-42"},
  "capabilities": [
    {
      "metricId": "tokens.input",
      "status": "supported-observed",
      "source": {"type": "runtime-api", "name": "future-cli", "mechanism": "usage-response"}
    }
  ],
  "metrics": [
    {
      "id": "tokens.input",
      "value": 0,
      "unit": "tokens",
      "quality": "observed",
      "scope": "turn",
      "source": {"type": "runtime-api", "name": "future-cli", "mechanism": "usage-response"}
    }
  ],
  "raw": {"usage": {"quantum_cache_tokens": 17}}
}
```

Generic input can also update `classification`, `scope`, `links`, and `qualitySignals`. Stable IDs should be used for requirements, acceptance criteria, evidence, experiments, decisions, defects, dependencies, commits, and pull requests.

## Current provider integration surfaces

Provider capabilities evolve; treat these as adapter guidance, not permanent assumptions.

- **OpenAI Codex:** `codex exec --json` emits JSON Lines with per-turn usage that the `openai-codex` adapter maps to input, output, cached-input, cache-write, reasoning, and total token metrics when present. Interactive Codex environments may expose session and thread IDs to the repository process without exposing token or cost totals; in that case ROS records identity and `supported-unavailable`, never zero. [Codex JSON event source](https://github.com/openai/codex/blob/main/codex-rs/exec/src/exec_events.rs)
- **Anthropic Claude Code:** the status-line JSON supplies session/runtime/model identity, an estimated cumulative session cost, and current-context/cache gauges. Hooks supply lifecycle, tool, compaction, permission, and subagent events. Claude Code OpenTelemetry adds request tokens, costs, retries, active/API time, tool outcomes, and request IDs; ingest transformed JSON with `anthropic-claude-otel`. [Status-line fields](https://code.claude.com/docs/en/statusline), [hooks](https://code.claude.com/docs/en/hooks), [monitoring](https://code.claude.com/docs/en/monitoring-usage)
- **Google Gemini CLI:** hooks expose session and tool lifecycle JSON. Its OpenTelemetry stream exposes input/output/thought/cache/tool tokens, file and line operations, tool latency/outcome, chat compression, model routing, agent turns/duration, memory, and CPU. Keep prompt logging disabled and transform the exporter JSON to the documented attributes before ingestion. [Gemini hooks](https://github.com/google-gemini/gemini-cli/blob/main/docs/hooks/reference.md), [Gemini telemetry](https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/telemetry.md)
- **GitHub Copilot CLI/cloud agent:** repository hooks expose session, tool, error, main-agent, and subagent lifecycle events. Copilot OpenTelemetry follows GenAI semantic conventions and can emit model/tool traces and token metrics. Content capture is not required by ROS and should remain disabled. [Copilot hooks](https://docs.github.com/en/copilot/reference/hooks-reference), [Copilot OpenTelemetry](https://docs.github.com/en/copilot/concepts/agents/opentelemetry)
- **Local/self-hosted and future runtimes:** set the `ROS_TELEMETRY_*` identity environment variables and ingest the generic envelope. Provider-specific extensions belong at this edge. New unmapped fields are retained without changing the core execution model.

ROS does not install vendor hooks automatically: repository-level hook files can execute with developer privileges and may collide with existing project policy. The provider router files (`CLAUDE.md`, `GEMINI.md`, and `.github/copilot-instructions.md`) only point to the canonical `AGENTS.md`; hook enablement remains an explicit, reviewable repository decision.

## Work and R&D classification

Classification is multi-valued. The core vocabulary is: Research, Development, Research & Development, Maintenance, Defect/Bug Fix, Investigation/Diagnostic, Architecture/Design, Documentation, Testing/Verification, Infrastructure/DevOps, Security, Operational/Support, Refactoring, Experiment, Prototype/Proof of Concept, and Administrative/Process. Records store their lowercase stable identifiers. Namespaced `x-...` extensions allow a domain to add a future category without changing the core vocabulary; other unknown values fail validation.

`research-development` requires factual context such as a research question, hypothesis, technical uncertainty, experimental objective, or knowledge gap. The `rd` object can additionally preserve alternatives, controls/comparisons, evidence, results, limitations, failed approaches, resulting knowledge/capability, resolution state, and follow-up experiments. This records facts for later analysis; it is not a tax or legal eligibility determination.

`scope.initial` and `scope.actual` can preserve expected versus discovered root cause, files/components, blockers, complexity, time/cost, dependencies, discoveries, new work, variance, and variance reason. Trivial work can leave these objects empty. `qualitySignals` records how a correction was detected—compiler, type system, test, static analysis, architecture check, runtime, agent self-review, human review, escaped defect, mutation test, or ROS state guard—only when the source is known.

## Mechanical and cooperative guarantees

| Capture | Guarantee |
|---|---|
| execution ID, work-item link, start/final times, wall/blocked duration | mechanical when work uses the ROS CLI |
| repository ID, branch, starting/ending SHA, dirty-state counts | mechanical when Git is available |
| commits, files, lines, extensions, test-file changes, documentation changes | mechanical only from a clean execution baseline; otherwise explicitly unavailable because pre-existing edits prevent trustworthy attribution |
| completion finalization and structural validation | mechanical for work items that have entered the telemetry contract |
| provider/model/runtime/session identity | discovered from a small whitelist of non-secret environment fields, adapter input, or explicit flags; unknown values remain explicit |
| tokens, cost, model/tool time, turns, retries, subagents, and detailed tool events | mechanical only when a provider runtime stream, hook, API, or OpenTelemetry exporter is connected |
| research context, failed approaches, scope variance, requirements, decisions, and evidence links | dependent on agent/human/work-system reporting unless a domain tool exposes them |

Git metrics intentionally fail open to `supported-unavailable` when the execution began dirty. ROS will not attribute another contributor's pre-existing changes to the current run. The record still retains start/end repository state and the reason the execution delta is unavailable.

## Aggregation and comparison

The metric registry declares `sum`, `maximum`, `latest`, `latest-per-session`, or `none` for every normalized metric. Cumulative session totals and context gauges use `latest-per-session`: ROS keeps the latest snapshot for each unique provider session and then combines distinct sessions. It never sums repeated cumulative snapshots, including when two executions on one work item share a session. Per-turn and per-operation deltas use `sum`.

Cross-provider comparisons must account for semantic differences. Providers may include reasoning in output tokens, define cache categories differently, estimate costs with a changing price table, route a configured model to a different served model, or expose current context rather than cumulative usage. Preserve the raw snapshot and source; do not coerce metrics merely to fill a comparison table.

## Privacy, storage, and schema evolution

Capture metadata, not content. Never enable prompt/response/tool-argument capture solely for ROS. Do not place credentials or authentication headers in adapter files. Raw snapshots are bounded per snapshot and split by execution; long-term rotation or external object storage is deferred until repository scale demonstrates a need. Teams publishing records later should define retention, access control, reconciliation, signing, and deletion policies in the central system.

Telemetry schema `1.0.0` is additive at the raw boundary. Readers reject an unknown record schema rather than guessing, while work history with no telemetry remains valid. To normalize a newly useful field:

1. confirm its provider semantics, unit, scope, and cumulative/delta behavior;
2. add a compatible metric definition to `telemetry/metrics.json`;
3. update an edge adapter mapping without deleting the raw field;
4. add observed, unavailable, zero, aggregation, and version tests;
5. document comparison limitations and pricing provenance where relevant.

Breaking identity, quality, unit, or aggregation meaning requires a new telemetry schema major version and a migration that preserves the original record.

## Current limitations

ROS cannot obtain hidden reasoning cycles, self-corrections, precise active-versus-waiting time, human interruption time, authoritative billed cost, model rerouting identity, or detailed tool activity unless the runtime exposes them. This Codex desktop environment exposes session/thread identity to repository commands but not its in-app token/cost counters. Provider hooks may miss UI-only actions, and exporter formats may change. Agent-reported findings remain lower-assurance than runtime or deterministic Git/test output. The local records are publication-ready but are not signed, centrally reconciled, or globally deduplicated in this release.
