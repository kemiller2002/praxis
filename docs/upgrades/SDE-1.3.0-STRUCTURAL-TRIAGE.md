# SDE 1.3.0 structural-locality triage

Status: reviewed under WI-0062  
Date: 2026-09-21  
Authority: `DF-ROS-2026-A035`

## Rule used

SDE 1.3.0 defines physical LOC as a review signal. This triage reviews the
current ROS 3.1 tree by semantic responsibility rather than treating 500, 1000,
or 2000 lines as a refactoring target.

No SDE threshold, ignore rule, or strict-verification behavior is changed.

## Dispositions

| File | Current LOC | Band | Disposition | Reason / constraint |
|---|---:|---|---|---|
| `src/Ros.Infrastructure/Work/FileTelemetryFinalizationRepository.fs` | 2850 | justification-required | bounded exception | One telemetry mutation/lifecycle semantic area but several responsibility clusters. Parity-sensitive JSON mutation makes churn-only extraction risky. Next new mutation family or material cluster change must evaluate extraction first. |
| `src/Ros.Cli/Program.fs` | 2684 | justification-required | bounded exception | CLI composition/root routing surface. Domain legality remains below the CLI. No new domain authority may accumulate here; substantial new command families must be extracted. |
| `tests/work-start-fsharp-differential.test.mjs` | 1644 | strong-review | reviewed, no change | Large frozen Node-to-F# golden masters dominate LOC. Splitting the literal would move data without reducing reasoning scope. |
| `tools/ros_cli.mjs` | 1564 | strong-review | frozen compatibility surface | Legacy Node/internal web dependency retained for compatibility after F# authority switch. Do not add new semantic authority here. |
| `tools/ros_telemetry.mjs` | 1517 | strong-review | frozen compatibility surface | Legacy telemetry implementation retained for internal compatibility/differential history. New authoritative telemetry semantics belong in F#. |
| `tests/telemetry-show-fsharp-differential.test.mjs` | 1444 | strong-review | reviewed, no change | Mostly frozen telemetry golden data used to prove F# parity. Physical split would not reduce the semantic test surface. |
| `setup_ros_layout.py` | 1096 | strong-review | data-heavy bootstrap exception | Most size comes from the declarative scaffold/file payload for one bootstrap concern. Existing-file safety/idempotency remains one bounded responsibility. |
| `src/Ros.Domain/Telemetry/TelemetryValidation.fs` | 723 | review | reviewed, no change | Cohesive pure Tier-2 telemetry-validation authority. Splitting individual validation rules would fragment one legality surface. |
| `src/Ros.Contracts/Ordo/ObservationJson.fs` | 622 | review | reviewed, no change | One provider-neutral Ordo observation wire boundary. Multiple schema parsers share the same compatibility/error vocabulary and remain one contract area. |
| `src/Ros.Cli/Lifecycle.fs` | 620 | review | reviewed, no change | Cohesive lifecycle command grammar, help, parsing, and rendering surface. Lifecycle decisions remain in Application/Domain. |
| `tests/npm-bootstrap.test.mjs` | 611 | review | reviewed, no change | One npm/bootstrap acceptance-test area with scenario setup and assertions. |
| `tests/lifecycle-package.test.mjs` | 604 | review | reviewed, no change | One packed lifecycle artifact acceptance surface. Splitting would duplicate package fixture/setup. |
| `tests/work-fsharp-differential.test.mjs` | 566 | review | reviewed, no change | One work-protocol differential/parity suite with shared frozen expectations. |
| `web/app.ts` | 548 | review | reviewed, no change | One backlog UI feature: state, boundary effects, rendering, and interaction. Server remains semantic authority; client does not duplicate transition legality. |
| `src/Ros.Infrastructure/Work/FileBacklogQueueRepository.fs` | 530 | review | reviewed, no change | One backlog queue persistence adapter including atomic JSON/Markdown projection writes. |
| `src/Ros.Infrastructure/Work/FileTelemetryExecutionRepository.fs` | 503 | review | reviewed, no change | One execution-creation persistence adapter. Finalization lives elsewhere, preserving the lifecycle boundary. |
| `tests/Ros.Tests/TelemetryValidationTests.fs` | 503 | review | reviewed, no change | One typed regression suite for the single TelemetryValidation authority. |

## Important interpretation

A reviewed warning may remain visible in `sde verify --strict`.

That is intentional. SDE 1.3.0 does not yet provide a machine-readable
per-file exception mechanism, and inventing an ignore list would weaken the
review signal. The durable evidence is this triage plus DF-ROS-2026-A035.

A future finding is not covered merely because it is in the same file. If LOC
or responsibilities grow, or normal changes begin traversing unrelated
regions, review again.

## Refactoring triggers

Refactor rather than renew an exception when any of these becomes true:

- a file gains an unrelated semantic authority;
- a normal narrow change repeatedly requires unrelated regions;
- an existing responsibility cluster can be extracted behind a stable typed
  contract without duplicating authority;
- merge contention or agent discovery repeatedly crosses unrelated clusters;
- Program.fs begins making domain decisions rather than routing them;
- the telemetry finalization file gains another independent mutation family.

## Verification

Completion of WI-0062 requires:

1. current SDE 1.3.0 strict verification captured;
2. all current SDE-STRUCT-001 findings accounted for here;
3. ROS registry rebuilt for DF-ROS-2026-A035;
4. `./ros registry check` and `./ros validate` green;
5. full ROS CI green.

The structural warning count is not itself a completion criterion.
