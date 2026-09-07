---
id: SDE-METHOD-004
title: Agent Execution Rules
status: draft
version: 0.1.0
created: 2026-09-02
updated: 2026-09-02
related_documents:
  - method/CONSTRUCTION-METHOD.md
tags: [method, agent-rules]
---

# Agent Execution Rules

Status: EXPERIMENTAL. Operationalizes the Construction Method specifically
for an autonomous coding agent, based on the one direct, controlled
comparison of two agent working strategies this research program has
produced.

## The central finding this document operationalizes

In Experiment 3 [EV-HN-2026-0005], the hardened architecture's own
structural mechanical coverage for the mission's required sites was higher
than baseline's (52.6% vs. 42.1%, architecture-potential). But the
*executing agent* did not experience that improvement, because it invested
upfront reading before editing and found several genuinely-guarded sites by
reading rather than by triggering the compiler. Its as-experienced MDR was
*lower* than the baseline agent's (31.6% vs. 42.1%), even while its search
operations (-63%) and repair loops (-50%) were both substantially lower.

> Mechanical hardening's payoff for AI agent workflows specifically may
> depend on the agent's own operating strategy in a way it does not for
> human developers, who typically cannot "read the whole diff space" as
> fast as an LLM-based agent can.

## Rules

**Edit incrementally, then compile — do not read the whole diff space
first, for a Semantic Change.** After making the smallest authoritative
change (per the Construction Method), compile before doing a broad,
exploratory search. Let the compiler's own errors name the next site,
rather than pre-deriving the full site list by reading ahead. This is
RECOMMENDED, not REQUIRED, because Experiment 3 is a single trial — see
`doctrine/EVIDENCE-TO-ENGINEERING-MAP.md`.

**Distinguish exploratory search from targeted search.** Exploratory search
("what does this codebase look like") is what dropped 63% between
conditions in Experiment 3. Targeted search (checking a specific tier the
mechanical checks are known not to cover, e.g. the API/HTTP tier) remains
necessary regardless of architecture and should not be skipped because
mechanical checks passed.

**Never treat a green compiler/architecture/contract-check run as proof of
behavioral correctness for a fan-out or dispatch arm.** Run the behavioral/
integration check from `method/VERIFICATION-METHOD.md` explicitly, every
time, for this defect class — it has a 100% miss rate across every
mechanical check tried in two independent experiments.

**Record self-classification honestly, including when it is unflattering.**
Experiment 3's two conditions used different self-classification strictness
for what counted as "found by the compiler" vs. "found by reading" — record
which one you used and why, rather than silently picking whichever
attribution looks better.

**Never report agent-self-reported token/cost/duration figures as
authoritative without labeling their source.** Experiment 3 found the
harness's own tool-spawn infrastructure held authoritative telemetry neither
agent could see from inside its own session, and that even harness
telemetry can be a documented partial figure (Condition B's resumed-only
count). Label every cost figure by its evidence class: self-report,
harness-telemetry (full run), or harness-telemetry (partial/resumed —
explicitly not a total). See `method/ENGINEERING-METRICS.md`.

**Stop searching once the applicable checks for the change's classification
pass**, per the Construction Method's stopping rule — do not keep searching
"just in case" once acceptance criteria and the classification's own
checklist are satisfied; this itself re-introduces the read-everything-first
pattern this document exists to caution against.

## What this document does not yet establish

It does not establish that these rules, followed together, produce a lower
total cost than an unconstrained agent strategy — that is exactly the
open question Experiment 3's own §35 "Recommended Next Experiment"
identifies (a same-mutation, same-architecture trial with the
investigation-strategy variable deliberately controlled). Treat these rules
as the current best operational guidance pending that trial, not as a
proven-optimal strategy.
