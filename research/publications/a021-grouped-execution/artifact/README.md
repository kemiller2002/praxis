# Anonymous reviewer artifact: grouped vs independent AI-agent execution

This bundle accompanies the submission "Does Shared Agent Context Reduce
Architectural Drift? A Blinded Replication Study of Grouped and Independent AI
Software-Engineering Execution". It contains everything needed to inspect the
two studies the paper reports:

* **A021**, the original study: one cohort of five tightly coupled work items
  (`PRAXIS-GROUP-01..05`, the `work group create | show | add | remove |
  checkpoint` commands) implemented twice from one baseline commit, once by a
  single grouped agent session and once by five independent per-item sessions,
  then compared by two blind evaluations.
* **R2**, a same-domain execution replication: the same cohort, the same
  baseline and the same protocol, executed again with fresh sessions and
  evaluated by a further independent blind evaluator.

The subject system is an open-source F#/.NET command-line tool. Its own name
appears throughout the code and the evaluated patches and is deliberately not
aliased (see "Anonymization" below).

## Layout

```
README.md                          this file
MAPPING.json                       blind arm name -> execution mode, for both studies
manifest.json                      every bundle entry: role, pinned origin, sha256, redactions
checksums.txt                      sha256 of every file in the bundle (sorted; excludes itself)
acceptance-criteria.txt            the five work items and their acceptance criteria
evaluation-protocol.txt            the frozen protocol, as committed before execution
evaluation-protocol-with-results.txt  the same protocol after results were appended
baseline/
  BASELINE.txt                     baseline commit, tree id, exclusions, redaction summary
  FILES.tsv                        every shipped baseline file: mode, original Git blob id, sha256, redacted?
  work-items-PRAXIS-GROUP-01..05.json  the cohort's backlog entries at the baseline
  source/                          the baseline source tree (see "Baseline policy")
a021/
  arm-x.patch, arm-y.patch         squashed diffs of each blinded arm against the baseline
  blind-mapping.json               the A021 random assignment, unsealed after evaluation
  evaluation/                      evaluation 1 report, evaluation 2 (kit) findings, evaluator brief, blinding script
  harness/                         implementation and evaluator prompts, harness note, metrics script, session ledger
  metrics-raw/                     per-session metrics captured inside each implementing session
r2/
  arm-M.patch, arm-N.patch         the exact patches the R2 blind evaluator received
  SHA256SUMS                       pre-evaluation checksums of those patches and the criteria
  README.txt                       the R2 blind bundle instructions given to the evaluator
  mapping-commitment.sha256        the R2 pre-evaluation mapping commitment
  evaluation/                      R2 blind report, findings, evaluator summary, post-unblinding metrics
  metrics-raw/                     per-session metrics and the independent-arm run log
data/                              paper datasets (metrics, architecture findings, threats), when present
analysis/                          redaction, verification and analysis scripts (Python 3 standard library)
```

`manifest.json` lists any expected dataset that was not yet available when the
bundle was built with `"status": "pending"`.

The scripts in `analysis/` are copies of the publication scripts.
`verify_artifact.py --bundle` runs from this bundle alone. The metric
extraction and checking scripts (`extract_metrics.py`, `verify_metrics.py`,
`check_architecture_findings.py`, `build_tables.py`, when present) read pinned
Git objects of the full study repository and cannot run from the bundle; their
outputs are `data/`, and the raw per-session records they read are in
`a021/metrics-raw/`, `a021/harness/sessions.json` and `r2/metrics-raw/`, so
every figure can be recomputed by hand. The full repository, with these
scripts runnable as-is, will be released after review.

## Arm mapping

Evaluation is complete in both studies and the paper reports the mapping, so
it is stated here; the blind file names are kept so that evaluator outputs,
which cite them, can be read verbatim.

| Study | Blind name | Execution mode |
|-------|-----------|----------------|
| A021  | arm-x     | grouped        |
| A021  | arm-y     | independent (control) |
| R2    | arm-N     | grouped        |
| R2    | arm-M     | independent (control) |

`MAPPING.json` records the evidence for each assignment. For R2 the mapping was
established after the blind report by exact file-blob identity with the
original arm trees; the salted commitment could not be recomputed because the
salt was not retained (a protocol deviation the paper reports).

## Checking the bundle

```
sha256sum -c checksums.txt                         # every file is intact
python3 analysis/verify_artifact.py --bundle .     # checksums, manifest, patch application
```

With `git` available, `verify_artifact.py --bundle .` also checks that all four
arm patches apply cleanly to `baseline/source/` (`git apply --check`). To build
and test an arm: copy `baseline/source/` to a fresh directory, run
`git init && git apply <patch>` there, then build with .NET SDK 10:

```
dotnet build Ros.slnx -c Release -p:FSharpCoreImplicitPackageVersion=10.1.400
dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll
```

Unredacted baseline files can be compared with the original commit by Git blob
id (`git hash-object <file>` equals the `git_blob_at_baseline` column of
`baseline/FILES.tsv`). The bundle itself was produced deterministically
(sorted entries, fixed timestamps and permissions) from inputs pinned to exact
commits; `manifest.json` lists the origin of every entry.

## Anonymization

The goal is that no file names a person: no personal names, e-mail addresses,
account names, account-scoped repository or site URLs, or agent-session
identifiers and links.

* **Person identifiers** are replaced everywhere, in code and in records, by
  neutral placeholders (`Anonymous Owner`, `owner`, `anonymous-owner`,
  `anonymous@example.invalid`). In the baseline they occur in documentation,
  packaging metadata, installer scripts, one installer default argument, and a
  person's first name used as an opaque actor id in four test files (replaced
  by `owner`, which has the same length). `baseline/FILES.tsv` marks every
  redacted file. The redaction is behaviour-neutral for the evaluated surface:
  the F# test suite gives the same per-test results on the original and the
  redacted baseline (792 of 792 passing in both), and the arm patches contain
  no person identifiers and ship byte-identical to their pinned sources.
* **Agent-session identifiers** and transcript/tool UUIDs in the study records
  are replaced by stable pseudonyms (`agent-session-NNN`, `uuid-NNN`) that are
  consistent across all files, so cross-references between the session
  ledger, the metrics files and the run logs still resolve. Session links are
  replaced by `<agent-session-url-redacted>`. Commit trailer lines that name a
  person or a session are removed from records.
* **Datasets** drop any field named `internal_ref`.
* **Patches** are squashed `git diff` output with no commit metadata, so no
  author, committer or date is present.

Retained by design:

* **Product, package and organisation names** of the subject system. They are
  embedded in source identifiers, the CLI command name, the work-item ids
  (`PRAXIS-GROUP-*`), package ids, configuration paths read by the code, and the
  evaluated patches. Rewriting them would change the evidence and, in places,
  the behaviour of the code, so they are kept verbatim.
* **Commit SHAs**, which anchor every claim to an exact tree.
* **Tool identities** (model and evaluator product names), which the paper
  discloses.

Reviewers are asked not to use these names or SHAs to look up the subject
system's public repository during review.

## Baseline policy

`baseline/source/` is the baseline commit's tree minus four whole subtrees
(listed with reasons in `baseline/BASELINE.txt`): the tool's own operating
state and telemetry, CI workflows, the research records, and owner-supplied
input documents. These are dense with person and session identifiers and are
not needed to read, apply, build or test the arm patches; the study records
relevant to the paper are shipped separately, sanitized, under `a021/` and
`r2/`. Everything else is the exact baseline content except for the
person-identifier replacements recorded per file in `baseline/FILES.tsv`.
