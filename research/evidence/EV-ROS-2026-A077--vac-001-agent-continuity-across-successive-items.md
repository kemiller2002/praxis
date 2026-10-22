---
id: EV-ROS-2026-A077
title: VAC-001 — one agent continued across 8 successive work items cost about 40% less per accepted requirement than a fresh agent per item, with no observed reliability cost (one fixture, ceiling correctness, cache condition K0)
status: review
version: 1.0.0
owners:
  - repository-governance
created: 2026-10-22
updated: 2026-10-22
research_area: repository-operating-system
evidence_type: secondary
supports: []
contradicts: []
related_documents:
  - DF-ROS-2026-A053
  - EV-ROS-2026-A064
  - EV-ROS-2026-A070
  - EV-ROS-2026-A075
  - requirements/PLANNING-WORK-GROUPS.md
tags: [planning, grouping, continuity, agents, cost, cache, experiment, vac-001]
confidence: low
provenance:
  contributions:
    EXE-20261022T201717327Z-94a467d8:
      operations: [created]
      at: 2026-10-22T20:18:09.840Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "VAC-CONT-001-R1: record VAC-001 as scoped evidence for the DF-ROS-2026-A053 / PRAXIS-PLAN-11 review"
derived_from: [INPUT-d20f58ce55a41317]
---

# Evidence

## Scope (read this first)

This record is **evidence for the `DF-ROS-2026-A053` decision-3 review
(`PRAXIS-PLAN-11`)**. It does not change the grouped-execution default, the
review's triggers, or any restart or compaction policy (`VAC-CONT-001-R3`).

It is narrow by construction:

- **One fixture.** A single task family (successive "Ledger" changes V1–V8)
  in one repository.
- **8-item chains.** Every chain is 8 successive work items; no chain is
  longer, and every chain is censored at 8.
- **Ceiling correctness.** Every arm accepted every unit (96/96 final and
  first pass). Reliability differences, if any exist, are not visible here.
- **Sub-agent cache condition K0.** The agents ran as sub-agents sharing a
  prompt-cache prefix with the orchestrator. Isolated processes (K1) could
  change the cache-write share and the size of the gap.
- **Output tokens are a lower bound**, as in `REL-TOKEN-002`; cost is a lower
  bound computed from price table `2026-10-20.1`.

The chains are successive work items executed in one agent session **that
are not a Praxis group**. They are therefore adjacent to, not an instance
of, `DF-ROS-2026-A053` decision 3: they bear on its premise (context reuse
lowers cost) rather than on grouped execution itself.

## Sources

Repository `kemiller2002/cohort-based-engineering`, branch `main` at
`836d0bb33fc552dd15c9b1365e267384dcb428d8` (preregistered on
`claude/verified-agent-continuity-vfjrrx` at `05b3cf2`). SHA-256 of the files
read when this record was written:

| File | SHA-256 |
|---|---|
| `research/verified-agent-continuity/agent-longevity-analysis.md` (§2–3) | `88ca36891c7c8b4814f3897c097896fe805dfbba3f8c2dec8e5738cd4298a548` |
| `research/experiments/VAC-001/results/pooled-P1-R1.json` | `f40d7ba1ec90214bab929817d68635baf0d2785b903ab9f02482a5eacd009696` |
| `research/token-efficiency/evidence-index.json` (`TE-H21`, `TE-H22`) | `48c5b110cc7113bd3dfe5e6c5de4ceda4f59bd62a099eac41a7f09ad75eb6fcc` |

The numbers below are copied from those files; nothing was recomputed here.
Every request in the experiment was served by one model at effort `medium`
(the source records the model).

## Design (as recorded by the source)

96 agent units across runs P1, R1 (no faults) and F1 (5 injected faults),
scored by a hidden oracle outside the agent workspace. Arms:

- **A**: a fresh sub-agent per unit, handed the previous agent's report.
- **B**: one sub-agent continued across the 8 units.
- **C**, **D**: B with Echelon controls added (D: Ordo, Praxis work protocol,
  host gate, verified state).
- **DR** (F1 only): D's controls and verified state, with a fresh sub-agent
  per unit (context reconstructed from verified state).

Blocks are (run, position); contrasts are percentile bootstraps, 10,000
resamples, seed `20261021`.

## Results

### Cost of continuity (analysis §2; `pooled-P1-R1.json` `contrasts`; `TE-H21`)

| Contrast | Blocks | Cost per accepted unit (lower bound) | 95% CI |
|---|---|---|---|
| B vs A (P1+R1) | 16 | −40.9% (`point` −0.4086) | −48.1% to −33.0% |
| B vs A (F1, replication) | 8 | −38.1% | −47.1% to −26.1% |
| D vs A (P1+R1) | 16 | −31.0% | −38.2% to −23.3% |
| D vs B (P1+R1) | 16 | +16.8% | +6.4% to +29.6% |
| DR vs A (F1) | 8 | +50.6% | +36.8% to +62.0% |
| D vs DR (F1) | 8 | −46.2% | −56.6% to −32.4% |

With the visible-output cost estimate instead of the lower bound, B vs A is
−36.6% (−42.4% to −30.3%).

### Tokens hide the effect (analysis §2.3; `TE-H21`)

- Total tokens over 2 chains × 8 units: A 10,533,719, B 9,776,823 (−7.2%).
  Tokens per accepted unit differ by at most about 10% (P1: 672k vs 605k;
  R1: 645k vs 617k).
- The cost difference is cache economics: A writes 2.7–3× more cache per
  chain and needs about 10 instead of about 6.7 model requests per unit,
  because each fresh agent rediscovers the repository.
- A token-count metric alone would have hidden most of the difference.

### Reconstruction is not cheaper (`TE-H22`)

Fresh agents rebuilding context from verified state (DR) cost +86% against a
continuous agent with the same controls (D): $3.37 against $1.81 (lower
bound), both 8/8 accepted and 0/5 faults accepted. Total tokens +22.5%.
n = 1 chain per arm. This agrees in direction with `EV-ROS-2026-A075`
(structured handoffs cost more than continuous context).

### No reliability cost observed (analysis §3)

- 0 critical failures, 0 regressions, 0 false completions, 0 architecture
  violations, 0 decision reversals in 96 units; 0 failures in 72 long-lived
  units and 0 in 24 fresh ones.
- All chains censored at 8 units. Upper 95% bound on the per-unit
  critical-failure probability: 3.1% pooled, 11.7% for A, B or D (24 units
  each).
- Prompt size at the last request grew from 57–74k (unit 1) to 131–167k
  (unit 8) in long-lived arms. **0 compactions** in 96 units: compaction was
  never reached, so its effect is unmeasured.
- H2 (failure depends on agent lifetime) is **untested** at this chain length:
  the preregistered failure model is not estimable with zero events.

## Interpretation for the `PRAXIS-PLAN-11` review

- Supports, in this fixture, the premise behind decision 3: keeping context
  across related successive items is a large cost lever (about 40%), and its
  mechanism is cache writes and requests per item, not total tokens. Reviews
  that compare grouped and independent execution should therefore read cost
  and cache-write tokens, not token totals alone.
- Says nothing about grouped execution's per-criterion fidelity, which is the
  risk decision 3 gates: correctness was at ceiling.
- Does not support any restart or compaction trigger (no failure, no
  compaction). That question is deferred to the longer-chain experiment VAC
  R-EXP-2 (`VAC-CONT-001-R3`).
- VAC-001 had to window usage per item from transcript timestamps outside
  Praxis. Praxis attributes a group's shared session but had no per-item
  window for a session that continues across ungrouped items; that gap is
  `RQ-ROS-2026-A034` (`VAC-CONT-001-R2`).

## Limitations

The source's own limitations apply unchanged: one fixture and task family; two
no-fault chains per arm (one for DR); bootstrap intervals capture task-level,
not chain-to-chain, variance; K0 shared prompt cache; arms ran concurrently
(no wall-clock comparison); output tokens a lower bound and hidden thinking
unobservable; arm A agents moved files outside the workspace (scope scan
hits), with no oracle contamination. The orchestrator's own cost is
unavailable.
