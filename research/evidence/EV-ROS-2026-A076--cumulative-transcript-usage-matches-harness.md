---
id: EV-ROS-2026-A076
title: Cumulative transcript usage in Praxis equals the CONSTRUCT-008 harness on six recorded runs
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-13
updated: 2026-10-13
research_area: repository-operating-system
evidence_type: primary
supports: []
contradicts: []
related_documents:
  - EV-ROS-2026-A064
  - docs/development-telemetry.md
  - tests/Praxis.Tests/TelemetryUsageAttributionTests.fs
  - tests/Praxis.Tests/Fixtures/construct-008-A1-usage-only.jsonl
tags: [telemetry, tokens, cost, attribution, claude, construct-010]
confidence: high
---

# Evidence

## Claim

The `anthropic-claude-session` adapter and `telemetry usage` report
(WI-0101, CONSTRUCT-010 Phase 6) reproduce, field for field, the cumulative
usage the research harness `usage.py` (research-documents
`research/construction-by-design/CONSTRUCT-008/harness/usage.py`) committed
as `usage.json` for six recorded runs: three CONSTRUCT-008 runs and three
time-entry-expanded-01 runs. No difference needs explaining.

## Method

Each run's `transcript.jsonl` (a Claude Code subagent transcript) was
ingested with the built CLI into its own execution in a scratch Praxis root:

```
praxis --root ROOT telemetry ingest EXE --adapter anthropic-claude-session --input transcript.jsonl
praxis --root ROOT telemetry usage --by execution
```

The report's agent-origin totals were compared with the run's committed
`usage.json`. `cumulative_total` is the sum of the four buckets, as in
`usage.py`; `wall_ms` is `agent_wall_seconds` × 1000.

## Results

| run | field | usage.json | Praxis | match |
|---|---|---|---|---|
| CONSTRUCT-008/A1 | requests | 13 | 13 | yes |
| CONSTRUCT-008/A1 | input | 26 | 26 | yes |
| CONSTRUCT-008/A1 | cache_write | 41786 | 41786 | yes |
| CONSTRUCT-008/A1 | cache_read | 792100 | 792100 | yes |
| CONSTRUCT-008/A1 | output | 213 | 213 | yes |
| CONSTRUCT-008/A1 | tool_calls | 13 | 13 | yes |
| CONSTRUCT-008/A1 | wall_ms | 208178 | 208178 | yes |
| CONSTRUCT-008/A1 | cumulative_total | 834125 | 834125 | yes |
| CONSTRUCT-008/A1 | (extra) cache_write 5m/1h, reasoning | - | 41786/0, None | - |
| CONSTRUCT-008/B1 | requests | 17 | 17 | yes |
| CONSTRUCT-008/B1 | input | 34 | 34 | yes |
| CONSTRUCT-008/B1 | cache_write | 62681 | 62681 | yes |
| CONSTRUCT-008/B1 | cache_read | 1311386 | 1311386 | yes |
| CONSTRUCT-008/B1 | output | 215 | 215 | yes |
| CONSTRUCT-008/B1 | tool_calls | 18 | 18 | yes |
| CONSTRUCT-008/B1 | wall_ms | 208935 | 208935 | yes |
| CONSTRUCT-008/B1 | cumulative_total | 1374316 | 1374316 | yes |
| CONSTRUCT-008/B1 | (extra) cache_write 5m/1h, reasoning | - | 62681/0, None | - |
| CONSTRUCT-008/C1 | requests | 11 | 11 | yes |
| CONSTRUCT-008/C1 | input | 22 | 22 | yes |
| CONSTRUCT-008/C1 | cache_write | 41058 | 41058 | yes |
| CONSTRUCT-008/C1 | cache_read | 689244 | 689244 | yes |
| CONSTRUCT-008/C1 | output | 156 | 156 | yes |
| CONSTRUCT-008/C1 | tool_calls | 11 | 11 | yes |
| CONSTRUCT-008/C1 | wall_ms | 136155 | 136155 | yes |
| CONSTRUCT-008/C1 | cumulative_total | 730480 | 730480 | yes |
| CONSTRUCT-008/C1 | (extra) cache_write 5m/1h, reasoning | - | 41058/0, None | - |
| time-entry-expanded-01/A1 | requests | 26 | 26 | yes |
| time-entry-expanded-01/A1 | input | 54 | 54 | yes |
| time-entry-expanded-01/A1 | cache_write | 106114 | 106114 | yes |
| time-entry-expanded-01/A1 | cache_read | 2520605 | 2520605 | yes |
| time-entry-expanded-01/A1 | output | 293 | 293 | yes |
| time-entry-expanded-01/A1 | tool_calls | 27 | 27 | yes |
| time-entry-expanded-01/A1 | wall_ms | 711288 | 711288 | yes |
| time-entry-expanded-01/A1 | cumulative_total | 2627066 | 2627066 | yes |
| time-entry-expanded-01/A1 | (extra) cache_write 5m/1h, reasoning | - | 106114/0, None | - |
| time-entry-expanded-01/B1 | requests | 46 | 46 | yes |
| time-entry-expanded-01/B1 | input | 92 | 92 | yes |
| time-entry-expanded-01/B1 | cache_write | 180265 | 180265 | yes |
| time-entry-expanded-01/B1 | cache_read | 6271304 | 6271304 | yes |
| time-entry-expanded-01/B1 | output | 533 | 533 | yes |
| time-entry-expanded-01/B1 | tool_calls | 49 | 49 | yes |
| time-entry-expanded-01/B1 | wall_ms | 818874 | 818874 | yes |
| time-entry-expanded-01/B1 | cumulative_total | 6452194 | 6452194 | yes |
| time-entry-expanded-01/B1 | (extra) cache_write 5m/1h, reasoning | - | 180265/0, None | - |
| time-entry-expanded-01/C2 | requests | 31 | 31 | yes |
| time-entry-expanded-01/C2 | input | 64 | 64 | yes |
| time-entry-expanded-01/C2 | cache_write | 125194 | 125194 | yes |
| time-entry-expanded-01/C2 | cache_read | 3664873 | 3664873 | yes |
| time-entry-expanded-01/C2 | output | 436 | 436 | yes |
| time-entry-expanded-01/C2 | tool_calls | 31 | 31 | yes |
| time-entry-expanded-01/C2 | wall_ms | 543711 | 543711 | yes |
| time-entry-expanded-01/C2 | cumulative_total | 3790567 | 3790567 | yes |
| time-entry-expanded-01/C2 | (extra) cache_write 5m/1h, reasoning | - | 125194/0, None | - |
All 54 compared values match exactly.

Request attribution over the six executions: 144 requests attributed, no
duplicate holdings, no conflicts, no unverified executions.

Beyond `usage.py`: the cache-write TTL split is reported for every run (all
cache writes were 5-minute); reasoning tokens are `unknown` for every run.
time-entry-expanded-01 C2 shows why: one of its 31 requests carries
`output_tokens_details.thinking_tokens`, so the bucket is
`supported-unavailable` ("reported on 1 of 31 requests") rather than a low
partial total. Its other new provider fields (`iterations[]`,
`server_tool_use.*`, `speed`) are preserved under the raw snapshot's
`usage.extensions`/`usage.labels` and discovered as `unknown` capabilities.

## Golden test

`tests/Praxis.Tests/Fixtures/construct-008-A1-usage-only.jsonl` is
CONSTRUCT-008 run A1's transcript (sha256
`af15e33a50e3e161882fff1f5caba44f0ac810003d3b728a5f85a53670acf4ae`) reduced
to entry types, IDs, timestamps, usage blocks and tool names, with all
prompt, message, tool input and output content removed (this repository is
public). `usage.py` reports identical requests, usage, tool calls and wall
time for the reduced and the original file; only its character-count
`estimated_output_tokens` differs, which needs the removed text. The golden
test asserts 13 requests; input 26; cache write 41,786; cache read 792,100;
output 213; cumulative 834,125; 13 tool calls (Bash 12, SubagentHandback 1);
agent wall time 208,178 ms.

A second golden test covers the CONSTRUCT-010 form: five real assistant
events (two requests) from one remote session's `list_events` page, content
removed, normalized by `CONSTRUCT-010/harness/events-to-transcript.py`.
`usage.py` and Praxis agree: 2 requests; input 4; cache write 3,835 (all
1-hour); cache read 676,526; output 5; cumulative 680,370; 2 tool calls;
4,701 ms.

## Limitations

- The transcripts' `output_tokens` is the count written at streaming start,
  a lower bound on the provider's final output count (noted by `usage.py`
  itself). Praxis carries it as a lower bound (`bound: lower`), never as the
  final count. It keeps the last-written value per request; every recorded
  run repeats identical usage per request, so first and last agree.
- None of these transcripts records an API error, so retries and request
  failures are `unknown`, not zero.
- No provider-reported cost is in a transcript; cost is `unknown` unless the
  platform's cost is recorded or a dated price table is supplied.
