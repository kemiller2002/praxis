# REL-TOKEN-004: minimal agent tool profile for workers that Praxis dispatches

Priority: P3 (harness-level context reduction); does not change F1-F3
Owner system: Praxis
Intake: Praxis document ingestion (draft from Echelon research worker F; coordinator places it)
Existing work items: none found. A read-only search of `src/` for agent launch code (`claude -p`, `--tools`,
`allowedTools`, `--strict-mcp-config`) found no place where Praxis launches a Claude Code worker today; the ECIR
`dispatch` in `src/Praxis.Infrastructure/Planning/EcirHostPolicy.fs:98` is a policy check, not a launcher. The
requirement therefore applies to the launch specification Praxis emits or owns when it dispatches workers, and to any
launcher added later.
Experimental finding: cohort `research/token-efficiency/prefix/PREFIX-COMPOSITION.md` (exploratory), following
`research/token-efficiency/OVERHEAD-ANALYSIS.md` §3.2 and §5 item 5 ("P3")

## Observed cost
Every request re-reads the runtime prefix (system prompt and tool definitions). In run-01 and run-02 this prefix was
about 40.6k tokens, 67-75% of cache-read tokens and 24-31% of estimated cost (OVERHEAD-ANALYSIS.md §3.2). Probes of an
isolated headless worker (Claude Code 2.1.306, `claude-opus-5-5`, fresh config dir, no MCP servers, n = 3 per
configuration) give:
- default built-in tool set (25 tools): 16,513 tokens on the first request (range 16,510-16,516);
- `--tools Bash,Read,Edit,Write`: 5,821 tokens (range 5,818-5,826);
- difference: 10,692 tokens per request (65% of the isolated prefix), about 117.6k tokens per 11-request run, about
  $0.024 per run if read from cache and about $0.107 if the first request writes it (1-hour write; price table
  2026-10-20.1).
A worker that inherits a host session's tools, MCP servers and CLAUDE.md carries more (one anecdotal host-environment
call: 36,042 tokens). An Echelon worker needs only to read and edit files and run a shell; it reaches Praxis, Dokimos,
Ordo, git and the test runner through Bash.

## Expected behaviour
1. Praxis defines one named agent tool profile, `minimal-worker`, as data: the built-in tool list
   (`Bash, Read, Edit, Write`), an empty MCP server set, and the runtime flags that apply it (for Claude Code:
   `--tools Bash,Read,Edit,Write --strict-mcp-config --mcp-config '{"mcpServers":{}}'`).
2. When Praxis dispatches a worker, or emits a launch command or specification for one, it uses this profile by
   default. A different profile must be named explicitly, and the dispatch record states the profile name and its
   digest.
3. Telemetry records the profile per agent binding, so reliability reports can split cost by profile and the first
   request's prefix size can be checked against the expected value.
4. Every worker in one dispatch group gets the same profile. In experiments, every arm gets the same profile.

## Acceptance criteria
- A dispatch with no profile argument produces the `minimal-worker` tool list and empty MCP set, and records the
  profile name and digest.
- The profile is deterministic data (no host-dependent defaults): the same Praxis version gives the same flags and
  digest.
- The REL-001 gate cases keep their results with workers launched under the profile (claim, verify,
  complete-verified via Bash only).
- A measured first-request context under the profile, with the pinned CLI, is recorded in the release evidence.
  This replaces the exploratory figure above.
