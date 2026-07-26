---
id: RP-ROS-2026-A005
title: ROS executable contract and validation slice
research_area: ros-executable-contract
discipline: [research-systems, software-engineering]
author_agent: codex
version: 1.0.0
status: accepted
confidence: high
completion: complete
priority: high
created: 2026-07-24
updated: 2026-07-24
related_projects: []
related_documents: [JR-ROS-2026-A004, EV-ROS-2026-A003, DF-ROS-2026-A001, DF-ROS-2026-A002]
supersedes: []
superseded_by: []
tags: [ros, validation, registries, architecture]
keywords: [lifecycle, identifiers, deterministic-validation, generated-registries]
---

# Research State Snapshot

- **Theory Version:** Not established.
- **Knowledge Base Version:** ROS 1.0.0 scaffold plus this executable contract.
- **Highest Confidence Areas:** Required validator paths passed locally [EV-ROS-2026-A003].
- **Lowest Confidence Areas:** Cross-agent interpretation and accepted-content mutation enforcement.
- **Largest Remaining Unknown:** Whether a fresh agent can create and continue valid work without conversation.
- **Active Research Streams:** None registered.
- **Recently Invalidated Ideas:** Manual registries and sequential-only branch allocation.
- **Priority Changes:** Template alignment and cold-start testing are now highest priority.

# Executive Summary

Phase 1 and the required Phase 2 validation/registry slice are complete. Plain
text artifacts are canonical, registries are generated, and a dependency-free
CLI validates front matter, identity, references, lifecycle values,
supersession, filenames, and registry freshness. Six focused tests passed
[EV-ROS-2026-A003]. Confidence is high for this narrow scope, not for the full
ROS roadmap.

# Original Objective

Run `ROS-Structure-Analysis-and-Implementation-Roadmap.md` through its stated
stop condition: implement Phase 1 and the smallest useful Phase 2 slice.

## Success Criterion

The CLI passes a valid set; rejects malformed front matter, duplicate IDs, and
broken evidence references; rebuilds registries deterministically; documents
how the next agent creates valid work; and records remaining risk.

# Scope

## Included

Kernel policies, type schemas, deterministic validation, registry generation,
tests, CI configuration, entry-point documentation, decisions, migration, and
handoff.

## Excluded

Artifact creation commands, context manifests, intake, websites, model routing,
agent scheduling, and the roadmap’s later vertical-slice mission.

## Scope Changes

None. The Phase 0 commit/tag was intentionally not performed because the
scaffold is uncommitted user work and the assigned implementation starts at
Phase 1.

# Repository Context

Governance is canonical under `docs/00-governance/`. The untracked scaffold
introduced a second REP description and templates with incompatible metadata
shapes. The migration report records compatibility choices instead of silently
rewriting those sources.

# Current Understanding

The minimum executable ROS is a plain-text canonical record set plus
deterministic validation and rebuildable indexes. Lifecycle, confidence, and
completion are orthogonal. Full handoff reliability remains a hypothesis until
a cold-start agent test is run.

# Key Discoveries

- Manual dual-writing makes registry drift structural; generated registries
  remove that failure mode (`DF-ROS-2026-A001`).
- Sequential IDs are unsafe across independent Git branches; readable random
  tokens preserve parallel safety (`DF-ROS-2026-A002`).
- Existing governance and scaffold metadata require explicit compatibility
  aliases, documented in the migration report.
- The mandated validation paths pass locally [EV-ROS-2026-A003].

# Evidence Registry

| ID | Claim/Observation | Source and Method | Supports/Contradicts | Quality and Limits |
|---|---|---|---|---|
| EV-ROS-2026-A003 | Six validator and registry tests pass | Local unittest and CLI execution | Supports completion of the narrow slice | High; one runtime, no remote CI run |

# Hypothesis Registry

No standalone hypothesis record was required for this implementation slice.
The next mission should test whether the contracts are interpreted consistently
by a fresh agent.

# Failed Assumptions

- Empty manually maintained registries would be sufficient: rejected due to
  unavoidable dual-write drift.
- Four-digit sequential IDs were safe: rejected for parallel branches.
- “Immutable” was self-executing: rejected; it needs an acceptance boundary and
  eventually a trusted-baseline comparison.

# Open Questions

1. Will a fresh agent interpret the contracts consistently?
2. Should templates migrate to structured REP confidence/completion now or
   during artifact-creation implementation?
3. What trusted baseline should enforce accepted-content immutability?

# Recommended Next Research

Run a cold-start handoff test after aligning templates and implementing
`ros artifact new`. Stop and revise the kernel if the fresh agent cannot create
a valid linked artifact set without coaching.

# Research Backlog

1. Align templates with lifecycle and schema contracts.
2. Add collision-safe artifact creation.
3. Detect multi-record supersession cycles.
4. Enforce accepted-content digests against a trusted Git baseline.
5. Run the first vertical-slice mission and handoff test.

# Suggested Specialized Research Agents

None required. A fresh general-purpose agent is preferable for the handoff test
because prior repository context would bias the result.

# Parallel Research Opportunities

Template migration and trusted-baseline design can be researched independently,
but both depend on the accepted kernel decisions.

# Risks

- The YAML subset may reject valid but unsupported general YAML.
- Schema documents and direct CLI checks may drift until schema evaluation is
  integrated.
- Accepted-content immutability is policy-only in this slice.
- The GitHub workflow is configured but not remotely executed.

# Cross-Discipline Opportunities

Reproducible-build practices can inform registry freshness; event-sourcing
practices can inform amendments and supersession; usability testing can inform
the cold-start handoff experiment.

# Knowledge Relationships

`DF-ROS-2026-A001` establishes canonical/generated boundaries.
`DF-ROS-2026-A002` establishes identity and acceptance.
`EV-ROS-2026-A003` validates the implementation.
`JR-ROS-2026-A004` preserves the execution sequence.

# Theory Impact Assessment

## Affected theory records

None; no theory registry existed.

## Affected engineering principles

The implementation operationalizes deterministic enforcement, plain-text
canonical sources, reversible evolution, and proportionate tests.

## New principle candidates

Generated indexes should be byte-deterministic and replaceable.

## Deprecated principles

Manual registry synchronization in the scaffold bootstrap.

## Confidence changes

Confidence in the executable validation slice rose from low to high after the
completion tests [EV-ROS-2026-A003].

## Predictions created

A fresh agent can identify the contracts and create the next valid artifact
after template alignment.

## Predictions invalidated

None tested beyond the implementation mechanics.

## Required theory-registry updates

None until the cold-start test produces evidence.

# Research Quality Metrics

- **Primary sources:** 10 repository governance/scaffold files inspected.
- **Independent sources:** 0; this was repository-internal engineering.
- **Counterexamples reviewed:** 5 invalid/stale fixture paths.
- **Competing viewpoints reviewed:** 3 registry strategies and 3 identifier strategies.
- **Hypotheses tested:** 6 executable behavior tests.
- **Failed hypotheses:** 2 architecture assumptions rejected.
- **Research completeness:** 100% of the assigned stop-condition behaviors.
- **Confidence gain:** Qualitative low to high for the narrow slice.
- **Open questions reduced:** 6 of 10 roadmap challenge questions resolved in policy; 4 remain implementation or experiment questions.

# Research Debt

- **Missing evidence:** Remote CI and cross-agent behavior.
- **Missing experiments:** Cold-start handoff and accepted-content mutation.
- **Missing disciplines:** Security review is low priority for the local,
  dependency-free reader but remains appropriate before untrusted intake.
- **Weak areas:** General YAML compatibility and schema/validator unification.
- **Replication needed:** Run tests on CI and another supported Python version.
- **Tool limitations:** No artifact allocator or full JSON Schema evaluator.
- **Assumptions awaiting evidence:** A four-character token is sufficient at
  current scale; a fresh agent can follow the contracts.

# Repository Updates

Added kernel standards/protocols, two Decision Records, seven type schemas, the
CLI, six tests, a CI workflow, generated registries, migration report, evidence,
journal, and this REP. Updated README, bootstrap, context risks/decisions, and
the legacy naming standard.

# Website Updates

Not applicable; website work is outside the stop condition.

# AI Consumption Notes

Treat artifact Markdown as canonical and registry JSON as generated. Use
`accepted` as the immutability boundary, with REP `canonical` as a compatibility
alias. Do not infer full YAML support or automatic mutation protection.

# Handoff Instructions

From the repository root run:

```bash
python3 -m unittest discover -s tests -v
./ros registry build
./ros validate
```

Before creating records, read `BOOTSTRAP.md`, the lifecycle and supersession
protocols, and the identifier/confidence/tier standards. The next cohesive
change is template alignment plus `ros artifact new`; then run a cold-start
handoff experiment.

# Research Journal

`JR-ROS-2026-A004` records baseline, decisions, implementation, falsification,
and the next action.

# Appendix

The detailed compatibility and rollback record is
`docs/migrations/ROS-1.0.0-EXECUTABLE-CONTRACT.md`.

# Completion Checklist

- [x] Required metadata is complete.
- [x] Important claims reference evidence and decisions.
- [x] Competing explanations were considered.
- [x] Failed assumptions were recorded.
- [x] Theory impacts were assessed.
- [x] Research debt was recorded.
- [x] Registries were generated.
- [x] The next agent has commands, paths, limitations, and an exact next step.
