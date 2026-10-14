# Verification pass: PRAXIS-EXEC-05

Group `GROUP-PRAXIS-EXEC-001`, section 5. Criteria from `./praxis work show PRAXIS-EXEC-05`.
Tests: `tests/Praxis.Tests/WebExecutionTests.fs` (`WET`), which starts the real `praxis web serve`.

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 1 | `GET /api/executions[?workItem=ID]` and `/api/executions/ID` return the CLI's own execution JSON with legal actions, reasons and actor requirement | met | WET "/api/executions exposes work-bound executions with legal actions, reasons and actor requirements" (work-bound execution listed after `start`; every action has `reasons` and `actorRequirement`; `execution.evaluate` unavailable with reasons); routes run `execution list|show --json` (`WebExecutions.tryHandle`) |
| 2 | `POST /api/executions/ID/transitions` runs the CLI transition; an illegal one is a structured refusal with state unchanged | met | WET "a refused execution transition is a structured 409 and changes nothing…" (409, `refused: true`, "requires a human actor", state still `active`) |
| 3 | `GET /api/control-plane` declares host, port and loopback scope | met | WET unit "the control plane declares its listen scope"; WET API test reads `loopbackOnly: true` from the served host |
| 4 | Provider/model/runtime only as execution-host attributes of the actor | met | the API returns the envelope's `actor` object unchanged (provider, model, runtime beside id and kind); the page labels them "Execution host" (WET page test) and work state stays in Praxis records |
| 5 | Stateless host, no added rule: every route shells out to the CLI | met | `WebExecutions.fs` has no effect API and calls only `CliProcess.runSelf` (CLI ratchet: new file, 0 effects); legality is read from `legalActions` |
| 6 | Tests cover each route | met | WET: list, show, control-plane, transition refusal (409), form transition, unknown execution (400), execution page |
