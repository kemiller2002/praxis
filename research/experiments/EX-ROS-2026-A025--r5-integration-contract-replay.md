---
id: EX-ROS-2026-A025
title: "Retrospective replay of Rehearsal 5's merges against a reconstructed integration contract"
research_area: repository-operating-system
status: completed
created: 2026-10-28
tests_hypotheses: []
inputs: []
outputs: []
related_theories: []
related_documents:
  - RQ-ROS-2026-A036
  - docs/work-protocol.md
  - schemas/praxis-integration-contract.schema.json
  - research/experiments/EX-ROS-2026-A025-r5-integration-replay/README.txt
tags: [integration, workstreams, ownership, merge-conflicts, replay, retrospective, non-causal]
derived_from: [INPUT-28bdc22a92aeab92]
provenance:
  contributions:
    EXE-20261028T005916741Z-aae5a0a4:
      operations: [created]
      at: 2026-10-28T01:28:45.000Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Retrospective, non-causal replay of Rehearsal 5's 55 merges against a reconstructed integration contract (EII-B R4)"
      evidence: [research/experiments/EX-ROS-2026-A025-r5-integration-replay/outputs/summary.primary.json]
---

# Experiment

## Question

Rehearsal 5 (`kemiller2002/indy-init-rehearsal-005`) ran five parallel
streams with no machine-checked ownership. What would `praxis streams check`
(RQ-ROS-2026-A036) have reported before each of its merges, given an
ownership contract reconstructed from the rehearsal's own documents?

**This is a retrospective, descriptive replay. It is not causal.** It
cannot say whether agents would have acted on the reports, or whether any
conflict, violation or reconciliation would have been avoided.

## Data

- R5 `main` up to the cutoff `e086147bc93142c1d97917f2f2fd3a3d08f66dfc`
  (2026-10-28T00:02:07Z), the same cutoff as the R5 baseline report:
  193 commits, 55 merges, 19 `Praxis: reconcile` commits. The stream
  branches `r5/{core,shell,persist,acc,ux}` were fetched read-only. The
  replay ran on a local copy whose remote URL had been disabled. Nothing
  was written to the R5 repository.
- Seven merges conflict when replayed with `git merge-tree --write-tree`
  (`inputs/merges.tsv`). This matches the baseline report: `7abed91`,
  `826b01a`, `85c9d68`, `6b1239e`, `80a0476`, `3858417`, `d23060f`.
- R5 recorded no `work.completion.verified` event at the cutoff (0 in
  `.ros/events/events.jsonl`).

## How the contract was reconstructed

The contract was reconstructed **after the fact**, on 2026-10-28, by the
agent that implemented the check. The author already knew which files had
conflicted (from the baseline report). This is the main source of bias
(see "Biases"). The sources are R5 documents at the cutoff commit only:

| Contract element | Source |
|---|---|
| Streams and work items: core WI-0003, shell WI-0004, persist WI-0005, acc WI-0006 | `.ros/work/queue.json` titles. ux (WI-0007) is left out: none of its commits reached `main` by the cutoff, and no merge involves it. |
| core owns `src/engine/**`, `tests/App.Engine.Tests/**`, `tests/Engine.WasmSmoke/**`, `docs/architecture/**` | `docs/architecture/BOUNDARIES.md`: boundaries A-E are owned by core, and the interface manifest and invariants belong to the kernel stream. WI-0003 title: "kernel, invariants, interface freeze, fixture replay". |
| shell owns `src/app/**`, `src/kernel/**`, `src/wasm/**`, `tests/Causa.App.Tests/**`, `tests/browser/**`, `.percepta/**`, `limen.config.json`, `playwright.config.js`, `.echelon/routes.json`, `**/Routes.fs`, `tests/**/RouteTests.fs` | WI-0004 title: "Limen host, WASM app shell, UI, Percepta". `docs/KICKOFF-EXECUTION-PACKET.md` §4 UI agent: Limen boundary, screen contracts, URL routes and `.echelon/routes.json`. |
| persist owns `src/persistence/**`, `tests/Persistence.Tests/**`, `src/app/PersistencePort.fs` | WI-0005 title. BOUNDARIES.md Boundary D: "persistence owns the provider port and adapters". |
| acc owns `acceptance/**`, `tools/acceptance/**`, `evidence/acceptance/**` | WI-0006 title: "stack lock gate and acceptance pipeline". |
| No stream owns CI, the solution, packages or vendor files | `RQ-CAUSA-2026-0318` O4 assigns "dependency lock, contract tests, CI" to the integration owner, and O5 assigns "interface change control" to the coordinator. Neither is a stream. |
| Interface `kernel-interface-manifest` (owner core): the manifest and BOUNDARIES.md | BOUNDARIES.md: "A breaking change needs a coordinator decision and a new manifest version". No approval is recorded. |
| Interface `shell-files` (owner shell): the 13 shell-area files named by the five "coordinator-approved" commits | `257e0d8`, `9af1125`, `ca848e9`, `819ab4f`, `04e03f2`. Each commit becomes one recorded approval for its stream, covering the files it changed (`inputs/approvals.json`). `src/app/PersistencePort.fs` is left out because persist owns it. |
| `managedPaths`: `.ros/**`, `.praxis/**` | Praxis state, merged by Praxis tooling. |

The time order is handled as follows:

- An approval counts for a merge only if its commit is in the history of
  one of the merge's parents, that is, if it had been recorded before the
  merge.
- Ownership is the cutoff version for every merge. BOUNDARIES.md first
  appeared at `89dc985` (20:41Z), after the first 9 merges.

## Method

`derive-merges.sh` assigns the stream side of each merge from its subject,
using the rules stated in that script. Every stream-side parent is
confirmed to be an ancestor of the stream's branch. For each merge,
`replay.sh` runs:

```
praxis streams check --contract <merge contract> --stream S --against <other parent> --json
```

The stream's ref is the stream-side parent. The check therefore sees
exactly the two sides Git merged, before the merge existed. The metrics
were defined before the outputs were inspected; they are in `analyze.sh`.

- **Overlap flag:** unmanaged paths both sides changed since their merge
  base. This is the overlap of `praxis sync check`, applied to the stream.
- **Ownership flag:** at least one finding. The preflight merge is excluded
  because it is not a stream merge.
- **Narrow flag:** overlap on a path the stream side does not plainly own.
- **False positive:** a flag on a merge whose parents merge cleanly.

`reconcile.sh` replays each commit that a reconcile event attributed. It
sets every stream's ref to that commit and runs against the commit's first
parent. A commit counts as attributable to a stream when that stream has no
finding and owns, is the interface owner of, or is approved for at least
one of the commit's paths. A reconcile commit counts as
"auto-attributable" when every commit it reconciled is attributable to
exactly the stream bound to its work item.

A sensitivity variant, `s1`, adds `commonPaths` for documentation and
records (`docs/**`, `research/**`, `evidence/**`, root `*.md`). No R5
document states this. Its results are identical except for two fewer
findings, so only the primary variant is tabulated.

## Results (primary contract)

| Measure | Existing R5 workflow | Replayed contract check |
|---|---|---|
| Merges | 55 | 55 replayed (54 stream merges, 1 preflight) |
| Conflicting merges | 7, found while merging; nothing checked before | **7 of 7 flagged before merging** by the overlap rule; every conflicting file was named |
| False positives, overlap rule | n/a | **7 of the 48 clean merges** were flagged (14 flagged in total; 7 conflicted) |
| Narrow overlap rule | n/a | 5 of 7 conflicts flagged; 4 of 48 clean merges flagged |
| Ownership findings | none recorded; the rule existed only as a commit-message convention | **164 findings on 41 merges** (7 conflicting, 34 clean): 155 `outside-ownership` and 9 `interface-unapproved`. As 59 distinct (stream, path) pairs: 51 outside-ownership and 8 interface-unapproved |
| Reconcile commits | 19 | **3 of 19** wholly auto-attributable (`6463620`, `4b2d277`, `af698c9`). Of 97 reconciled stream commits, 43 are uniquely attributable to the reconciled stream, 6 are ambiguous (also another stream's), 8 match another stream only, and 40 match no stream. 3 more are WI-0001 preflight commits, which are not stream commits. |
| Merge readiness | not evaluated | 0 of 54 stream merges ready. 42 are blocked by a finding or an overlap. 12 are unverified only because R5 recorded no `work.completion.verified` evidence. |

Per-merge rows are in `outputs/merges.primary.tsv`. Per-commit attribution
is in `outputs/reconcile.primary.jsonl`.

### Reading the numbers

- **Recall is close to guaranteed by construction.** A textual Git conflict
  requires both sides to change the same path, so path overlap flags every
  ordinary conflict. 7 of 7 therefore shows that the check runs before the
  merge from Git facts alone. It is not evidence of predictive skill. The
  informative figure is precision: 7 of the 14 overlap flags conflicted.
- **The ownership rule does not discriminate conflicts:** 34 of the 48 clean
  merges carry findings. Most distinct findings are on paths that R5's
  documents assign to no stream: `.github/workflows/**`, `App.slnx`,
  `package.json`, `scripts/**` and `vendor/**`. That reflects a contract
  gap the rehearsal itself had, and is not noise introduced by the check.
  The rest are cross-stream edits of shell files that predate the
  "coordinator-approved" convention, or that it never covered.
- **Cross-stream merges inflate findings.** `r5/core` merged `r5/shell`
  (`e94e828`), so core's side then carried shell's commits. For example,
  `331f76c` has 27 findings and 24 overlapping paths.
- **8 mismatches** are persist and core commits that changed only shell-area
  files, for example persist's browser tests under `tests/browser/**`. The
  post-hoc reconciliation attributed them to the authoring stream. The
  reconstructed contract says the path owner is shell.

## Biases and limits

1. **Hindsight.** The contract was written after the rehearsal by an author
   who knew where the conflicts were. The `shell-files` interface exists
   only because cross-edits happened. Both make the interface rule
   partially circular.
2. **Anachronism.** Ownership is the cutoff version for every merge.
   BOUNDARIES.md did not exist for the first 9 merges.
3. **Side assignment.** The stream side comes from merge subjects. Two
   merges (`6b1239e`, `3858417`) are placed on `r5/shell` from the baseline
   report and parent ancestry.
4. **False positive means "merged cleanly".** A clean merge can still break
   semantically: the port-4322 regression came through a conflict
   resolution. A flag on a clean merge is not necessarily wrong.
5. **Path granularity.** Overlap is per path, not per hunk. Managed `.ros/`
   overlap is excluded.
6. **No verification evidence existed**, so merge readiness could never be
   ready. That is a property of R5's evidence, not of its work.
7. **One rehearsal, one contract author, no counterfactual run.** Nothing
   here estimates an effect.

## Reproduce

See `research/experiments/EX-ROS-2026-A025-r5-integration-replay/README.txt`.
