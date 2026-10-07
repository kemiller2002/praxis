# Reproducibility and anonymization plan

## Goal

Make the study independently inspectable while complying with double-anonymous review.

The public repository is identifying. Do not link it from the anonymous submission. Reviewers receive the **review bundle** described below. The **faithful bundle** is the post-acceptance (camera-ready) artifact.

## Two editions

| Edition | Bundle | Purpose | What changes relative to the pinned sources |
|---------|--------|---------|---------------------------------------------|
| review (default, submission) | `build/anonymous-artifact/`, `anonymous-artifact.tar` | Double-anonymous review | Person and session identifiers removed. Product, organisation and sibling-tool names aliased in contents **and paths**. Every resolving Git object id pseudonymised. |
| faithful (camera-ready) | `build/artifact-faithful/`, `artifact-faithful.tar` | After acceptance | Person and session identifiers removed. Everything else verbatim; patches byte-identical. |

Every build produces both editions from the same pinned inputs.

## Pipeline (implemented)

All scripts are Python 3 standard library, written as a functional core (pure functions over immutable data) with I/O at the edges.

| File | Role |
|------|------|
| `scripts/artifact_sources.json` | Pinned inputs: exact commit SHA and path (or base/head/pathspec for generated diffs) for every bundle member; optional working-tree datasets; blind-arm mapping; review aliases, retained-token justification, SHA policy and expected test counts. Never bundled, because it names the real tokens. |
| `scripts/anonymize.py` | Pure redaction rules, alias and object-id pseudonym rules, dataset transforms, denylist construction and scanning. Contains no identifying literal. |
| `scripts/build_anonymous_artifact.py` | Builds both editions, their tars, manifests, checksums, the private provenance map and the private denylist. |
| `scripts/verify_artifact.py` | Rebuilds and compares, checks checksums and manifests, scans for identifiers, and checks that patches apply. `--behavior` builds and tests every target from both editions. Also runs from inside a bundle (`--bundle`). |
| `artifact/README.md` | Bundle README template with per-edition blocks. The paper title is read from `manuscript/paper.tex` `\title` at build time. |
| `artifact/manifest.json`, `artifact/checksums.txt` | Review edition, generated. |
| `artifact/faithful/manifest.json`, `artifact/faithful/checksums.txt` | Faithful edition, generated. |
| `internal/provenance-map.json` | **Private.** Every alias and pseudonym mapped to its real value: persons, sessions, UUIDs, arms, product tokens, object ids (`review.object_pseudonyms`, `review.object_tokens`), renamed paths, per-file change counts, and per-file origins. Never bundled. |
| `internal/denylist.json` | **Private.** Person identifiers, real product tokens and all real object ids that the scanner must never find. Never bundled. |
| `internal/behavior-verification.json` | **Private.** Per-target build and test results of both editions, written by `verify_artifact.py --behavior`. |
| `build/` | Generated bundles; git-ignored. |

Commands, from the repository root:

```
python3 research/publications/a021-grouped-execution/scripts/build_anonymous_artifact.py
python3 research/publications/a021-grouped-execution/scripts/verify_artifact.py              # both editions, non-strict
python3 research/publications/a021-grouped-execution/scripts/verify_artifact.py --strict     # submission gate
python3 research/publications/a021-grouped-execution/scripts/verify_artifact.py --behavior   # + build/test parity (~40 min)
```

`--review` or `--faithful` restricts verification to one edition.

The build fetches any pinned arm commit that is missing locally (`git fetch origin <branch>`); it never checks out a branch.

`--strict` also fails while any optional dataset is still `pending` or any input or published file is uncommitted. Run it after everything is committed and immediately before submission.

Reviewers can check an unpacked bundle with `python3 analysis/verify_artifact.py --bundle .`.

### Determinism

- Every Git input is read as a blob of a pinned commit (`git show`, `git cat-file --batch`). Generated diffs use fixed options: `--full-index --no-renames --diff-algorithm=myers`, fixed prefixes, no colour or external diff.
- Traversal is sorted. Pseudonyms are numbered by the sorted order of the real values.
- Text written by the build is UTF-8 with LF line endings. Baseline blobs keep their bytes except for the recorded changes.
- The tar uses GNU format with sorted entries, `mtime = source_date_epoch` (1790726400, 2026-09-30T00:00:00Z), uid and gid 0, empty owner names, and modes 0644/0755.
- `verify_artifact.py` rebuilds into a temporary directory and requires every sha256 to match the published checksums, including the tars'.
- One environmental input remains in the review edition: which hex tokens resolve in the local object store. Fetching more objects can only add pseudonyms, and the verifier reports any resulting drift as a checksum mismatch.

## Bundle layout

```
anonymous-artifact/            (artifact-faithful/ has the same layout, with real names)
  README.md  MAPPING.json  manifest.json  checksums.txt
  acceptance-criteria.txt
  evaluation-protocol.txt                  frozen protocol at the baseline commit
  evaluation-protocol-with-results.txt     same file after results were appended
  baseline/  BASELINE.txt  FILES.tsv  work-items-<ITEM>-GROUP-01..05.json  source/...
  a021/      arm-x.patch  arm-y.patch  blind-mapping.json
             evaluation/{evaluation.txt, findings.json, evaluator-prompt.txt, prepare-blind-bundle.sh}
             harness/{harness-note.txt, session_metrics.py, sessions.json, prompts/*.txt}
             metrics-raw/{grouped.json, control-0[1-5].json, group-analysis.md}
  r2/        arm-M.patch  arm-N.patch  SHA256SUMS  README.txt  acceptance-criteria.txt  mapping-commitment.sha256
             evaluation/{EVALUATION.txt, findings.json, POST-UNBLINDING-METRICS.txt, EVIDENCE-PENDING-ID.txt}
             metrics-raw/{grouped.json, session-metrics-raw.json, control-0[1-5].json, control-run-log.md, ...}
  data/      metrics.csv  metrics.json  metric-conflicts.json  architecture-findings.json  threats.json
  analysis/  *.py from scripts/ (except check_manuscript.py)
```

Arm patches keep their neutral names, so the evaluator outputs that cite them can be read verbatim. `MAPPING.json` and the README state the mapping, because evaluation is complete and the paper reports it:

- A021: arm-x = cohort (grouped), arm-y = per-item (control).
- R2: arm-N = cohort, arm-M = per-item.

A021 patches are squashed `git diff`s of the neutral-label arm heads against the baseline. They exclude `.ros/` and the arms' own `EX-ROS-2026-A021-*` records, as the original evaluation kit did. R2 patches are the files the R2 evaluator received. In the faithful edition they are byte-identical and match `r2/SHA256SUMS`. In the review edition they carry the mechanical rename, so `r2/SHA256SUMS` matches the faithful edition only; the bundle README says so.

Not bundled:

- `data/metrics-sources.md`. Its author marks it INTERNAL because it names platform session identifiers.
- `data/evidence-index.json`. It contains internal paths and record ids.
- `data/venues.json` and `data/references-verification.json`. They concern the manuscript.
- `scripts/check_manuscript.py`. It is a manuscript lint whose rules list the real identifiers.
- The `EV-` evidence records.

The verifier fails if any of these file names appears in a bundle.

## Anonymization policy

### Both editions (person level)

These are enforced by a case-insensitive denylist scan of every file's contents and path, in the bundle directory and in the tar:

- **Personal names, e-mail addresses and account names.** They are discovered at build time from `git log` author and committer fields over every pinned commit, plus the origin remote's owner.
  - The account stem (the account name without trailing digits) is included.
  - Tool identities (model vendors, CI bots, the blinding identities) are kept.
  - Replacements: `Anonymous Owner`, `owner` (case-matched, same length as the first name it replaces), `anonymous-owner`, `anonymous@example.invalid`.
- **Account-scoped URLs.** The account name inside them is replaced.
- **Agent-session identifiers.** They become `agent-session-NNN`, and session URLs become `<agent-session-url-redacted>`.
- **Records only:**
  - transcript and tool UUIDs become `uuid-NNN`;
  - `Co-Authored-By:` and session trailer lines are dropped;
  - dataset fields named `internal_ref` are dropped.
- **Patches.** They are plain diffs, with no author or committer headers.

### Review edition (product level)

**Aliasing.** A single mechanical, case-preserving, **same-length** rename is applied to every text file and every path. This covers the baseline tree, all four patches, prompts, protocol, evaluator outputs, raw metrics, datasets, the manifest and the README. The real-to-alias table lives only in `artifact_sources.json` and `internal/`.

| Real (category) | Alias | Matching |
|-----------------|-------|----------|
| product name (6 letters) | `subjex` | substring, any case |
| organisation name (7 letters) | `acmelab` | substring, any case (covers package ids, npm scope, `.<org>/` config directory, CLI and env-var names) |
| sibling-tool name (4 letters) | `nexa` | Capitalised and UPPER forms anywhere; lower case only after a non-letter, so unrelated identifiers such as `recordOk` are untouched |
| "repository operating system" (the system's former public repository name) | "repository lifecycle engine" | word-wise, with `-`, `_`, whitespace or no separator, any case |
| "research operating system" (an earlier name) | "research lifecycle engine" | as above |

- **Case is transferred position by position**, so the identifier families stay consistent: `Praxis`/`PRAXIS`/`praxisRoot` map to `Subjex`/`SUBJEX`/`subjexRoot`.
- **Same length keeps byte offsets, padding and patch hunk geometry unchanged.** The patches therefore apply to the aliased baseline without being regenerated. The verifier checks `git apply --check` for all four.
- **The build refuses to run if any alias form already occurs in the faithful corpus**, so the rename is invertible.

**Object ids.** Every lowercase hex token of 7–40 characters that resolves in the repository (`git cat-file --batch-check`) is replaced by a same-length pseudonym. This covers commits, trees and blobs: patch `index` lines, `FILES.tsv`, protocol text, evaluator reports, datasets and the manifest.

- Pseudonyms are prefixes of one 40-hex pseudonym per object, so `8b4ffa3` and its full SHA stay consistent.
- The build checks that no pseudonym resolves or collides with an existing token.
- All-digit tokens are included: in this corpus every all-digit token that resolves is a genuine abbreviated id, such as patch `index` lines.

**Retained, with justification:**

- **The three-letter project prefix** (`Ros.*` namespaces, assembly names, the `ros` CLI, `EX-ROS-*` record ids). It is a generic acronym shared with well-known unrelated software. Its two expansions, the former repository name and an earlier name, are aliased. Renaming it would touch every project, namespace and assembly name, with no anonymity gain over the aliases.
- **Tool identities**, which the paper discloses.

**Review denylist (blocking).** The scan fails on any of the following:

- person identifiers;
- the real product, organisation and sibling-tool tokens and both expanded names;
- any hex token that abbreviates a real object id recorded in the map.

### Faithful edition

The faithful edition keeps product names and SHAs. The verifier reports their counts as advisory notes.

## Baseline policy

`baseline/source/` is the baseline commit minus five subtrees. `baseline/BASELINE.txt` records each one's reason and file count:

- `.ros/`: operating state, events, telemetry and remote requests.
- `.github/workflows/` and `.github/actions/`: owner-bound CI.
- `research/`: prior study records. The relevant ones ship sanitized under `a021/` and `r2/`.
- `input-documents/`: owner-supplied request documents.

`.github/copilot-instructions.md` is kept because the infrastructure project embeds it as a resource.

**Person-identifier redaction (both editions) touches 43 files.** These are docs, packaging metadata, registries, schemas, installer scripts and site data. Code-affecting cases:

- an installer's default `--source-repository` argument (`src/Ros.Cli/InstallationCommands.fs`);
- the package project URL (`src/Ros.Cli/Ros.Cli.fsproj`), which the patches also modify in unrelated lines;
- a first name used as an opaque actor id, replaced by the same-length `owner` in four F# test files and one JS test file.

**The review edition additionally renames 31 paths** (for example `.<org>/`, `bin/<org>.sh`, `bin/<product>-native.*`, `schemas/<product>-remote-*.json`). It also rewrites product tokens and object ids in contents. `FILES.tsv` marks each file as `redacted` and/or `aliased`.

**Faithful `FILES.tsv` lists original blob ids**, so each unredacted file can be checked with `git hash-object`.

### Behaviour preservation

Run by `verify_artifact.py --behavior` with .NET SDK 10.0.112 (`dotnet build Ros.slnx -c Release -p:FSharpCoreImplicitPackageVersion=10.1.400`, then the F# test runner). Results are in `internal/behavior-verification.json`.

BEHAVIOR_PLACEHOLDER

## Identifying content that could not be removed

1. **Verbatim code and prose.** Every distinctive identifier, string or comment of a public repository can, in principle, be found with a code-search engine. Record ids such as `EX-ROS-2026-A021`, F# type names and test names all remain verbatim. Aliasing reduces casual exposure and removes every direct pointer (names, URLs, object ids), but it cannot make public code unsearchable without rewriting the evidence. The submission therefore relies on the venue's rule that reviewers do not search for authors.
2. **GitHub Actions run numbers** cited by the R2 evaluator. The account is redacted but the run ids remain. They are not object ids, so they are not pseudonymised.
3. **Free-text style**: agent-written prose and comments.
4. **The `r2/SHA256SUMS` values in the review edition** refer to the pre-rename patches. They are kept unchanged because they are evaluator-facing evidence.

## Required reproducibility checks

| # | Check | Status |
|---|-------|--------|
| 1 | Checksums of blind arm patches match the recorded bundle | Verifier: bundle checksums. Faithful: patches byte-identical to pinned sources and to `r2/SHA256SUMS`. Review: patches differ from source only by the mechanical rename. |
| 2 | Baseline tree corresponds to the baseline commit | Faithful: `BASELINE.txt` tree id, `FILES.tsv` blob ids. Both editions: patches apply. |
| 3 | Paper metric table can be regenerated from `metrics.csv` plus analysis script | `data/` and `analysis/` bundled. Extraction scripts need the full repository (pinned Git objects); documented in the bundle README. |
| 4 | Every qualitative architecture row has source locations for both arms | `data/architecture-findings.json` bundled; checked by its own script. |
| 5 | Evaluator output remains unchanged after unblinding | Evaluator outputs bundled from their pinned commits, record profile only (review: plus rename). |
| 6 | Mapping reveal is stored separately from blind findings | `MAPPING.json`, `a021/blind-mapping.json`, `r2/evaluation/POST-UNBLINDING-METRICS.txt` |
| 7 | Protocol deviations are included, not removed | Frozen and post-results protocol texts, evaluator threat sections, `data/threats.json` |
| 8 | Unknown/missing metrics remain missing | Datasets are bundled as produced; only `internal_ref` is stripped. |
| 9 | Anonymization does not change behaviour | `verify_artifact.py --behavior`: identical per-test outcomes, faithful vs review, for baseline and all four arms. |

## AI-authorship disclosure

Before submission, check the selected venue's current policy.

The paper must:
- list only human authors who satisfy the venue/IEEE authorship policy;
- treat AI systems as tools, not authors;
- disclose AI-assisted writing/coding when required;
- independently verify references, factual claims, tables, and generated text.

## Double-anonymous checklist

Before submission:

- [ ] no author names or affiliations in PDF;
- [ ] no acknowledgments;
- [ ] self-references written in third person;
- [ ] artifact URL is anonymous or omitted;
- [ ] PDF metadata does not identify authors;
- [ ] repository screenshots do not expose user/org names;
- [ ] branch/commit links in the paper do not point to identifying public URLs;
- [ ] supplemental archive filenames are neutral (`anonymous-artifact.tar`, the review edition);
- [ ] no session URL contains user identity;
- [ ] all citations are real and manually checked;
- [ ] `verify_artifact.py --strict` passes on the committed state.

## Post-review release

If accepted, publish the faithful edition (`artifact-faithful.tar`) and:
- canonical evidence manifest (`artifact/manifest.json` plus `internal/provenance-map.json` origins);
- anonymization mapping (`internal/provenance-map.json`);
- final metrics dataset;
- analysis scripts;
- exact artifact SHAs;
- final paper;
- a replication guide for applying the protocol to another repository.
