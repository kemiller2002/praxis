# Verification pass: PRAXIS-EXEC-03

Group `GROUP-PRAXIS-EXEC-001`, section 5. Criteria from `./praxis work show PRAXIS-EXEC-03`.
Tests: `tests/Praxis.Tests/ExecutionRuntimeTests.fs` (`ERT`).

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 1 | `ros.json` `execution.launchers` maps roles to configured launcher commands; Praxis never selects a provider | met | `ExecutionJson.readPolicy`; ERT "policy: ros.json execution section parses and rejects unknown roles and dimensions"; the launcher is the configured command only (`ExecutionService.launch`). A per-transition key was not added: the launch itself is the legal transition `execution.launch`, and work transitions never launch (PRX-GRP-117) |
| 2 | `execution launch [--dry-run]` requires `execution.launch`, runs in the workspace with `PRAXIS_EXECUTION_*`, records launcher, command, exit code, actor | met | ERT "launch: the configured role launcher runs…" (`launched.txt` holds ID, role, capabilities; `launch-finished.exitCode` = 0; `--dry-run` prints the launcher and runs nothing; implementation without launcher -> 3 "no launcher is configured") |
| 3 | `execution start --launch` accepts the start, then launches | met | ERT launch test starts with `--launch`; `ExecutionCommands.start` launches only after `ExecutionService.start` returned the saved envelope |
| 4 | Roles launchable independently with distinct identities; verification launcher gets the verification capability set only | met | ERT launch test: `PRAXIS_EXECUTION_CAPABILITIES` contains `evaluator.invoke`, not `implementation.modify`; implementation and verification executions have different IDs |
| 5 | `execution.worktree.required` makes `execution start` create the worktree | met | ERT launch test: implementation start without `--worktree` has `workspaceBinding.mechanism` = `git-worktree`; the start record carries `worktreePolicy: required` |
| 6 | Tests, including no-launcher refusal | met | ERT launch test and ERT "legal actions: launch needs a configured launcher and the required host restrictions" |
