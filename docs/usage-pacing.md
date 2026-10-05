# Provider usage pacing

Praxis can pace Claude Code and Codex subscription usage so long-running agent work
does not consume a weekly allowance far ahead of the clock and then strand a work
group near the end of the quota window.

This implementation is F#/.NET only. Its pacing behavior was informed by
[jpwinans/usage-auto-pause](https://github.com/jpwinans/usage-auto-pause) (MIT)
and reimplemented around Praxis domain state. Praxis does not take a Python or
Node dependency on that project.

## Policy

The default policy is deliberately simple and shared across providers:

- an applicable quota window strictly above 98% holds until its reset;
- a seven-day window at least 8 hours ahead of a flat weekly pace creates a
  shared hold;
- after that trigger, the hold remains while the lead is above 4 hours;
- a fresh reading at or below +4 hours releases it;
- stale or missing quota data never creates a weekly hold, but it also cannot
  clear a hold already established from fresh evidence;
- a >98% window becomes a durable hard-limit hold that survives provider
  outage, partial responses and restarts; it ends at its reset time or when a
  fresh reading of the same window shows usage at or below 98%;
- a weekly latch is bound to its quota window's reset identity, so a new
  weekly window never inherits the previous window's latch;
- completeness is tracked per expected window (`observed`, `missing`,
  `invalid`): a missing Codex weekly window or a previously reported Claude
  scoped window that disappears makes the reading stale, never fresh;
- unreadable, partially valid or newer-schema safety state is
  `indeterminate` and blocks work (see "Safety state and failure behaviour");
- the longest active hold is the binding reason;
- an override bypasses gating without deleting the latch.

The +8h/+4h split is hysteresis. It prevents repeated stop/start cycles near a
single threshold.

## Commands

Inspect current capacity:

    praxis pacing status --provider codex
    praxis pacing status --provider claude
    praxis pacing status --provider codex --model gpt-5.6-codex --json

Enable or disable the emergency bypass:

    praxis pacing override on
    praxis pacing override off

Recover from unreadable or newer-schema safety state (moves it aside as
evidence; readable state is never quarantined):

    praxis pacing state quarantine

Live state is local, not repository state. By default it is stored under
`~/.praxis/usage-pacing`. Set `PRAXIS_PACING_DIR` or pass `--state-dir`
to isolate it.

## Hook gates

The gate is synchronous. It holds the pending hook call, periodically refreshes
quota, and exits successfully when the pacing state permits work again.

For Codex, register the same command for `UserPromptSubmit` and `PreToolUse`:

    praxis pacing gate --provider codex

Use a hook timeout of at least 691200 seconds (8 days). Praxis stops a still-held
call after 7 days plus 1 minute with a structured denial, before that hook
timeout.

For Claude Code, register this command for `PreToolUse`:

    praxis pacing gate --provider claude

Use a hook timeout of at least 604800 seconds (7 days). Praxis stops a still-held
call after 6 days with a structured denial.

The gate checks the override every few seconds, while live provider quota is
refreshed at most once per minute.

## Provider acquisition

Codex is queried through the signed-in local `codex app-server --stdio`
`account/rateLimits/read` RPC. The quota query does not start a model turn and
does not read credentials directly.

Claude uses the existing `Claude Code-credentials` entry in the macOS Keychain
and sends only its unexpired OAuth access token to
`https://api.anthropic.com/api/oauth/usage` with the observed OAuth beta header.
Praxis refuses HTTP redirects so the bearer token is not forwarded elsewhere.
This Claude endpoint and credential shape are observed implementation
dependencies, not stable provider contracts. Claude live pacing therefore
currently targets macOS and should be rechecked when Claude Code changes login
or usage APIs.

Successful readings are cached locally. If a refresh fails, Praxis can preserve
known hard-exhaustion evidence and any established weekly latch. Cached telemetry
never becomes fresh evidence merely because it was read from disk.

## Scope and limits

Pacing is a boundary gate, not a billing cap. It cannot stop an already-running
shell process, an in-flight model response, a remote client that does not pass
through the hook, or concurrent clients between checks. Claude's first model
response can occur before its `PreToolUse` gate.

Model-scoped Claude weekly limits apply only to matching models. When model
identity is unavailable, Praxis conservatively evaluates all reported scoped
limits. For Claude tool hooks, Praxis can recover the calling model from the
session/subagent transcript. Codex uses the hook payload model and maps model
names containing `spark` to the `codex_bengalfox` quota bucket; other models
use `codex`.

## Safety state and failure behaviour

`hold.json` is schema-versioned (`schemaVersion` 2, with a monotonically
increasing `revision`). Each hold records its basis: `weekly-lead` with the
window's `resetsAt`, or `hard-limit` with `usedPercent` and `resetsAt`.
Schema 1 documents (no `schemaVersion`) are migrated on read; their latches
adopt the next fresh reading's window identity.

A persistence failure never becomes permission to continue:

- corrupt, truncated or partially valid state, and state written by a newer
  Praxis, are reported as `stateIntegrity: indeterminate`; the gate denies
  immediately and the document is preserved, not overwritten;
- lock contention (`PRAXIS-PACING-STATE-LOCK`) and write failures
  (`PRAXIS-PACING-STATE-WRITE`) are typed faults; the gate renders them as an
  explicit deny rather than crashing, because agent runtimes treat a crashed
  hook as a non-blocking error;
- `praxis pacing status` exits `3` when safety state is indeterminate or
  cannot be read, and `1` when the provider is unavailable;
- the only ways past indeterminate state are the explicit override or
  `praxis pacing state quarantine`.

`snapshot-<provider>.json` is a display and merge cache only. Losing or
corrupting it cannot release a hold, because hard and weekly holds live in
`hold.json`.

## Architecture

The pacing state machine is pure and lives in `Praxis.Domain.Pacing`.
Orchestration, typed ports and the integrity/transaction decisions live in
`Praxis.Application.Pacing`. Provider calls, credential access, normalization,
cache and state files, locking and diagnostics live in
`Praxis.Infrastructure.Pacing`. `PacingCommands.fs` only parses arguments,
renders hook output and status, and selects exit codes; architecture tests
enforce that split.
