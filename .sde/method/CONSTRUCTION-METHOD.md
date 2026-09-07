---
id: SDE-METHOD-001
title: State-Directed Engineering — Software Construction Method v0.1
status: draft
version: 0.1.0
created: 2026-09-02
updated: 2026-09-02
related_documents:
  - method/CHANGE-CLASSIFICATION.md
  - method/VERIFICATION-METHOD.md
  - method/AGENT-EXECUTION-RULES.md
tags: [method, construction-method]
---

# State-Directed Engineering — Software Construction Method v0.1

**Status: Provisional / Engineering Validation.** This is a scaffold from
three HelixNote controlled experiments, not a finalized method. It has not
been validated as a repeatable practice on any application other than
HelixNote. Do not present it to an engineering agent as settled doctrine —
present it as the current best-known ordering, subject to revision once
`method/FIRST-VALIDATION-DESIGN.md` runs.

## The workflow

```
REQUIREMENT
    |
    v
CLASSIFY CHANGE                  -> method/CHANGE-CLASSIFICATION.md
    |
    v
IDENTIFY AUTHORITY                (which tier owns this fact? Tier 1 for
    |                              semantic decisions, see doctrine/FOUR-TIER-ARCHITECTURE.md)
    v
MAKE SMALLEST AUTHORITATIVE CHANGE
    |
    v
COMPILE
    |
    v
REPAIR COMPILER-DIRECTED OBLIGATIONS
    |
    v
RUN ARCHITECTURE CHECKS
    |
    v
RUN BOUNDARY / CONTRACT CHECKS
    |
    v
RUN FOCUSED BEHAVIOR TESTS
    |
    v
TARGETED SEARCH FOR UNGUARDED REPRESENTATIONS
    |
    v
INTEGRATION VERIFICATION
    |
    v
LIVE EFFECT / PERSISTENCE PROOF IF WARRANTED
    |
    v
ACCEPTANCE CRITERIA
    |
    v
STOP
```

**The ordering is deliberate and evidence-grounded; do not casually reorder
it.** Any reordering must be recorded with rationale (a Decision Record if
it is a repository-wide change, otherwise a note in the work item).

## Why this order (evidence trail)

- **Compile before broad search** is placed early because Experiment 3
  [EV-HN-2026-0005] showed an agent that reads broadly *before* compiling
  under-realizes a hardened architecture's mechanical-discovery benefit —
  three genuinely-guarded sites in Condition B were found by reading rather
  than by the compiler that would have caught them, which is exactly why
  they are not credited to the architecture's benefit in the
  as-experienced MDR figure. See `TH-SDE-2026-0003` and
  `method/AGENT-EXECUTION-RULES.md`.
- **Architecture checks come immediately after compiler repair**, not
  instead of it, because compiler exhaustiveness and architecture-isolation
  checks catch different obligation classes (see
  `method/VERIFICATION-METHOD.md`'s enforcement map).
- **Boundary/contract checks precede behavior tests** because they are
  cheaper and catch a different failure class (representation collapse,
  uncoordinated duplication) than behavioral defects do — no reason to pay
  for a slow integration run before a fast contract check has passed.
- **Targeted search for unguarded representations comes after the
  mechanical checks, not before them**, specifically to avoid the
  read-everything-first pattern Experiment 3 showed reduces realized
  architectural benefit. "Targeted" means: search for the tiers and sites
  the mechanical checks do NOT cover (e.g., the API/HTTP tier in
  HelixNote's own architecture, per `EV-HN-2026-0005` §26), not a repeat of
  exhaustive upfront reading.
- **Behavioral/integration verification and live effect proof are placed
  last, and are REQUIRED, not optional**, because every HelixNote trial so
  far found a present-but-inert boundary defect that evaded every earlier
  check identically (see `doctrine/CONTRADICTIONS-AND-DEPRECATED.md`,
  "Compiler/architecture/contract-check protection proves behavioral
  correctness").

## Fast Path vs. Full Path

Not every change needs every step. See `method/CHANGE-CLASSIFICATION.md`
for the four change classes; this table is provisional pending engineering
trials:

```
Presentation Change
    -> local change -> focused verification -> done

Mechanical Propagation
    -> mechanically directed repair -> focused verification -> done

Boundary Change
    -> explicit boundary decision -> contract verification
    -> host/integration verification -> done

Semantic Change
    -> semantic modeling -> authoritative change -> compile
    -> architecture checks -> boundary checks -> behavior tests
    -> targeted search -> integration/live verification -> done
```

## Stopping rule

Stop when: acceptance criteria are met; the applicable checks for the
change's classification have run and passed (or their failures/skips are
explained, per `docs/00-governance/Engineering-Standards.md`'s Definition
of Done); and no known high-severity regression remains. Do not continue
searching once these conditions hold merely because more could theoretically
be found — see `method/ENGINEERING-METRICS.md` for what "diminishing value"
looks like in practice.

## What this method does not yet claim

It does not yet claim this exact sequence, as a whole, has been validated
end-to-end as reducing engineering cost — only that each transition in it is
individually evidence-grounded (see the citations above). The sequence
itself is EXPERIMENTAL per `doctrine/EVIDENCE-TO-ENGINEERING-MAP.md`.
