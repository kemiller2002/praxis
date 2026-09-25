---
id: DF-ROS-2026-A036
title: Agent identity and provenance as a first-class, execution-keyed extension of existing ROS records
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-25
updated: 2026-09-25
research_area: repository-operating-system
decision_type: architecture
supports: []
related_documents:
  - RQ-ROS-2026-A001
  - RQ-ROS-2026-A002
  - RQ-ROS-2026-A003
  - RQ-ROS-2026-A004
  - RQ-ROS-2026-A005
  - RQ-ROS-2026-A006
  - RQ-ROS-2026-A007
  - RQ-ROS-2026-A008
  - RQ-ROS-2026-A009
  - RQ-ROS-2026-A010
  - RQ-ROS-2026-A011
  - RQ-ROS-2026-A012
  - DF-ROS-2026-A010
  - DF-ROS-2026-A035
  - docs/agent-provenance.md
supersedes: []
superseded_by: []
tags: [provenance, identity, agents, attribution, requirements, validation]
confidence: high
provenance:
  contributions:
    EXE-20260925T193942361Z-84dc9b22:
      operations: [created, modified]
      at: 2026-09-25T20:16:23.378Z
      last: 2026-09-25T20:29:55.830Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Architecture decision for agent identity and provenance"
derived_from: [RQ-ROS-2026-A001, RQ-ROS-2026-A004, RQ-ROS-2026-A007]
---

# Decision

Agent identity and provenance extend the three records ROS already had. There
is no parallel provenance system.

1. **Execution identity is the existing telemetry execution.** The
   `EXE-...` record created by `work begin` already discovered provider,
   model, runtime, session, and agent ID from a whitelisted environment
   (`DF-ROS-2026-A010`). It now also records `identity.actorKind`. That record
   is where an agent "registers" once per execution.
2. **Action provenance is the existing event log.**
   - Every work event carries a structured `actor`.
   - Newly captured backlog items carry `createdByActor`.
   - `./ros provenance record` appends `artifact.contributed` events.

   The adapter publishes events verbatim, so attribution crosses the
   integration boundary unchanged.
3. **Artifact provenance lives in the artifact.** Canonical Markdown
   artifacts carry `provenance.contributions`, a mapping keyed by execution
   ID. It travels with the file and is projected into registries.
   Derivation stays in the existing `derived_from` reference field, which
   keeps lineage distinct from authorship.
4. **Requirements become a canonical artifact kind** (`RQ-`,
   `research/requirements/`), with an optional registry, so requirement
   authorship can be validated.
5. **One portable actor object** is used everywhere: `kind`, `id`, and for
   non-humans `provider`, `model`, `runtime`, with `unknown` spelled out.
   Stable identity (the actor) and run identity (the execution) are
   different keys.

Validation is policy-driven (`ros.json` `provenance`) and severity-graded:

- errors fail validation;
- warnings do not;
- legacy observations are informational and appear only in
  `provenance audit`.

Legacy history is never rewritten or inferred.

# Why

- **Reuse, not a competing mechanism.** The execution record already held
  discovered identity, the event log already held immutable, idempotent,
  published semantic events, and front matter already held artifact metadata
  and lineage. Adding a separate provenance store would have duplicated all
  three and created a question of which one is authoritative.
- **Why key contributions by execution.**
  - Two runs of one agent can never collapse into one entry.
  - Everything from one run is traceable to it.
  - "One entry per execution" is a structural property.
  - It fits the existing front-matter reader, which supports nested
    mappings but not lists of mappings. Extending that reader would have
    changed how existing artifacts parse, and with it their registry bytes.
- **Why surgical edits.** Recording provenance never re-renders another
  contributor's entry, so fields a future attestation adds survive, and the
  history is append-only by construction, not by convention.
- **Why actor resolution comes from the current process only.** The existing
  fallback to the "last actor" stored in `.ros/context/current.json` would
  have attributed one agent's transition to another agent. That field is
  kept for compatibility but is explicitly not provenance.
- **Why a date-based policy.** Existing repositories keep validating; nothing
  is invented for their history; and new work is strict from a declared date.
  The greenfield and project-administration starters enable the policy.
- **Why provenance and not attestation.** Signed events and verified
  execution receipts need key management and a trust root, and belong behind
  the adapter boundary or in an attestation service. The model preserves room
  for them: open contribution entries, execution records as receipt anchors,
  and content digests on events.

# Alternatives rejected

- **Git commit authorship, trailers, or a `Co-Authored-By` convention.**
  Git-host specific, lost on squash or rebase, not per-artifact, and unable to
  separate stable identity from execution.
- **A single `author_agent` string**, which is what legacy artifacts use. It
  collapses kind, provider, model, and run into prose. It cannot accumulate,
  and it cannot be validated.
- **A separate `.ros/provenance/` ledger.** It duplicates the event log, and
  the provenance would not travel with the artifact.
- **A list of contribution mappings in front matter.** This needs a parser
  change that alters how existing artifacts parse.
- **Inferring historical authorship** from `author_agent`, style,
  timestamps, or Git. This fabricates certainty. Legacy fields are reported
  as "self-declared, unverified" instead.

# Consequences

- Event IDs now hash the `actor`. The Node internal library mirrors actor
  resolution byte for byte (`tests/provenance-actor-fsharp-differential.test.mjs`),
  and the golden masters were extended for the additive fields.
- **Composition root.** Following `DF-ROS-2026-A035`, the `provenance` command
  family lives in its own CLI module (`src/Ros.Cli/ProvenanceCommands.fs`).
  `Program.fs` gained routing only.
- **Bootstrap boundary.** This capability was built inside work item
  `FEAT-AGENT-PROVENANCE`, whose execution
  `EXE-20260925T193942361Z-84dc9b22` was created by the CLI *before* this
  change existed. That execution record therefore lacks `identity.actorKind`,
  and its `work.started` event lacks `actor`.
  - These are not back-filled.
  - The execution is projected as an agent from the discovery mechanism it
    preserved, `whitelisted-claude-environment`.
  - The requirements and this decision were attributed to it with the new
    `./ros provenance record` once the command existed, so this change dogfoods
    itself.
- **Modification detection.** An unattributed modification is detected two
  ways:
  - from Git: an artifact whose content changed since the base revision
    while its contributions did not;
  - from dates: an `updated` date later than the latest contribution.

  Every recorded operation updates the contribution's `last` timestamp, so
  repeated modifications within one execution remain detectable.
- **Adversarial review hardening.** An independent review before merge found
  six problems, each now fixed with a regression test:
  - an identity-less process, or a second session of the same agent, could
    inherit another run's execution;
  - CRLF could rewrite the document body;
  - a quoted `derived_from` value could be split;
  - a local-date versus UTC false positive;
  - reason and whitespace read-back failures;
  - Node/F# actor divergences on empty and explicit-`unknown` environment
    values.
- **Known limitations.**
  - Identity is self-reported.
  - The Node internal library (`tools/ros_cli.mjs`) and the legacy Python
    tooling do not know the `RQ` artifact kind. The F# CLI is the artifact
    authority (`DF-ROS-2026-A033`).
  - Git-based detection needs a Git checkout; without one, only the date
    rule applies.
  - A blocked or stale execution that is still active counts as a candidate
    for automatic selection. `--execution` resolves the ambiguity.

# Revisit when

- A signing or attestation authority is chosen for Echelon.
- An external work system begins consuming `artifact.contributed` events.
- Git-less repositories need finer modification detection than dates allow.
