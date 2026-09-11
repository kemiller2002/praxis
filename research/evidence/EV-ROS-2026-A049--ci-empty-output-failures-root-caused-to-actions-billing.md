---
id: EV-ROS-2026-A049
title: The session-long "empty-output" CI failure pattern is GitHub Actions billing, not repository or workflow defect
status: accepted
version: 1.0.0
owners:
  - repository-governance
created: 2026-09-11
updated: 2026-09-11
research_area: repository-operating-system
evidence_type: primary
supports: []
related_documents:
  - DF-ROS-2026-A033
  - .github/workflows/ros-validation.yml
  - .github/workflows/publish.yml
supersedes: []
superseded_by: []
tags: [ci, infrastructure, github-actions, billing, sde]
confidence: high
---

# Evidence summary

Across every PR merged in this session's work (`#38`-`#44`), the GitHub
Actions `validate` check (`.github/workflows/ros-validation.yml`) failed
with an identical, distinctive signature: `conclusion: "failure"`,
`output.summary`/`output.text`/`output.title` all empty strings, and a
completion time of roughly 2-6 seconds regardless of the diff's actual
size. It reproduced identically on `main`'s own tip after every merge
(e.g. runs `34551851027`/`34551851070` on commit `3658ebd`), not only on
open PRs, which was the basis for repeatedly standing down on it in PR
comments and decision-record prose as "a known CI-infra issue, not this
diff's" -- a reasonable, correctly non-blocking judgment call at the time,
but one made *without* a confirmed root cause. `DF-ROS-2026-A033`'s own PR
(`#43`) and the `#44` version-bump PR both carried this same unresolved
characterization forward.

That gap is now closed. The same signature appeared on a second, unrelated
workflow (`Publish to npm`, `.github/workflows/publish.yml`) after merging
`#44` (the `2.0.0` version bump): run `34570090987` failed twice in direct
succession (attempt 1: started `06:28:45Z`, completed `06:28:50Z`, job
`103170169137`; attempt 2, a deliberate one-time flake-confirmation re-run:
started `06:36:40Z`, completed `06:36:46Z`, job `103171867784`), both times
with the identical empty-output signature and, on both, `get_job_logs`
returned an HTTP 404 rather than any log content at all -- consistent with
a job that never actually started (nothing was ever written to log). The
user then checked GitHub's own UI directly and reported its message
verbatim: *"The job was not started because recent account payments have
failed or your spending limit needs to be increased. Please check the
'Billing & plans' section in your settings."*

This is the root cause: GitHub rejects the job before allocating a runner
whenever the account's Actions billing is in a failed-payment or
over-spending-limit state, which is exactly what a 2-6 second
"completion" with no logs and no check output looks like from the API's
side -- there is nothing to log because nothing ran. It explains why the
failure was schedule-independent, diff-independent, and workflow-independent:
it is an account-level gate applied before any workflow-specific step,
including checkout.

After the user resolved the billing issue, the same run (`34570090987`,
attempt 3) was re-triggered and transitioned to `status: "in_progress"`
with a real `run_started_at` timestamp (`10:34:47Z`) -- the first
observed case all session of a runner actually being allocated for this
repository. This is the confirming half of the diagnosis: identical
trigger, identical workflow, only the account's billing state changed
between the failing attempts and this one.

# Method

Observational, not experimental: no code or workflow change was made to
produce or resolve this finding. Evidence is the GitHub Actions API's own
responses (`get_workflow_run`, `list_workflow_jobs`, `get_job_logs`,
`get_check_run`) collected in the ordinary course of driving PRs `#43`
and `#44` to green, plus the user's own report of GitHub's UI-only billing
message (not exposed by any API this session has access to).

# Findings

- The empty-output/no-logs/2-6-second signature is **not** a defect in
  this repository's workflows, in the code under test, or in this
  session's own changes. Every prior PR comment or decision-record note
  characterizing it as "a known CI-infra issue" was directionally
  correct (non-blocking, not the diff's fault) but incomplete (no
  confirmed cause).
- The actual cause is the GitHub account's Actions billing state
  (failed payment or exceeded spending limit), which gates job
  scheduling before any runner is allocated -- upstream of checkout,
  upstream of any workflow-specific step, and therefore identical in
  signature across every workflow in the repository.
- This is visible from GitHub's own UI (the billing message) but not
  from the Actions API surface this session has access to (`get_check_run`,
  `get_job_logs`) -- both report the failure with zero diagnostic content,
  which is itself a useful negative signal: an instant, log-free failure
  on a job that should take minutes is a stronger indicator of a
  scheduling-gate rejection than of an in-workflow defect.
- Resolving the account's billing state was sufficient, on its own, to
  let the identical, unmodified workflow run proceed to a real
  `in_progress` state on a re-run of the same run id.

# Consequences

Future instances of this exact signature (near-instant completion, empty
check output, 404 on log fetch, reproducing identically across unrelated
workflows and across `main` and every open PR) should be triaged as a
likely account-level Actions billing gate first, before assuming a
workflow or repository defect -- checking GitHub's own Billing & plans UI
directly is faster and more conclusive than iterating on workflow YAML or
re-running blindly. No code, workflow, or documentation change in this
repository is warranted by this finding; it corrects the record, not the
implementation.

# Reversibility and validation

Purely observational; there is nothing to revert. Validation is the
state transition itself: the same run id (`34570090987`) moved from
`conclusion: "failure"` (twice, pre-fix) to `status: "in_progress"`
(post-fix) with no change to the workflow file, the branch, or the
triggering commit in between -- isolating the billing state as the only
variable that changed.
