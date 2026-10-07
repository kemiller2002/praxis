<!-- mode:review -->
# Anonymous reviewer artifact
<!-- /mode -->
<!-- mode:faithful -->
# Study artifact (faithful edition)
<!-- /mode -->

This bundle accompanies the paper "{{PAPER_TITLE}}". It contains the
material the paper's two studies rest on:

* **A021**, the original study. One cohort of five coupled work items
  (`PRAXIS-GROUP-01..05`, the `work group create | show | add | remove |
  checkpoint` commands of an F#/.NET command-line tool) was implemented twice
  from one baseline commit. The treatment is *cohort execution with a
  mandated up-front cross-item analysis*: one coding-agent session receives
  all five items and must write and commit a cross-item analysis before
  changing code. The comparison arm is *per-item execution*: five sessions,
  one item each. Two evaluations were made under neutral arm labels, but
  neither was effectively blind (see the paper); their reports are included
  as records, not as blind judgements.
* **R2**, an internal, non-preregistered re-execution (an operational
  replication): the same cohort, baseline and repository instructions,
  executed again with new sessions (its prompts were not archived), then
  evaluated under neutral labels by a model from a different vendor whose
  blindness rests on its own report.

<!-- mode:review -->
This is the review edition. To keep the submission double-anonymous, the
subject system's product, organisation and sibling-tool names have been
replaced throughout by same-length aliases (`Subjex`, `Acmelab`, `Nexa`, and
`Repository Lifecycle Engine` for the expanded name), and every Git object
id (commit, tree or blob SHA, full or abbreviated) by a stable same-length
pseudonym. The rename is mechanical and applies to file contents and file
paths alike, including the baseline tree and all four patches. A faithful
edition with the original names and object ids will be released after
review.
<!-- /mode -->
<!-- mode:faithful -->
This is the faithful edition: product names and Git object ids are as in
the original repository, and the arm patches are byte-identical to their
pinned sources. Only person identifiers and agent-session identifiers are
removed (see "Anonymization").
<!-- /mode -->

## Layout

```
README.md                          this file
MAPPING.json                       blind arm name -> execution mode, for both studies
manifest.json                      every bundle entry: role, pinned origin, sha256, redactions
checksums.txt                      sha256 of every file in the bundle (sorted; excludes itself)
acceptance-criteria.txt            the five work items and their acceptance criteria
evaluation-protocol.txt            the A021 protocol, as committed before execution
evaluation-protocol-with-results.txt  the same protocol after results were appended
baseline/
  BASELINE.txt                     baseline commit, tree id, exclusions, redaction summary
  FILES.tsv                        every shipped baseline file and how it was changed
  work-items-PRAXIS-GROUP-01..05.json  the cohort's backlog entries at the baseline
  source/                          the baseline source tree (see "Baseline policy")
a021/
  arm-x.patch, arm-y.patch         squashed diffs of each arm against the baseline
  blind-mapping.json               the A021 label draw, recorded after evaluation
  evaluation/                      evaluation 1 report, evaluation 2 (kit) findings, evaluator brief, label-scrubbing script
  harness/                         implementation and evaluator prompts, harness note, metrics script, session ledger
  metrics-raw/                     per-session metrics captured inside each implementing session
r2/
  arm-M.patch, arm-N.patch         the patches the R2 evaluator received
  SHA256SUMS                       pre-evaluation checksums of those patches and the criteria
  README.txt                       the R2 bundle instructions given to the evaluator
  mapping-commitment.sha256        the R2 pre-evaluation mapping commitment
  evaluation/                      R2 evaluation report, findings, evaluator summary, post-unblinding metrics
  metrics-raw/                     per-session metrics and the per-item arm's run log
data/                              paper datasets (metrics, metric conflicts, architecture findings, threats)
analysis/                          redaction, verification and analysis scripts (Python 3 standard library)
```

`manifest.json` lists any expected dataset that was not yet available when the
bundle was built with `"status": "pending"`.

The scripts in `analysis/` are copies of the publication scripts. These run
from this bundle alone:

- `verify_artifact.py --bundle .` checks checksums, the manifest and the
  identifier scan;
- `build_tables.py` and `build_architecture_table.py` regenerate every paper
  table and number macro from `data/` (write them to `manuscript/tables/`);
- `check_architecture_findings.py --no-git` validates the architecture
  findings and checks every probe-based claim against `data/probes/`;
- `run_probes.py` re-runs the runtime probes against a built arm.

`extract_metrics.py` and `verify_metrics.py` read pinned Git objects of the
full study repository and cannot run from the bundle. The raw per-session
records they read are in `a021/metrics-raw/`, `a021/harness/sessions.json`
and `r2/metrics-raw/`, so every figure in `data/metrics.json` can be
recomputed by hand.

## Arm mapping

Evaluation is complete in both studies and the paper reports the mapping, so
it is stated here. The neutral file names are kept so that evaluator outputs,
which cite them, can be read verbatim.

| Study | Neutral name | Execution mode |
|-------|-------------|----------------|
| A021  | arm-x       | cohort (grouped) |
| A021  | arm-y       | per-item (control) |
| R2    | arm-N       | cohort (grouped) |
| R2    | arm-M       | per-item (control) |

`MAPPING.json` records the evidence for each assignment. Both mappings were
verified by source-tree identity with the original arm branches. The A021
label draw was recorded after the neutral branches were created, with no
commitment; the R2 commitment cannot be recomputed because its salt was lost.

## Checking the bundle

```
sha256sum -c checksums.txt                         # every file is intact
python3 analysis/verify_artifact.py --bundle .     # checksums, manifest, patch application
```

With `git` available, `verify_artifact.py --bundle .` also checks that all four
arm patches apply cleanly to `baseline/source/` (`git apply --check`). To build
and test an arm: copy `baseline/source/` to a fresh directory, run
`git apply <patch>` there, then build with .NET SDK 10:

```
dotnet build Ros.slnx -c Release -p:FSharpCoreImplicitPackageVersion=10.1.400
dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll
```

Expected F# test results, identical per test between the faithful and the
review edition: baseline 792 of 792 passing; A021 arm-x 811 of 811, arm-y 829
of 829; R2 arm-M 839 of 839, arm-N 819 of 819 (the R2 counts equal those the
R2 evaluator recorded).

<!-- mode:review -->
One external NuGet dependency of the subject system is published under the
organisation's name, so in this edition its package id is aliased
(`EchelonFoundry.Aegis.Core` 1.0.0) and cannot be restored from the public
feed. To build the review edition, supply that id from a local package source
(`-p:RestoreAdditionalProjectSources=<dir>`). The authors' parity check did
so by repacking the public package under the alias id, changing only the
package id and leaving the binaries untouched. The faithful edition restores
it from the public feed as is.

`r2/SHA256SUMS` lists the checksums the R2 evaluator verified; they refer to
the patches before the review rename and therefore match the faithful
edition, not the renamed `r2/*.patch` in this bundle. `manifest.json`
records each patch's pre-rename sha256 (`source_sha256`).
<!-- /mode -->
<!-- mode:faithful -->
Unredacted baseline files can be compared with the original commit by Git blob
id (`git hash-object <file>` equals the `git_blob_at_baseline` column of
`baseline/FILES.tsv`).
<!-- /mode -->

The bundle was produced deterministically (sorted entries, fixed timestamps
and permissions) from inputs pinned to exact commits; `manifest.json` lists
the origin of every entry.

## Anonymization

No file names a person: personal names, e-mail addresses, account names,
account-scoped repository or site URLs, and agent-session identifiers and
links are removed.

* **Person identifiers** are replaced everywhere by neutral placeholders
  (`Anonymous Owner`, `owner`, `anonymous-owner`,
  `anonymous@example.invalid`). In the baseline they occur in documentation,
  packaging metadata, installer scripts, one installer default argument, and
  a person's first name used as an opaque actor id in test fixtures (replaced
  by `owner`, which has the same length). `baseline/FILES.tsv` marks every
  redacted file. The arm patches contain no person identifiers.
* **Agent-session identifiers** and transcript/tool UUIDs in the study records
  are replaced by stable pseudonyms (`agent-session-NNN`, `uuid-NNN`) that are
  consistent across all files, so cross-references between the session
  ledger, the metrics files and the run logs still resolve. Session links are
  replaced by `<agent-session-url-redacted>`. Commit trailer lines that name a
  person or a session are removed from records.
* **Datasets** drop any field named `internal_ref`.
* **Patches** are squashed `git diff` output with no commit metadata.
<!-- mode:review -->
* **Product, organisation and sibling-tool names** are replaced by
  same-length aliases in contents and paths, and **Git object ids** by
  same-length pseudonyms. Equal lengths keep byte offsets, column padding and
  patch hunks intact. One schema identifier shared with an external
  implementation is left unchanged, because a test pins a hash computed over
  it.

Retained: the three-letter project prefix used in namespaces, assembly names
and record ids (a generic acronym), and the names of the AI tools, which the
paper discloses. Agent-written prose and code are otherwise verbatim.
<!-- /mode -->
<!-- mode:faithful -->

Retained in this edition: product, package and organisation names (embedded
in code, CLI names, work-item ids and the patches), commit SHAs, and the
names of the AI tools, which the paper discloses.
<!-- /mode -->

The redactions are behaviour-neutral: the per-test results listed under
"Checking the bundle" are the same with and without them.

## Baseline policy

`baseline/source/` is the baseline commit's tree minus five subtrees
(listed with reasons in `baseline/BASELINE.txt`): the tool's own operating
state and telemetry, CI workflows and actions, the research records, and
owner-supplied input documents. These are dense with person and session
identifiers and are not needed to read, apply, build or test the arm
patches; the study records relevant to the paper are shipped separately,
sanitized, under `a021/` and `r2/`. Everything else is the baseline content
with only the changes recorded per file in `baseline/FILES.tsv`.
