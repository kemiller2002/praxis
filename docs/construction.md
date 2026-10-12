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

   `preview` and `apply` output report the admitted plan exactly:
   - `plan`: the plan in Ordo's canonical form. Its SHA-256 is
     `binding.planDigest`.
   - `binding.planSourceManifestDigest` and `binding.planBlueprintDigest`:
     the canonical digests that plan is bound to.
   - `binding.manifestDigest` and `binding.blueprintDigest`: the byte digests
     of the committed ECIR files, which is what the operator grant pins.

   An independent verifier such as Dokimos `construction verify` reads the
   reported plan directly instead of rebuilding a host-stamped binding from the
   agent's operations. Submitting the reported plan again yields the same plan
   digest.

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

## Routing and handwritten extension slots

Praxis #243 and decision `DF-ROS-2026-A072` add two commands.

**`construct route`** classifies the admitted construction's work, mechanical
work first:

| Class | Source | Route |
|---|---|---|
| Deterministic construction | Ordo's generated artifacts | the host's deterministic constructor (`apply`) |
| Semantic decision | `resolve` and `resolve-effect-failure` obligations | an authorized human decision; never a worker |
| Bounded custom implementation | `implement` obligations | the one host-declared slot whose contracts name it |
| Independent verification | `verify` obligations | independent verification (Dokimos); never closed here |

An `implement` obligation that no slot names becomes a typed
`unroutable-implementation` exception. It keeps the obligation's original
requirement keys. It never falls back to general repository write. Resolving
it needs an operator-authorized host configuration revision that declares a
slot, followed by a new route.

The routing record (`praxis.construction-routing/1`) is written once to
`routes/ROUTE-ID.json` in the host construction root, never to `.ros`. The
record has no clock reading, and its ID is the digest of its content, so a
rerun finds the same file. Its `measurement` block counts:
- deterministic writes and bytes;
- bounded custom slots and obligations;
- semantic and verification obligations;
- exceptions.

That separates reasoning work from mechanical work. Reasoning time and tokens
read `unavailable`, because routing does not observe them; execution telemetry
owns them. Routing is a separate command rather than part of `preview`
because `preview` is read-only by contract, and persisting a record is an
effect.

**`construct extend`** commits handwritten files into declared slots on top of
an exact construction. In order, it:

1. Refuses a non-implementation `--role`, then re-admits the plan exactly as
   `apply` does: grant, signed approval, committed sources and pinned engine.
2. Judges every entry in `--files DIR` on its own, and reports every refusal
   before any effect. It accepts only a regular, non-empty, UTF-8 file without
   a byte-order mark, NUL bytes or the Ordo generated-file marker, whose name
   is exactly a declared slot that implements at least one of this
   construction's contracts, within that slot's `maxBytes`. It refuses:
   - symlinks (and a linked `DIR`), subdirectories and extra or undeclared
     entries, including a case-only variant of a slot name;
   - a name with a path separator;
   - dot entries such as `.github`, `.claude`, `.echelon` and `.git`;
   - the `generated` directory and `*.g.*` names;
   - project and build files (`*.fsproj`, `*.props`, `*.targets`, `*.sln`,
     and others);
   - package and dependency files (`NuGet.config`, `paket.*`,
     `packages.lock.json`, `global.json`, and others);
   - policy, agent-instruction and CI files (`ros.json`, `AGENTS.md`,
     `*.yml`, and others);
   - scripts and binaries;
   - ECIR documents and requirement sources.
3. Requires the target to carry this exact construction. Every generated file
   must be unchanged, and its history must hold a commit with this plan's
   `Construction-Plan`, `Construction-Cohort` and `Construction-Group`
   trailers. Otherwise the plan is stale for this target, and `extend`
   refuses.
4. Refuses placement inside the generated root, in an ECIR source's
   directory, on a requirement source, or over a generated or non-regular
   entry.
5. Writes the routing record, builds one commit from Git objects in a private
   index, journals it like `apply`, and moves the target by compare-and-swap.
   The same files yield `already-current`.

The commit carries these trailers:
- `Extension-Construction` (the construction ID `apply` reported, and the
  construction commit);
- `Extension-Plan`, `Extension-Group`, `Extension-Cohort` and
  `Extension-Route`;
- `Extension-Policy`;
- one `Extension-Slot` per file, with its digest and contracts.

**No worker runs.** `extend` commits submitted bytes as data. It never starts
a worker, a build or any submitted code. Autonomous custom-code worker
execution stays disabled until host containment is qualified (#231, #235).
`--worker`, `--command`, `--run`, `--exec`, `--agent-command` and `--adapter`
are refused with that reason; they are never ignored.

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
- `extensionSlots`: handwritten extension slots directly under the mount (at
  most 32), for example:

  ```json
  "extensionSlots": [
    { "file": "Extensions.fs",
      "contracts": ["ITimerPorts.CurrentTime", "ITimerPorts.Perform/Stop"],
      "maxBytes": 16384 }
  ]
  ```

  `file` is one exact file name, never a path or glob. `contracts` are Ordo
  extension-contract names, each assigned to at most one slot. `maxBytes` is
  between 1 and 262144. The deny rule above refuses an unsafe slot name when
  the configuration loads. Slots are refused when the mount lies under a dot
  directory.

Authority comes from the same operator grant as `praxis local` (see
[local-runtime.md](local-runtime.md)). The operator signs a scope covering
the group, cohort, source commit, manifest and blueprint digests, requirement
keys and decisions.

## Commands

```bash
./praxis construct preview --host-config HOST.json --group GROUP-ID --plan plan.json --target refs/heads/BRANCH
./praxis construct apply   --host-config HOST.json --group GROUP-ID --plan plan.json --target refs/heads/BRANCH
./praxis construct route   --host-config HOST.json --group GROUP-ID --plan plan.json
./praxis construct extend  --host-config HOST.json --group GROUP-ID --plan plan.json --target refs/heads/BRANCH --files OUTBOX [--role implementation]
```

Output is JSON (`praxis.construction-preview/1`,
`praxis.construction-application/1`, `praxis.construction-routing/1` and
`praxis.construction-extension/1`). Exit codes: `0` success, `2` invalid
arguments, `3` refused (the message names the check).

## What this does and does not establish

- **Construction API prevention.** Arbitrary writes, shell, packages, policy
  edits and path choice are unrepresentable in the plan. This is a property of
  the input language and of the host's own writer.
- **Host containment.** None is claimed here. The writer runs in the trusted
  Praxis process, and no agent process runs during construction or
  extension. Autonomous custom-code worker execution (Praxis #243) needs
  qualified OS containment (#231, #235). It is not implemented, and it is
  explicitly refused.
- **Slot scope.** Slots bound *what the host commits*: exact names, sizes and
  contracts. They do not bound what an author outside Praxis read or ran
  while writing the files. The submission directory is read with file
  mechanics, not OS protection.
- **Evidence.** Ordo's structural checks are derived from the plan and are
  not independent acceptance evidence. Behavioral verification belongs to
  Dokimos.

## Ordo vocabulary 2 (Ordo 1.7.0)

Praxis pins Ordo 1.7.0, which reads both vocabularies.

- **Vocabulary 1 plans** are unchanged. They keep their digests and generated
  bytes, and use generator `ordo.construction/1`.
- **Vocabulary 2 plans** (generator `ordo.construction/2`) must give every
  `transitionEffect` port an `onFailure` disposition:
  - `remain` keeps the workflow in its source state when the effect fails or
    its outcome is unknown;
  - `proceed` lets the target state stand and reports the failure;
  - `unresolved` leaves the question open.
- **Who decides.** `remain` and `proceed` are admitted only when the plan
  cites a decision the approved cohort requires. Without one, the only legal
  disposition is `unresolved`, so an agent cannot settle what a failed effect
  means on its own.
- **How Praxis reports it.** An `unresolved` disposition appears in `preview`
  and `apply` output as a `resolve-effect-failure` obligation naming the
  effect. `construct route` classifies it as a semantic decision
  (`effectFailure` in the routing record), never as custom code.

This came from timer pilot 01 (research-documents#7, finding F2). There,
handwritten session code silently decided this question, and different runs
decided it differently.

