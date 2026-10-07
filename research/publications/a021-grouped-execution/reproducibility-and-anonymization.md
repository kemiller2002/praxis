# Reproducibility and anonymization plan

## Goal

Make the study independently inspectable while complying with double-anonymous review.

The public Praxis repository is identifying. Do not link it directly from an anonymous SANER submission. Reviewers receive the anonymous artifact described below instead.

## Pipeline (implemented)

All scripts are Python 3 standard library, written as a functional core (pure functions over immutable data) with I/O at the edges.

| File | Role |
|------|------|
| `scripts/artifact_sources.json` | Pinned inputs: exact commit SHA and path (or base/head/pathspec for generated diffs) for every bundle member; optional working-tree datasets; blind-arm mapping. |
| `scripts/anonymize.py` | Pure redaction rules, deterministic pseudonyms, dataset transforms, denylist construction and scanning. Contains no identifying literal. |
| `scripts/build_anonymous_artifact.py` | Builds the bundle, its tar, the manifest, checksums, the private provenance map and the private denylist. |
| `scripts/verify_artifact.py` | Rebuilds and compares, checks checksums and manifest, scans for identifiers, checks that patches apply. Also runs from inside the bundle (`--bundle`). |
| `artifact/README.md` | README shipped as the bundle's `README.md`. |
| `artifact/manifest.json` | Generated manifest (also shipped inside the bundle). |
| `artifact/checksums.txt` | Generated sha256 of every bundle file and of the tar. |
| `internal/provenance-map.json` | **Private.** Alias to real identifier map, per-file origins, redacted baseline files with original blob ids. Never bundled. |
| `internal/denylist.json` | **Private.** The identifiers the scanner must never find. Never bundled. |
| `build/` | Generated bundle directory and `anonymous-artifact.tar`; git-ignored. |

Commands, from the repository root:

```
python3 research/publications/a021-grouped-execution/scripts/build_anonymous_artifact.py
python3 research/publications/a021-grouped-execution/scripts/verify_artifact.py            # non-strict
python3 research/publications/a021-grouped-execution/scripts/verify_artifact.py --strict   # submission gate
```

The build fetches any pinned arm commit that is missing locally (`git fetch origin <branch>`); it never checks out a branch.

`--strict` also fails while any optional dataset is still `pending` or any working-tree input is uncommitted. Run it after the datasets and scripts are committed and immediately before submission.

Reviewers can check the unpacked bundle on their own with `python3 analysis/verify_artifact.py --bundle .`. They can add `--denylist FILE` to repeat the identifier scan with a denylist the authors supply.

### Determinism

- Every Git input is read as a blob of a pinned commit (`git show`, `git cat-file --batch`). Generated diffs use fixed options: `--full-index --no-renames --diff-algorithm=myers`, fixed prefixes, no colour or external diff.
- Traversal is sorted. Pseudonyms are numbered by the sorted order of the real values.
- Text written by the build is UTF-8 with LF line endings. Baseline blobs keep the bytes Git stores.
- The tar uses GNU format with sorted entries, `mtime = source_date_epoch` (1790726400, 2026-09-30T00:00:00Z), uid and gid 0, empty owner names, and modes 0644/0755 (0755 only for Git-executable files and scripts with a shebang).
- `verify_artifact.py` rebuilds into a temporary directory and requires every sha256 to match `artifact/checksums.txt`, including the tar's.

## Bundle layout

```
anonymous-artifact/
  README.md  MAPPING.json  manifest.json  checksums.txt
  acceptance-criteria.txt
  evaluation-protocol.txt                  frozen protocol at the baseline commit
  evaluation-protocol-with-results.txt     same file after results were appended
  baseline/  BASELINE.txt  FILES.tsv  work-items-PRAXIS-GROUP-01..05.json  source/...
  a021/      arm-x.patch  arm-y.patch  blind-mapping.json
             evaluation/{evaluation.txt, findings.json, evaluator-prompt.txt, prepare-blind-bundle.sh}
             harness/{harness-note.txt, session_metrics.py, sessions.json, prompts/*.txt}
             metrics-raw/{grouped.json, control-0[1-5].json, group-analysis.md}
  r2/        arm-M.patch  arm-N.patch  SHA256SUMS  README.txt  acceptance-criteria.txt  mapping-commitment.sha256
             evaluation/{EVALUATION.txt, findings.json, POST-UNBLINDING-METRICS.txt, EVIDENCE-PENDING-ID.txt}
             metrics-raw/{grouped.json, session-metrics-raw.json, control-0[1-5].json, control-run-log.md, ...}
  data/      metrics.csv  metrics.json  metric-conflicts.json  architecture-findings.json  threats.json   (when present)
  analysis/  *.py from scripts/
```

Arm patches keep their blind names, so the evaluator outputs that cite them can be read verbatim. `MAPPING.json` and the README state the mapping, because evaluation is complete and the paper reports it:

- A021: arm-x = grouped, arm-y = independent.
- R2: arm-N = grouped, arm-M = independent.

A021 patches are squashed `git diff`s of the blinded arm heads against the baseline. They exclude `.ros/` and the arms' own `EX-ROS-2026-A021-*` records, as the original blind kit did. R2 patches are the exact files the R2 evaluator received. They are byte-identical and checked against the shipped `r2/SHA256SUMS`.

Not bundled:

- `data/metrics-sources.md`. Its author marks it INTERNAL because it names platform session identifiers.
- `data/venues.json` and `data/references-verification.json`. They concern the manuscript, not the evidence.
- The `EV-` evidence records. Their content is summarized by the bundled evaluator outputs.

## Anonymization policy

**Removed everywhere.** These are enforced by the denylist scan, which is case-insensitive and covers file contents and file names in both the bundle directory and the tar:

- Personal names, e-mail addresses and account names. These are discovered at build time from `git log` author and committer fields over every pinned commit, plus the origin remote's owner.
  - Tool identities (model vendors, CI bots, the blinding identities) are classified as tools and kept.
  - Replacements: `Anonymous Owner`, `owner` (case-matched), `anonymous-owner`, `anonymous@example.invalid`.
- Account-scoped URLs: `github.com/<owner>/...`, `raw.githubusercontent.com/<owner>/...` and `<owner>.github.io`. The account name inside them is replaced.
- Agent-session identifiers and session URLs. Identifiers become stable pseudonyms `agent-session-NNN`. URLs become `<agent-session-url-redacted>`.
- Transcript and tool UUIDs, in records only. They become stable pseudonyms `uuid-NNN`.
- `Co-Authored-By:` and session trailer lines, in records only.
- Fields named `internal_ref`, in datasets only.
- Git author metadata. Patches are plain diffs with no commit headers.

**Two redaction profiles**:

- The *code* profile applies to baseline source and patches. It applies person rules only and preserves bytes otherwise.
- The *record* profile applies to the protocol, prompts, evaluator outputs, metrics, logs and datasets. It applies all rules.

**Retained by design** (reported by the verifier as advisory counts, not failures):

- **Product and organisation names** ("Praxis", "Echelon Foundry", package ids, the `praxis`/`ros` CLI names, `.echelon/` paths). They are embedded in source identifiers, CLI command names, work-item ids (`PRAXIS-GROUP-01..05`), package ids, configuration paths the code reads, and both studies' arm patches.
  - Aliasing them would change the evaluated evidence bytes, would break context lines so the patches no longer apply, and in places would change behavior (for example, paths read at run time).
  - Aliasing only the prose would protect nothing: the code is public, and any distinctive fragment can be searched.
  - The README asks reviewers not to look the system up. The paper's prose may still use a neutral alias if the venue chairs prefer, but the artifact cannot.
- **Commit SHAs.** They anchor every claim and appear verbatim inside the evidence (protocol, evaluator reports, R2 README). A public commit-hash search can locate the repository. This is the main residual de-anonymization vector after product names.
- **Tool identities** (implementing model, evaluator product). The paper discloses them.

## Baseline policy

`baseline/source/` is the baseline commit `8b4ffa392e93b19bf39f6672a608954c934cb815` minus five subtrees. `baseline/BASELINE.txt` records each one's reason and file count:

- `.ros/`: operating state, events, telemetry and remote requests. These are dense with names and session identifiers.
- `.github/workflows/` and `.github/actions/`: owner-bound CI.
- `research/`: prior study records. The relevant ones ship sanitized under `a021/` and `r2/`.
- `input-documents/`: owner-supplied request documents.

`.github/copilot-instructions.md` is kept because the infrastructure project embeds it as a resource, and the baseline does not build without it.

Every other file is the exact baseline blob. The only exception is person-identifier redaction in 43 files: docs, packaging metadata, registries, schemas, installer scripts and site data. Two of these files affect code:

- `src/Ros.Cli/InstallationCommands.fs`: an installer's default `--source-repository` argument.
- `src/Ros.Cli/Ros.Cli.fsproj`: the package project URL. The arm patches modify this file in unrelated lines.

In addition, a first name used as an opaque actor id is replaced in four F# test files and one JS test file, and the account name is replaced in three further JS test files. The replacement for the first name, `owner`, has the same length.

`baseline/FILES.tsv` gives every file's mode, original blob id, shipped sha256 and redacted flag, so a reviewer can check each unredacted file with `git hash-object`.

Evidence that redaction does not alter behavior on the evaluated surface (run 2026-10-07 with .NET SDK 10.0.112, `dotnet build Ros.slnx -c Release -p:FSharpCoreImplicitPackageVersion=10.1.400`):

- The original baseline (`git archive 8b4ffa3`) and the redacted `baseline/source/` both build with 0 errors.
- Both pass 792/792 F# tests, with identical per-test result sets.
- All four arm patches apply to the redacted baseline (`git apply --check`; enforced by the verifier). Each arm also builds and passes its tests on it: ARM_RESULTS_PLACEHOLDER

## Identifying content that could not be removed

1. Product and organisation names in code and patches. See the policy above: removing them would alter the evidence.
2. Commit SHAs throughout the evidence.
3. Free-text style. Agent-written prose and code comments are kept verbatim, as in the original blind kits.
4. Platform-specific details in the evaluator outputs, such as the GitHub Actions run references the R2 evaluator cited. Their account names are redacted, but run numbers remain.

## Required reproducibility checks

| # | Check | Status |
|---|-------|--------|
| 1 | Checksums of blind arm patches match the recorded bundle | verifier: bundle checksums, `r2/SHA256SUMS`, patches byte-identical to pinned sources |
| 2 | Baseline tree corresponds to `8b4ffa392e93b19bf39f6672a608954c934cb815` | `baseline/BASELINE.txt` (tree id), `FILES.tsv` blob ids; patches apply |
| 3 | Paper metric table can be regenerated from `metrics.csv` plus analysis script | `data/` and `analysis/` bundled. Extraction scripts need the full repository (pinned Git objects); documented in the bundle README |
| 4 | Every qualitative architecture row has source locations for both arms | `data/architecture-findings.json` bundled when present; checked by its own script |
| 5 | Evaluator output remains unchanged after unblinding | evaluator outputs bundled from their pinned commits, record profile only |
| 6 | Mapping reveal is stored separately from blind findings | `MAPPING.json`, `a021/blind-mapping.json`, `r2/evaluation/POST-UNBLINDING-METRICS.txt` |
| 7 | Protocol deviations are included, not removed | frozen and post-results protocol texts, evaluator threat sections, `data/threats.json` |
| 8 | Unknown/missing metrics remain missing | datasets are bundled as produced; only `internal_ref` is stripped |

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
- [ ] supplemental archive filenames are neutral (`anonymous-artifact.tar`);
- [ ] no session URL contains user identity;
- [ ] all citations are real and manually checked;
- [ ] `verify_artifact.py --strict` passes on the committed state.

## Post-review release

If accepted, publish:
- canonical evidence manifest (`artifact/manifest.json` plus `internal/provenance-map.json` origins);
- anonymization mapping (`internal/provenance-map.json`);
- final metrics dataset;
- analysis scripts;
- exact artifact SHAs;
- final paper;
- a replication guide for applying the protocol to another repository.
