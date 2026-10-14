# EX-ROS-2026-A024 architectural-coherence rubric (frozen)

Copied without change from the preregistered protocol
(`EX-ROS-2026-A024--context-continuity-and-externalized-handoff.md`,
"Outcomes / 2. Architectural coherence"). Applied by a blinded evaluator to
each anonymized snapshot.

Score each snapshot 0, 1 or 2 on each of five dimensions:

1. shared persistence/state model;
2. shared admission/validation rules;
3. shared domain classification and invariants;
4. error/JSON/CLI behavior consistency;
5. shared mutation/checkpoint architecture and reuse.

Levels:

- 0 = fragmented or contradictory
- 1 = mixed/partially unified
- 2 = one coherent reusable model

Composite = sum of the five dimension scores, range 0-10.

Rules:

- The scope is the code and tests that implement `PRAXIS-GROUP-01` to
  `PRAXIS-GROUP-05` (the `work group create | show | add | remove | checkpoint`
  feature) and the existing code it reuses or duplicates.
- Every dimension score cites concrete evidence: `LABEL/tree/path:line`
  references, hunks of `LABEL/diff.patch`, or a command the evaluator ran with
  its output. A score without evidence is invalid.
- The same checks are applied to every snapshot (symmetry). When a property is
  looked for in one snapshot it is looked for in all three.
- The evaluator does not know, and must not try to infer, which experimental
  condition produced which snapshot.
- Acceptance correctness is scored separately (acceptance-criteria.json) and is
  not part of this composite. No combined score is produced.
