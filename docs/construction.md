# Host-controlled construction (`praxis construct`)

`praxis construct` applies Ordo's deterministic construction output to a
repository without giving the planning agent repository write access
(Praxis #242, decision `DF-ROS-2026-A071`). The agent's only input is a typed
Ordo construction plan (`ordo.construction-plan/1`). It cannot name file paths,
packages, shell commands, policies or validators. The plan vocabulary cannot
express them, and the host decides placement.

## What the host does

1. **Admission.** Praxis reloads the protected ECIR host policy and resolves
   the grant for the named native group against its current members. It
   re-verifies the operator's signed approval at the current time, then reads
   the exact committed manifest and blueprint bytes at the approved source
   commit and compares them with the approved digests. In-process, the pinned
   Ordo engine (`EchelonFoundry.Ordo.Core` and
   `EchelonFoundry.Ordo.Construction`, vendored release assets) checks:
   - the manifest's canonical digest;
   - cohort readiness under the *signed* decisions (never the agent's);
   - the plan's binding to that prepared cohort.

   A plan may omit `binding`. The host then stamps it from the prepared
   cohort: the manifest digest, Ordo's canonical blueprint digest, the cohort
   ID, and the cohort's required decisions and rationales. The agent supplies
   only operations and never copies digests by hand. In the timer pilot, every
   construction agent first copied the blueprint file's hash, which is not the
   canonical digest (research-documents#7, finding F1). A supplied binding is
   still checked exactly as before, and an agent cannot use it to rebind the
   plan to another cohort.

   Only then does Praxis call `Construction.construct`. An admitted
   construction is an opaque value that only admission can produce.
2. **Preview** (read-only). Praxis reads every file under the generated
   ownership root from the target commit's Git tree, never from a working
   tree or filesystem path. A symlink, submodule, executable or other
   non-regular entry, or a non-directory ancestor, refuses the whole
   construction. Ordo's `planWrites` then decides `create`, `replace`,
   `unchanged` or `remove-stale`. It refuses to overwrite a handwritten file,
   to leave a handwritten file inside the generated root, or to write outside
   that root.
3. **Apply.** Praxis builds a candidate commit in a private index outside the
   repository from exactly the planned writes, and verifies that the candidate
   differs from the target only at those paths with exactly those bytes. It
   journals the plan in the host's construction journal, then moves the
   target with `git update-ref REF NEW OLD`. It never touches the checked-out
   branch or working tree.

The commit carries the provenance as trailers: plan digest, generator, Ordo
versions, group, cohort, source commit, manifest and blueprint
paths/digests, signed decisions, policy revision and approver. Ordo's
`construction-manifest.json` in the generated root traces every artifact to
its requirement keys.

Generated structure is not completion. Handwritten extension contracts,
unresolved questions and independent-verification obligations are reported
with `"status": "open"`, and nothing here closes them.

## Recovery

| Interruption | What a rerun of the same command does |
|---|---|
| Before the journal entry | Plans afresh; no effect had occurred. |
| After the journal entry, before `update-ref` | Rebuilds the identical candidate (the commit date is the journalled preparation time) and applies it if the target is still at the recorded head. |
| After `update-ref`, before the applied record | Records the entry as applied; the target already holds the candidate. |
| Target moved by someone else | Refuses; nothing is applied. A new plan against the new head is a new construction. |

## Configuration

Two optional host-configuration fields (`praxis.local-host/1`, outside the
repository):

- `roots.construction`: the construction journal directory. Construction is
  disabled without it.
- `constructionMount`: the repository-relative directory under which Ordo's
  `generated/<Workflow>/` roots are placed (default `.`). It cannot be under
  `.git`, `.ros`, `.praxis`, `.github`, `.claude` or `.echelon`.

Authority comes from the same operator grant as `praxis local` (see
[local-runtime.md](local-runtime.md)). The operator signs a scope covering
the group, cohort, source commit, manifest and blueprint digests, requirement
keys and decisions.

## Commands

```bash
./praxis construct preview --host-config HOST.json --group GROUP-ID --plan plan.json --target refs/heads/BRANCH
./praxis construct apply   --host-config HOST.json --group GROUP-ID --plan plan.json --target refs/heads/BRANCH
```

Output is JSON (`praxis.construction-preview/1` and
`praxis.construction-application/1`). Exit codes: `0` success, `2` invalid
arguments, `3` refused (the message names the check).

## What this does and does not establish

- **Construction API prevention.** Arbitrary writes, shell, packages, policy
  edits and path choice are unrepresentable in the plan. This is a property of
  the input language and of the host's own writer.
- **Host containment.** None is claimed here. The writer runs in the trusted
  Praxis process, and no agent process runs during construction. A
  custom-code worker (Praxis #243) needs qualified OS containment, which
  remains deferred.
- **Evidence.** Ordo's structural checks are derived from the plan and are
  not independent acceptance evidence. Behavioral verification belongs to
  Dokimos.
