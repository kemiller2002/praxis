---
id: DF-ROS-2026-A074
title: Construction packages owe a mandatory host-observed scope check backed by the construction journal as evidence only
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-12
updated: 2026-10-12
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A029]
related_documents:
  - DF-ROS-2026-A067
  - DF-ROS-2026-A071
  - DF-ROS-2026-A072
  - docs/worker-checklists.md
  - docs/construction.md
tags: [agents, checklist, construction, ordo, evidence, provenance]
confidence: medium
derived_from: [DF-ROS-2026-A067, DF-ROS-2026-A071, DF-ROS-2026-A072]
provenance:
  contributions:
    EXE-20261012T223108671Z-34b4307a:
      operations: [created]
      at: 2026-10-12T22:55:31.705Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record mandatory construction-package scope verification (GH-234)"
---

# Context

GH-234 makes worker checklists independent, commit-bound acceptance gates
(`DF-ROS-2026-A067`). Praxis #242 and #243 then moved construction into the
host: `construct apply` commits Ordo's deterministic output, and
`construct extend` commits handwritten files into host-declared slots
(`DF-ROS-2026-A071`, `DF-ROS-2026-A072`). Both journal their effects in the
host construction root and stamp provenance trailers on the commits.

On 2026-10-12 the owner approved a mandatory scope check for construction
packages in the existing acceptance path (GH-234 owner comment). It must:

- be host-observed, never worker self-attestation;
- compare the journal evidence with independently pinned values;
- verify every applied operation against the actual candidate tree;
- treat the journal as evidence only, never as authority;
- fail closed on missing, stale, unverifiable or inconsistent evidence.

It must also leave every existing checklist requirement unchanged, and make
no OS containment claim.

# Decision

1. **A new checklist row, not a new framework.** `WorkerCheckKind` gains
   `ConstructionScope`. When the host identifies a construction package,
   `WorkerChecklist.generate` adds one row per member,
   `system/MEMBER/construction-scope`. The row is added to the build, tests,
   architecture, scope and acceptance rows, never instead of them. Its
   validator, `praxis.construction-scope/1`, is the host's own
   implementation, and `generate` refuses any other validator identity for
   the row. The validator and its input digest are framed into the checklist
   identity, like every other row. Intake, integration and completion
   already call `WorkerChecklistOperations.verify` and `recheckCandidate`, so
   they enforce the row without new code paths.
2. **Identification from protected facts only.** A package is a
   construction package when any one of these holds:
   - **(a)** The host construction journal holds an entry whose host-written
     commit names the packet's native group in a `Construction-Group` or
     `Extension-Group` trailer. An untrusted or unreadable journal counts,
     so this fails closed.
   - **(b)** A commit between the delegated source and the candidate carries
     a host construction or extension trailer.
   - **(c)** With construction enabled, the candidate changes a path under
     the host-placed `MOUNT/generated/` directory.

   The packet's group, source and candidate are bound by the signed
   delegation and observed from Git. No worker statement is consulted. Fact
   (a) does not depend on the candidate's trailers, and fact (c) does not
   depend on the journal, so omitting trailers cannot evade the row.
3. **The journal is evidence; authority is re-derived.** At check time the
   host:
   - reloads the protected ECIR policy;
   - re-admits the recorded plan through the same `ConstructionAdmission.admit`
     that `apply` uses: the current grant for the current members, the signed
     approval re-verified now, the committed ECIR bytes, and the pinned Ordo
     engine;
   - re-derives every operation.

   Journal entries, trailers and plan records are only compared with those
   re-derived values. The comparisons cover:
   - the cohort, requirement and decision sets, and the source commit,
     manifest and blueprint digests, against both the packet and the
     approval;
   - the plan digest, generator and Ordo versions, and the ownership root;
   - the route, which binds the slot policy;
   - the exact candidate commit.
4. **Host-recorded plans.** `apply` and `extend` record the admitted plan
   document write-once under `plans/HEX.json` in the construction root. That
   lets the check re-admit the plan without trusting a worker for it. The
   record is evidence too: re-admission recomputes its digest, and any other
   plan is refused as a plan-digest mismatch.
5. **The tree is read from Git objects.** The candidate's generated
   ownership root must hold exactly the re-derived artifacts, byte for byte.
   Every other change since the delegated source must be a slot write that a
   current, journalled extension recorded, within the slot's size limit.
   Every journalled operation is re-planned on its recorded base and
   compared with its journalled commit.
6. **Freshness.** Several rules keep the evidence current:
   - The candidate must be the exact commit of the newest journalled host
     entry on its chain.
   - Every applied entry for the group must be carried by the candidate.
     Otherwise it is stale: a newer or divergent construction exists.
   - Pending, foreign, partial or linked journal entries refuse.
   - The validator's input digest pins the configuration digest, the policy
     revision and a fingerprint of the whole construction root, so drift
     between observations refuses.
   - The verification itself is never cached across evaluations.
7. **Untrusted placement refuses.** The construction root must be absolute,
   outside the repository and unlinked. It must be accepted by the host
   file-access policy (mode `0700` under `unix-private`), and it must not be
   inside or around the checkout, spool or submission roots.

# Consequences

- After the host constructs for a native group, all of that group's
  packages are construction packages. A worker commit in such a package is
  refused as not host-applied. Further work goes through a new construction,
  an extension, or a new group.
- The chain must start at the delegated source commit. Integration must
  therefore fast-forward to the host-applied tip. When the target has moved,
  re-plan against the new head, as `construct` already requires.
- After a re-construction, extensions must be reapplied against the new
  construction. Slot files from extensions of an older plan are unexpected.
- A construction applied before this change has no plan record. Rerunning
  `construct apply` with the same plan records it and changes nothing else.
- `construct preview` and the read-only diagnostics are unchanged, and they
  work while acceptance refuses.
- **No containment claim.** This verifies the scope and provenance of
  host-applied writes. It does not establish OS containment or stop a
  process running as the host user from rewriting the host root. Containment
  is qualified separately (#231, #235).

# Alternatives considered

- **A parallel construction acceptance gate.** Rejected: it would duplicate
  the checklist engine, and the owner required the existing #234 path.
- **Trusting journal trailers or digests directly.** Rejected: the journal is
  writable evidence, not authority.
- **Identifying construction packages by trailers alone.** Rejected: a
  worker could omit them.
