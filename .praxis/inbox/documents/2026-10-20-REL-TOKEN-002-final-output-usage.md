# REL-TOKEN-002: Final output-token counts for Claude Code agents (replace the streaming-start lower bound)

Priority: P1 (measurement correctness; estimated cost is understated)
Owner system: Praxis (telemetry adapters); harness change in cohort REL-001 run-03
Intake: Praxis document ingestion (draft from Echelon research worker D; coordinator places it)
Existing work items: follows PRAXIS-REL-010 / PRAXIS-REL-013. Praxis already labels the value a lower bound
(`src/Praxis.Domain/Telemetry/ReliabilityReport.fs:240-262`); this item obtains the real value.
Experimental finding: cohort `research/token-efficiency/OVERHEAD-ANALYSIS.md` ("P1: output tokens")

## Observed gap
Claude Code writes a transcript line per content block at block start and never rewrites `output_tokens`
(confirmed again on 2026-10-20 sub-agent transcripts: every block of a message repeats the same 2-16 output tokens and
`stop_reason` is null). Run-02 Echelon trials report about 18-25 output tokens per request. Using the run-01 range for
real final messages (120-761 tokens per request, ReliabilityReport.fs:240-246), estimated cost per run is understated by a
median of 5.6% to 43% (profiling/out/run-02-echelon.json, `medianOutputScenarioRelativeUnderstatement`).

## Expected behaviour
1. An adapter ingests a per-request source that carries final usage: Claude Code OpenTelemetry `api_request` events or the
   `token.usage` metric (per the Claude Code monitoring documentation; implementer to confirm field names), or the
   `result` usage of `claude -p --output-format json|stream-json`.
2. Where both sources exist, the final source replaces the transcript's output bucket per request; input and cache buckets
   are cross-checked and a mismatch is reported, not averaged.
3. `tokens.output` loses its lower-bound label only for requests matched to a final source.

## Acceptance criteria
- On a recorded session with both sources, output is no longer `partial`, and input/cache totals equal the transcript's.
- Requests without a final-source match keep the lower-bound label and are counted in the report.
