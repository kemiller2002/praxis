---
id: RQ-ROS-2026-A030
title: Reclassify a work item's type and accept external evidence references at completion
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-13
updated: 2026-10-13
research_area: repository-operating-system
priority: medium
related_documents:
  - RQ-ROS-2026-A022
  - docs/work-protocol.md
tags: [work-protocol, evidence, completion, work-type, audit]
provenance:
  contributions:
    EXE-20261013T162326019Z-fed260ae:
      operations: [created]
      at: 2026-10-13T16:23:59Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Program-lead-directed requirements capture: work-type reclassification and external evidence references (echelon-registry WI-0039/WI-0040 blocked)"
---

# Requirement

Praxis MUST let a work item's type be corrected after work has started, as an
auditable transition. It MUST also let completion cite evidence that lives
outside the repository, such as a merged pull request or a CI check run,
without pretending that evidence is a repository file.

Requirements only. The program lead directed this capture on 2026-10-13 and
will route the implementation separately.

New requirements mark their baseline: **(exists)** names behaviour already
implemented, **(new)** behaviour that does not exist yet, and **(changes)**
behaviour that exists and is required to change.

# Why

On 2026-10-13, echelon-registry captured requirements-only specifications as
work items WI-0039 (REG-SIG-001..008, echelon-registry#74) and WI-0040
(REG-RU-001..006, echelon-registry#75). Both were started with the default
type `task`, whose configured completion evidence is `implementation` and
`tests`. A requirements capture has neither, so both items remain `blocked`.

The repository can add a `requirements` type to
`workProtocol.completionEvidence`. The items still cannot reach it, for two
reasons.

1. **The type is fixed when work starts.** `work start --type` applies only
   when an item leaves `ready`. Running it again on an `active` item is
   refused with `cannot begin '<id>' from 'active'`, and no command changes
   the type later (exists). The only routes left are hand-editing `.ros`
   state, which is forbidden, or abandoning and recapturing the item, which
   records work as abandoned when it was not.
2. **Evidence must be a repository path.** `work complete --evidence
   TYPE=REFERENCE` requires the reference to exist as a file (`evidence path
   does not exist: <reference>`) (exists). The real evidence for a
   requirements capture is the merged pull request and the CI run that
   validated it. Neither is a file, so it can be recorded only by committing
   a stand-in file, which adds noise and is weaker evidence than the
   reference itself.

# Acceptance criteria

## Work-type reclassification

| ID | Required behavior |
| --- | --- |
| PRX-WTY-001 | (new) A command, for example `work reclassify --id ID --type TYPE --reason TEXT --occurred-at TIMESTAMP`, changes the type of an existing work item. It is allowed from `active` and `blocked`. It is refused from `ready` (use `work start --type`) and from terminal states (`complete`, `abandoned`), which are never rewritten. |
| PRX-WTY-002 | (new) `--reason` is required and non-blank. The target type must be configured in `workProtocol.completionEvidence`, or be `default`. An unknown type, a missing reason, or a type equal to the current one is refused with a message that names the problem. |
| PRX-WTY-003 | (new) Reclassification appends one immutable, idempotently identified event (for example `work.reclassified`) that carries the previous type, the new type, the reason, `occurredAt`, the actor resolved in the same way as other work events, and the active execution if there is one. Earlier events are never rewritten. |
| PRX-WTY-004 | (changes) Completion readiness uses the item's current type, so the evidence required at `work complete` follows the reclassification. `work context` and `status` show the current type and the reclassification history. |
| PRX-WTY-005 | (new) The lifecycle state, checkpoints, telemetry links and attribution are unchanged by reclassification. A `blocked` item stays `blocked` until a legal transition moves it. |
| PRX-WTY-006 | (new) `praxis validate` accepts a reclassified item whose events are consistent. It rejects a reclassification event whose previous type does not match the type recorded before it, or one that follows a terminal state. |

## External evidence references

| ID | Required behavior |
| --- | --- |
| PRX-XEV-001 | (changes) `work complete --evidence TYPE=REFERENCE` accepts an external reference as well as a repository path. Repository paths keep the current existence check unchanged. |
| PRX-XEV-002 | (new) An external reference is recognized and validated by form only. At minimum it is an `https` URL of a supported kind: a GitHub pull request (`https://github.com/OWNER/REPO/pull/N`), a GitHub Actions run (`…/actions/runs/N`) or check run (`…/runs/N`), or a commit (`…/commit/SHA`). Other schemes, malformed URLs, credentials or query strings in the URL, and unsupported hosts are refused with a message that names the rule. |
| PRX-XEV-003 | (new) Praxis records each piece of evidence with its kind (`path` or `external`). An external reference is recorded as external and is never reported as a file that was checked. Validation by form is not a claim that the PR was merged or that the run passed, and `work context` says so. |
| PRX-XEV-004 | (new) Completion makes no network call. Completion and `praxis validate` stay deterministic and offline. Any later online verification of an external reference (for example that a PR was merged at a SHA) is a separate, optional command that records its own observation. |
| PRX-XEV-005 | (new) A repository MAY declare, per work type in `completionEvidence`, which evidence types accept external references. If it declares nothing, the current path-only behaviour is kept for every type. |
| PRX-XEV-006 | (new) The external reference is preserved verbatim in the completion event and the work context. `praxis validate` re-checks its form, and rejects an external reference that a type does not permit. |

# Verification

Tests SHALL cover the following:

- Reclassification from `active` and from `blocked`.
- Refusal from `ready`, `complete` and `abandoned`, and of an unknown type, a
  blank reason and a no-op.
- Completion readiness switching to the new type's evidence.
- Event immutability, and a validator that rejects a forged or out-of-order
  reclassification event.
- External references: accepted forms, and refused forms (an `http` URL, a
  URL with a query string or embedded credentials, an unsupported host, an
  issue URL where a PR is required).
- Recorded kind, offline completion, and the path-only default when no type
  permits external references.

The echelon-registry items WI-0039 and WI-0040 are the first real cases. Once
the requirements here are implemented, they move from `blocked` to complete
as `requirements` items with their pull-request evidence.
