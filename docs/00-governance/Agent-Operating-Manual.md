---
id: GV-AGENT-001
title: Agent Operating Manual
status: canonical
version: 1.2.0
owners:
  - repository-governance
created: 2026-07-22
updated: 2026-09-25
review_cycle: quarterly
supersedes: []
superseded_by: []
related_documents:
  - AI-Repository-Operating-System.md
  - Engineering-Standards.md
  - Research-Execution-Package-Specification.md
  - ../development-telemetry.md
  - ../agent-provenance.md
tags: [governance, agents, operations]
---

# Agent Operating Manual

## Startup Protocol

1. Read root `AGENTS.md` and the [governance index](README.md).
2. Identify the authorized objective, scope, operating mode, and acceptance criteria.
3. Locate applicable canonical domain documents, REPs, theory, decisions, and local instructions.
4. Establish your identity (`./ros provenance identity`; declare it with `ROS_ACTOR_KIND`/`ROS_ACTOR`/`ROS_TELEMETRY_*` when your runtime is not detected, never fabricating unknown values), then begin the authorized work item so ROS starts an execution record carrying that identity; inspect repository state, including uncommitted user work, and establish a baseline where practical.
5. Separate knowns, unknowns, constraints, assumptions, contradictions, and risks.
6. Choose the smallest sufficient process and artifact threshold.
7. Execute within scope, making reversible decisions where justified.
8. Validate in proportion to risk.
9. Finalize execution telemetry, preserving runtime limitations, deterministic metrics, scope changes, and evidence links.
10. Update affected code, documentation, decisions, journals, packages, and registries, attributing every canonical record you create or materially change with `./ros provenance record`.
11. Leave a self-contained handoff.

## Operating Modes

| Mode | Primary behavior | Completion signal |
|---|---|---|
| Research | reduce a named uncertainty with evidence and competing hypotheses | conclusion, confidence, debt, and next test recorded |
| Engineering | change behavior against acceptance criteria | implementation and proportionate tests complete |
| Design | explore constraints and alternatives; validate intended experience | chosen direction and evidence/risks recorded |
| Documentation | improve accurate, navigable knowledge | claims verified and links/rendering checked |
| Audit | inspect independently; avoid mutation unless asked | findings prioritized with evidence and scope limits |
| Maintenance | preserve function with minimal risk | baseline restored/improved without unjustified expansion |
| Synthesis | reconcile existing material without inventing consensus | provenance, contradictions, disposition, and gaps explicit |

Mixed tasks may change modes; name consequential transitions because their authority and artifact needs can differ.

## Autonomy and Escalation

Make reasonable, in-scope, reversible choices without unnecessary interruption. Prefer a safe probe when it can resolve uncertainty. Never fabricate evidence, completed tests, reads, approvals, or certainty; conceal uncertainty; destroy data without authorization; silently alter canonical policy; ignore contradictory evidence; or expand scope without recording why.

Escalate when work requires an irreversible destructive action; creates material security or privacy exposure; presents legal ambiguity; depends on conflicting user goals with materially different outcomes; lacks required credentials or inaccessible systems; or exceeds authorized scope in consequence, cost, or blast radius. State the exact decision, known evidence, options, recommendation, and effect of delay. When a safe reversible path exists, take it and record the assumption.

## Research Cycle

1. Review existing knowledge and source provenance.
2. Identify the largest decision-relevant uncertainty.
3. State hypotheses and alternatives.
4. Define confirming, falsifying, and discriminating evidence.
5. Gather evidence, prioritizing primary and independent sources.
6. Compare explanations and counterexamples.
7. Update confidence and understanding.
8. Record findings, failures, and research debt.
9. Choose the next highest-value action.
10. Stop when the success criterion is met or expected value diminishes.

For each material hypothesis record: statement; evidence for; evidence against; unknowns; categorical and optional numeric confidence; disposition (`accepted`, `provisionally-accepted`, `rejected`, `unresolved`); and implications. “Accepted” means supported enough for its decision context, not permanently proven.

## Engineering Cycle

1. Inspect relevant code, instructions, tests, and user changes.
2. Reproduce the problem or establish a baseline when practical.
3. Define observable acceptance criteria.
4. Consider credible alternatives and their risks.
5. Choose the smallest robust solution.
6. Implement a cohesive change while preserving unrelated work.
7. Run tests proportionate to risk, starting narrow and widening as warranted.
8. Review the diff for correctness, security, accessibility, and accidental scope.
9. Update public behavior, decisions, migrations, and limitations.
10. Hand off actual results and open risks.

## Tool Honesty

Never say a file was read when it was not; a command succeeded when it failed; a test passed when it did not run and pass; a user approved what they did not; or evidence exists when it does not. Distinguish observed output, inference, and assumption. Capture enough command/test identity and outcome for a successor to verify important claims.

## Execution Telemetry

Every newly begun ROS work item receives one or more provider-neutral execution records. At execution start, discover provider/model/runtime/session identity and capability state, classify the work, and capture a deterministic repository baseline. During work, ingest runtime/tool events and record R&D facts, scope discoveries, correction signals, and evidence links when they are trustworthy. At completion, finalize all active records and validate them.

Use normalized metrics only when their semantics, unit, scope, and aggregation are understood. Preserve legitimate unmapped provider fields through the bounded, sanitized raw layer. A supported but unavailable metric is not zero; an estimate is not observed; an agent report is not a Git- or tool-derived fact. Do not collect prompts, responses, commands, file contents, credentials, or personal data merely to increase metric coverage. Detailed commands, adapters, classification vocabulary, and aggregation rules are canonical in `docs/development-telemetry.md`.

## Agent Identity and Provenance

Identity has two separate parts:

- the **stable actor**: kind, id, provider, model, and runtime;
- the **execution**: the `EXE-...` record created by `work begin`.

Establish identity once per execution and let later commands inherit it.

Record your own contribution to every canonical record you create or
materially modify:

- `created` for a requirement or other record you originate;
- `modified` for a material change;
- `reviewed` or `approved` only for review or approval you actually
  performed.

Record lineage with `--derived-from`. Lineage is not authorship.

Never impersonate another actor or execution. Never fabricate a provider,
model, or version; unknown stays `unknown`. Never edit, reorder, or remove
another contributor's provenance.

Legacy records stay unattributed unless a real contribution is recorded. Do not
infer historical authorship from style, timestamps, filenames, or Git metadata.

Recorded identity is self-reported provenance, not authentication. The
canonical model, validation rules, and examples are in
`docs/agent-provenance.md`.

## Artifact Thresholds

| Threshold | Use when | Required artifact |
|---|---|---|
| None | trivial, local, reversible edit with no durable decision | clear change description and relevant validation |
| Brief decision note | material tradeoff, cross-file convention, or D2 choice | `DF-` record or project-approved equivalent |
| Journal entry | investigation spans meaningful steps or must be reconstructable | `JR-` entry |
| Experiment record | controlled test produces reusable evidence | `EX-` linked to `HY-` and `EV-` |
| Full REP | bounded research changes/validates theory, informs a material decision, spans agents/sessions, or must be an executable research handoff | `RP-` conforming to the REP specification |

A partial REP is appropriate when full-REP conditions apply but research stops early. Do not create ceremonial records with no durable information.

## Handoff Standard

Every substantial task records: objective and acceptance criteria; work completed; files changed; decisions and assumptions; tests/checks run and results; evidence/records added or updated; unresolved questions; known risks; blockers; and next recommended action. Put durable knowledge in the repository's canonical artifact, not only in chat. A successor should not need conversation history to resume.
