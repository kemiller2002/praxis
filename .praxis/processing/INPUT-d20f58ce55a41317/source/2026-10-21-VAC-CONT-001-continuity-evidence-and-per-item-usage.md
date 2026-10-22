# VAC-CONT-001: Agent-continuity evidence for the grouped-execution review, and per-work-item usage when one session spans successive items

Priority: P2 (evidence input to an existing decision review) and P2 (measurement gap)
Owner system: Praxis (planning, execution telemetry)
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Existing work items and decisions:
- DF-ROS-2026-A053 decision 3 (grouped execution by default for high-affinity groups) and its review PRAXIS-PLAN-11.
  This input is **evidence for that review**. It does not ask to change the default.
- PRX-GRP-150..158 (context reuse and cost measured; a group's shared session is ingested once).
- REL-TOKEN-001 (per-request usage ledger) and REL-TOKEN-002 (final output tokens). Not duplicated here: VAC-001
  hit the same output-token lower bound and cites REL-TOKEN-002 as the fix.

Experimental finding: kemiller2002/cohort-based-engineering, branch `claude/verified-agent-continuity-vfjrrx`:
- `research/verified-agent-continuity/agent-longevity-analysis.md` §2–3
- `research/experiments/VAC-001/results/pooled-P1-R1.json`
- `research/token-efficiency/evidence-index.json` TE-H21, TE-H22

## Finding

VAC-001 ran 96 chained work units: 8 successive Ledger changes per chain, scored by a hidden oracle outside the
agent workspace. Model `claude-opus-5-5`, effort medium.

- **Cost of continuity.** One agent continued across the 8 changes (B) cost **40.9% less per independently
  accepted requirement** than a fresh agent per change with an agent-written handoff (A): CI −48.1% to −33.0%, 16
  paired blocks. The F1 run replicated it at −38.1%.
- **No reliability cost observed.** 0 failures in 72 long-lived units, against 0 in 24 fresh ones. Prompt size grew
  to 167k and there were 0 compactions.
- **Tokens hide the effect.** Tokens per accepted requirement differed by ≤ 10%. The cost difference comes from
  cache writes (2.7–3× in A) and requests per unit (≈10 vs ≈6.7).
- **Reconstruction is not cheaper.** Fresh agents rebuilding context from verified state (DR) cost +86% vs a
  continuous agent with the same controls (D).

These chains are **successive work items in one session that are not a Praxis group**. VAC-001 had to window usage
per item from transcript timestamps itself (`harness/vac.sh reusage`, `harness/ledger.py`). Praxis today ingests a
*group's* shared session once and apportions it; it has no per-item window for a session that continues across
ungrouped items.

## Requirements (proposed)

- **VAC-CONT-001-R1 (evidence).** Record VAC-001 as evidence for the DF-ROS-2026-A053 / PRAXIS-PLAN-11 review.
  Scope it explicitly: one fixture, 8-item chains, ceiling correctness, sub-agent cache condition K0.
- **VAC-CONT-001-R2 (telemetry).** When one agent session executes successive work items that are not a group,
  attribute usage to each item from the item's own begin and complete window. Record per item:
  - continuation index: the number of earlier items in the same session;
  - prompt size at the item's first and last request;
  - compactions inside the window;
  - cache-write and cache-read tokens.

  Never apportion across items; leave values that cannot be attributed `unavailable`.
- **VAC-CONT-001-R3 (planning, deferred).** Do not change restart or compaction policy on this evidence. No failure
  or compaction occurred, so no restart trigger is supported yet. Revisit after a longer-chain experiment
  (VAC R-EXP-2).

## Acceptance evidence

- A Praxis evidence record citing the VAC-001 files above and linked to DF-ROS-2026-A053.
- `praxis telemetry` output for a two-item continued session, showing per-item windows that add up to the
  session's usage, with tests.
- Canonical work items created by Praxis with provenance linking to this input. Do not use GitHub Issues.
