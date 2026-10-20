# REL-TOKEN-001: Content-free per-request usage ledger in `telemetry reliability-report`

Priority: P1 (measurement correctness; prerequisite for any phase-level token claim)
Owner system: Praxis
Intake: Praxis document ingestion (draft from Echelon research worker D; coordinator places it)
Existing work items: extends PRAXIS-REL-010 (per-agent attribution, complete) and PRAXIS-PLAN-05 (session transcript metrics). Not a duplicate: those produce run-level sums; this keeps the per-request facts they already compute.
Experimental finding: kemiller2002/cohort-based-engineering `research/token-efficiency/OVERHEAD-ANALYSIS.md` (sections "Data availability" and "P1"), `research/token-efficiency/profiling/out/run-02-echelon.json`

## Observed gap
REL-001 observations carry only run-level token sums from `praxis telemetry reliability-report --agent-binding`.
The trial transcripts are kept in harness state outside the repository and are not retained, so no committed record supports
attributing tokens to workflow phases (claim, test, verify, complete), finding duplicate tool calls, repeated reads, or
cache misses per request. Praxis already derives most of these facts per session
(`src/Praxis.Domain/Telemetry/SessionTranscript.fs:340-361` request dedup, `:376-389` tool-use dedup, `:440-490`
tool calls, repeated reads, builds, test runs) and then reduces them to sums.

## Expected behaviour
1. `telemetry reliability-report --request-ledger FILE` (or an equivalent output section) writes one content-free row per
   deduplicated provider request: request key, agent id, model, timestamp, the usage buckets (input, output with its
   lower-bound label, cache read, cache write 5m/1h), the tool names emitted, and for shell calls only a classified verb
   (`praxis work <verb>`, `dotnet test`, `dotnet build`, `git <verb>`, `other`), plus tool-result byte counts.
2. No prompt, message, command argument, path or tool output is written (same redaction rules as
   `docs/development-telemetry.md` raw snapshots).
3. Row totals reconcile exactly with the report's existing sums (conservation, as `conservationBalanced`).

## Acceptance criteria
- On a fixture transcript, the ledger rows sum to the report's token totals and tool-call count.
- A row for a request whose first entry has `cache_creation_input_tokens > cache_read_input_tokens` is identifiable
  (cold-prefix detection) without the transcript.
- The ledger validates against a published schema; the cohort profiler (`research/token-efficiency/profiling/`) can consume it
  in place of a transcript.
