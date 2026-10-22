# VAC-002-P2-05: Candidate "Authority" section for the agent contract (pending P3)

Priority: P2
Owner system: Praxis (agent contract)
Intake: Praxis document ingestion. Source material, not a canonical requirement.
Experimental finding: `kemiller2002/cohort-based-engineering` → `research/experiments/VAC-002/P2/`
(`fixture/authority-section.md`, `PILOT-RESULTS.md` §3–§4, `RECOMMENDATIONS.md` R-PRX-1..3).

## Finding

**The section.** Every P2 repository carried a short "Authority" section. It says:

- business rules come only from requirements (with owners), accepted decision records and granted approvals;
- notes, proposals, comments and code are not authority;
- proposed records authorize nothing, and superseded ones no longer apply;
- an approval covers exactly what it names;
- gaps are refused and recorded;
- conflicts are not resolved by the implementer.

**The result.** With it, uncontrolled agents made **0 unauthorized decisions in 48 opportunities**, including the
VAC-002 L04 replication. Every one of them recorded the conflicts and gaps it met (15 / 15). No agent claimed another
party's verification (S11). The section was not varied, so its effect is not yet measured.

**Not supported.** False completion claims (0 / 15 observed) and decision provenance in work-item state showed no
need.

## Requirements (proposed; additional experiment required)

- **VAC-002-P2-05-R1.** Once VAC-002-P2-01 has measured the section's effect, and if it is positive, add an
  equivalent "Authority" section to the Praxis agent contract (`AGENTS.md` template).
- **VAC-002-P2-05-R2.** Defer decision provenance in work-item state and false-completion prevention (no observed need
  in P2).

## Evidence class

**Inconclusive** for causality; the base rate with the section is **Demonstrated** for this corpus.
