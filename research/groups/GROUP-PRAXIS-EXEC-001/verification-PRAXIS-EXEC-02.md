# Verification pass: PRAXIS-EXEC-02

Group `GROUP-PRAXIS-EXEC-001`, section 5 of the group analysis. Each criterion is from
`./praxis work show PRAXIS-EXEC-02` and was exercised directly, not inferred from the
shared design. Tests: `tests/Praxis.Tests/ExecutionRuntimeTests.fs` (`ERT`) and
`tests/Praxis.Tests/ExecutionGovernanceTests.fs` (`EGT`), run with
`PRAXIS_TEST_FILTER=<name> dotnet tests/Praxis.Tests/bin/Release/net10.0/Praxis.Tests.dll`.

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 1 | `--evaluator-command`; `evaluate` runs only the declared command (other refused, exit 3); legal action `execution.evaluate` gates role, state, evaluator | met | ERT "evaluate: only the declared command…" (undeclared -> 3; `--command "rm -rf src"` -> 3 and `src/a.txt` still exists; review role -> 3 "may not invoke the evaluator"); ERT "legal actions: evaluation needs a declared command and evaluator authority"; `ExecutionService.evaluate` in `src/Praxis.Application/Execution/Operations.fs` |
| 2 | Record holds actor id/kind, command, candidate, fingerprint, exit code or unknown, evidence | met | ERT evaluate test asserts `candidate` = HEAD, `exitCode` 0, `actor.id`, `evidence[0]`; `ExecutionJson.verification` writes `command`, `evaluator`; unknown outcome omits `exitCode` (`exitCode, passed, reason` match in `evaluate`) |
| 3 | Verification of another candidate blocks `execution.complete` as stale | met | ERT evaluate test: new commit, then `transition --action complete` -> 3 "evaluate again"; ERT "legal actions: verification of another candidate is stale for completion" |
| 4 | Every appended entry carries actor/observer, role, evaluator fingerprint, revision | met | ERT "ledger: every appended step entry carries actor, role, revision and evaluator identity" (declared, started, observed all attributed; `show --json` lists 3 attributions) |
| 5 | Every `praxis execution` mutation is legal-action gated; refusal paths tested | met | `step.declare` and `step.observe` added to `LegalActions.evaluate`; ERT ledger test: after abandon, `step declare` -> 3 "step.declare is not legal now", `step observe` -> 3; EGT cleanup/expand-scope/complete refusals; docs/execution-runtime.md "Legal actions" lists every gated command |
