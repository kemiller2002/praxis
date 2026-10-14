# Verification pass: PRAXIS-EXEC-06

Group `GROUP-PRAXIS-EXEC-001`, section 5. Criteria from `./praxis work show PRAXIS-EXEC-06`.
Tests: `tests/Praxis.Tests/WebExecutionTests.fs` (`WET`).

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 1 | Work detail page lists the item's executions with state, role, actor, evaluator and containment | met | WET API test: `/work/WI-GOV` contains `id="executions"` and a link to `/executions/<id>`; `WebExecutions.workSection` renders role, state, actor and containment; evaluator is on the execution page |
| 2 | Execution page shows receipt status, unresolved scope effects as obligations, unknown effects, blocked state and verification, labelled distinctly | met | WET "the execution page separates mismatch, unknown effects, obligations and human-required actions with reasons" (`receipt mismatch`, `unknown effect`, `unresolved scope effect: README.md`, `workspace divergence`, `Blocked: waiting on review`) |
| 3 | Legal actions with availability, reasons and a text human-required marker | met | same WET test: `human required`, `<li>not every step receipt matches</li>`; availability shown as text pills `legal`/`blocked` |
| 4 | Human-required transitions only through a form recording a human actor with normal provenance | met | WET unit: form with `human`+`operator` -> `--actor-kind human --actor kem`; WET API: form post completes the human-only execution and the ledger records `actor: casey`, `actorKind: human`; the JSON API without that declaration was refused (409) |
| 5 | No legality computed by the UI; no script | met | only actions with `available: true` get a form (WET: no `complete` form for the blocked action); pages contain no `<script` (WET page and API tests) |
| 6 | Tests | met | WET unit and API tests above |
