# Group analysis: GROUP-PRAXIS-PLANNING-002

- Group: `GROUP-PRAXIS-PLANNING-002` (advisory; recorded here and in the members' descriptions, because grouped-mode commands from `PRAXIS-GROUP-08..10` are not on main yet)
- Members: `PRAXIS-MISC-02`, `PRAXIS-MISC-03`, `PRAXIS-MISC-04`, `PRAXIS-MISC-05`, `PRAXIS-MISC-06`
- Execution repository: `kemiller2002/praxis`
- Base commit: `f242aae6`
- Machine-readable companion: [`group-analysis.json`](group-analysis.json) (`praxis.group-analysis/1`)

## 1. Members

| Member | Obligation | Acceptance criteria | Depends on |
|---|---|---|---|
| PRAXIS-MISC-02 | PRX-PLAN-020: observe CI for checkpoints waiting on CI | see JSON (6) | - |
| PRAXIS-MISC-03 | PRX-PLAN-081: historical merge-conflict evidence in the collision graph | see JSON (5) | - |
| PRAXIS-MISC-04 | PRX-PLAN-152: replay compares predicted and observed cost | see JSON (5) | - |
| PRAXIS-MISC-05 | PRX-PLAN-162: compare recommendations with what happened | see JSON (4) | - |
| PRAXIS-MISC-06 | PRX-PLAN-170: persist estimate-error history | see JSON (5) | PRAXIS-MISC-04 |

## 2. Reuse inventory

See `reuseInventory` and `searches` in the JSON companion. Summary: CI uses
the existing `ObservationKind.ContinuousIntegration*` observations and the
existing dependency resolution; historical conflicts are one more
`CollisionSignal`; cost replay reuses `History` cost evidence rules and the
replay no-hindsight filter; the recommendation comparison reuses
`Snapshot.compare`'s inputs.

## 3. Group-level design

- Common architecture and shared invariants: the planner is read-only
  (PRX-PLAN-001) and deterministic for identical inputs (PRX-PLAN-002).
  External evidence is an `Observation` with provenance and never rewrites
  recorded state (PRX-PLAN-021). Domain stays pure; Git and network queries
  live in Infrastructure behind `PlanningReadPort`.
- Conflicting requirements: PRX-PLAN-170 needs persistence while PRX-PLAN-001
  forbids plan commands from mutating state. Resolved by an explicit
  `--record` flag on `plan replay` that writes only its own history file;
  every default plan command stays read-only and the no-mutation test keeps
  covering them.
- Coordination: `PRAXIS-PLAN-10` (another track) also changes planner inputs
  (context metrics, cost apportionment). This group adds observation sources
  and replay outputs only, and does not change `HistoricalExecution`,
  `SessionEvidence` or cost apportionment.
- Common tests: `PlanningTests` (domain), `PlanningCliTests` (CLI and
  no-mutation).
- Risks of solving each member independently: four separate observation or
  history mechanisms; this design keeps one observation model and one
  replay report.

## 4. Order

MISC-02 and MISC-03 (inputs), then MISC-04 (replay cost), then MISC-06
(persist replay error including cost), then MISC-05 (freshness comparison).

## 5. Verification pass

Recorded per member and criterion in [`group-verification.json`](group-verification.json).
