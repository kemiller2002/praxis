# Operating Praxis remote execution

This page is for maintainers who install, secure, upgrade and troubleshoot
remote execution in a repository. Remote execution is what lets agents
without a local .NET or Praxis runtime be governed by Praxis.

The agent side is described in
[`remote-agent-contract.md`](remote-agent-contract.md), and the protocol in
[`remote-protocol.md`](remote-protocol.md). The design and its rationale are
in `DF-ROS-2026-A041`.

## What is involved

| Part | Where | Role |
|---|---|---|
| `praxis remote execute` / `classify` / `describe` | the Praxis binary | Decides, runs, and journals requests. This part is the authority. |
| `.github/workflows/praxis-remote.yml` | your repository | Thin GitHub Actions host. It holds no Praxis rules. |
| `.github/actions/praxis-remote` | your repository | Runs the request, persists the state, reports the outcome. |
| `.github/actions/praxis-setup` + `scripts/praxis-bootstrap.sh` | your repository | Installs the pinned Praxis release and verifies it. |
| `scripts/praxis-remote-persist.sh` | your repository | Commits exactly the state Praxis reports, as the executor. |
| `.echelon/toolchain.json` | your repository | Pins the Praxis version. |
| `ros.json` → `remote.capabilities` | your repository | Opts the repository in to remote mutation. |

No server, database, cloud infrastructure, GitHub App, or secret is
required.

## Installation

**Automated.** Run `scripts/praxis-remote-enable.sh --version X.Y.Z` from
a clean, up-to-date `main`. It takes three phases:

1. Release. It bumps the version and pushes. It waits for the native
   release workflow, then verifies the release's checksum, its
   build-provenance attestation, and that it contains `remote execute`.
   This phase runs only in the Praxis repository. Pass `--skip-release`
   elsewhere, and whenever the version has already been published.
2. Pin and opt in. It pins the release, sets `remote.capabilities`
   (`--capabilities`, default `read,mutate`), and makes sure the journal
   is listed in `ignoredPaths`.
3. Smoke test. It dispatches a `praxis.describe` request and prints the
   structured result.

Each change is made under its own mechanical Praxis work item, attributed
to the person running the script. `--via-pr` lands changes through pull
requests instead of pushing to `main`. `--dry-run` shows every step
without changing anything.

**Manual.** The steps below do the same by hand.


1. **Pin a Praxis release that has remote execution and attestations.** Set
   `praxis` in `.echelon/toolchain.json` to an exact `MAJOR.MINOR.PATCH`.
   The release must contain `praxis remote execute`, and its native assets
   must carry build-provenance attestations. Releases up to and including
   3.4.0 have neither.
2. **Copy the adapter files into the repository.** From the Praxis
   repository, at the same version you pinned, copy:
   - `.github/workflows/praxis-remote.yml`
   - `.github/actions/praxis-remote/`
   - `.github/actions/praxis-setup/`
   - `scripts/praxis-bootstrap.sh`
   - `scripts/praxis-remote-persist.sh`

   Commit them to the **default branch**. GitHub only dispatches workflows
   that exist there.
3. **Keep the journal out of attribution.** Make sure `ros.json`
   `workProtocol.ignoredPaths` contains `.ros/remote/**`. The request
   journal is Praxis bookkeeping. Scaffolds from this version on include
   it.
4. **Opt in to what remote requests may do.** Remote reads work as soon as
   the workflow is installed. Mutation is opt-in, and you can grant it by
   class:

   ```json
   { "remote": { "capabilities": ["read", "mutate", "complete", "reconcile"] } }
   ```

   - `mutate` covers start, resume, block, telemetry and steps.
   - `complete` covers completing work items.
   - `reconcile` covers post-hoc attribution under #80.
   - Leave out any class you do not want remote callers to use.
5. **Verify.** Run `praxis remote describe` locally. The output should list
   the transport, your capabilities, and the operations. Then dispatch a
   `praxis.describe` request, as the agent contract shows.

Automated installation by Conditor is planned as PRAXIS-REMOTE-12.

## Permissions

The workflow starts with `permissions: {}`, and each job asks only for what
it needs:

| Job | Runs for | Permissions |
|---|---|---|
| `read` | Every request. Praxis classifies the request here, and reads are executed here. | `contents: read`, `attestations: read` |
| `write` | Mutating requests persisted by push | `contents: write`, `attestations: read` |
| `write-pull-request` | Mutating requests with `persistence: pull-request` | `contents: write`, `attestations: read`, `pull-requests: write` |

**Who can start it.** Only `workflow_dispatch` and `workflow_call` can start
the workflow. Dispatching requires write access to the repository. There is
no `pull_request` trigger, so forks can neither run the workflow nor obtain
its token.

**Protected branches.** If the target branch requires reviews or status
checks, `GITHUB_TOKEN` usually cannot push to it. Dispatch with
`persistence: pull-request` in that case. Praxis state is then proposed on
a branch named `praxis/remote/<digest>`, and the result reports the pull
request. Nothing is persisted to the target branch until the pull request
is merged.

A retry with the same request ID, for example after the pull request could
not be opened, finds that branch already pushed. When the branch's tip
carries this request's `Praxis-Request-Id` trailer, the adapter keeps it:
the earlier attempt's state is the request's state. It reports the open pull
request, or opens one, with `reused: true` in the adapter result, and never
pushes a second state for the same request. A branch whose tip names another
request, or none, is `concurrency-conflict` and is left untouched.

## Version pinning and upgrades

**What the bootstrap does.** The bootstrap installs exactly the pinned
version and fails explicitly in each of these cases, with a distinct exit
code:

- the pin is missing or malformed (3);
- the release or asset is unavailable (4);
- the checksum or attestation fails verification (5);
- the binary reports a different version (6).

It never falls forward to another version. Cached bundles are re-verified
on every run.

**Upgrading.**

1. Bump the pin in `.echelon/toolchain.json`.
2. Copy the adapter files from the same release.
3. Merge.

Old journal entries stay readable, because they record their protocol
version and are never rewritten. A newer executor accepts older `1.x`
requests. A newer request sent to an older executor is refused as
`unsupported-protocol`, and the refusal lists the supported versions.

**Releases for pre-attestation versions.** Only if you must pin such a
release, set `attestation: skip` on `praxis-setup` in your copy of the
action. That choice is explicit, and the run reports it.

## Invocation and results

See the [agent contract](remote-agent-contract.md). In short, dispatch
`praxis-remote.yml` on the target branch, with a `request` (JSON) and its
`request_id`. You get results from three places:

- **The journal** (durable). A mutation writes
  `.ros/remote/requests/<requestId>.json` into the same commit as the
  state it describes.
- **The run.** It is named `praxis remote <request_id>`. Its summary holds
  the response and the adapter result. The executing job's log prints the
  same document in a `praxis.remote response` group, with workflow
  commands suspended while it prints, so request-derived text cannot act
  as a workflow command.
- **The artifact.** It is named `praxis-remote-response`, is kept for 30
  days, and serves as supporting evidence only.

## Concurrency and idempotency

- **One mutation per ref at a time.** Write jobs are serialized per ref by
  a concurrency group. This is a convenience only.
- **The real protection.** Praxis refuses a request whose `expectedSha` is
  not the checked-out commit (`stale-ref`). The adapter pushes without
  force, so a ref that moved first makes the push fail with
  `concurrency-conflict`, and nothing is persisted.
- **Retries.** A retry with the same `requestId` replays the recorded
  outcome. This works even after the requester's own commit moved the
  branch. The same ID with a different payload is refused
  (`idempotency-conflict`).
- **Uncertain outcomes.** A run that timed out, was cancelled, or lost its
  result never leaves state marked done. Either the journal entry is on
  the branch, in which case the request happened, or it is not, in which
  case it did not. `request.status` answers from the repository.

## Security model

- **Explicit operations only.** The request selects an allow-listed Praxis
  operation with typed arguments. No field can carry a command.
- **Untrusted input.** Identifiers, refs, SHAs, paths and text are
  validated, and values that start with `-` are refused. The request
  reaches Praxis through an environment variable and a file, never through
  shell interpolation.
- **Secrets.**
  - Requests that contain credential-shaped values are refused and never
    echoed.
  - Praxis runs each command in a derived environment. Tokens, provider
    keys and host identity markers in the runner's environment never reach
    the command, its telemetry, or its diagnostics.
  - No repository secrets are used.
- **Identity.**
  - The requester's actor is recorded as *asserted*.
  - The runner is recorded as the *observed* executor, and the GitHub
    account that triggered the run as the principal.
  - A runner never becomes the author of an agent's work.
  - A request cannot continue or record into another actor's execution.
  - Commits are authored by the executor, with trailers that name the
    asserted requester.
- **Least privilege.** Grants are the job's capabilities intersected with
  `remote.capabilities`. Holding a request document confers no authority.
- **Supply chain.**
  - The bootstrap verifies SHA-256 and GitHub build-provenance attestation.
  - Release assets for a published version are never replaced.
  - Third-party actions are pinned to commit SHAs.

Identity recorded this way is provenance, not authentication.

## Failure and retry

| Code | Meaning | What to do |
|---|---|---|
| `invalid-request`, `secret-detected`, `unsupported-operation` | The request is wrong. | Fix it. |
| `unsupported-protocol` | The request's version is not supported here. | Use a version `praxis.describe` lists. |
| `unauthorized` | The job's grant or the repository's opt-in does not allow the operation. | Opt in through `remote.capabilities`, if that is intended. |
| `idempotency-conflict` | The request ID was reused for a different intent. | Use a new request ID. |
| `stale-ref` | The branch moved, or the tree is dirty. | Re-read the branch and form a new request. |
| `domain-rejected` | Praxis refused the transition. | Read `failure.message`. It is the same refusal the local CLI gives. |
| `validation-failed` | The mutation would have introduced validation errors. | Fix the repository or the request. Nothing was kept. |
| `concurrency-conflict` | The push lost a race. | Re-read the branch and form a new request. |
| `bootstrap-failed` | The pinned Praxis could not be installed. | See Troubleshooting. |
| `internal` | An executor defect. Praxis reported it, and nothing was kept. | Retry with the same request ID. If it persists, report it with the run's artifact. |
| `timeout`, `cancelled`, `transport-failed`, `rate-limited`, `repository-write-failed` | The outcome is unconfirmed. | Retry with the same request ID, or ask `request.status`. |

`rate-limited` means GitHub throttled the push or the pull-request creation.
The adapter reports it when Git or `gh` relays HTTP 429 or a primary or
secondary API rate-limit message. It is a transient condition of the
transport, not a judgement about the request, so wait before retrying with
the same request ID. Any other refused push or pull request is
`repository-write-failed`. A push that lost a race stays
`concurrency-conflict`.

## Reconciliation

Some work is committed while no work item was active: the agent could not
reach Praxis, or forgot to begin. Attribute such work with `work.reconcile`
remotely, or with `praxis work reconcile` locally, naming the commits.

- The attribution is recorded as `post-hoc`, with the Git evidence.
- The original author and committer are preserved.
- The requester is recorded as the reconciliation actor.
- Nothing is touched or recommitted.
- Duplicates are idempotent. Conflicts with another work item are refused.

See "Post-hoc attribution reconciliation" in
[`work-protocol.md`](work-protocol.md).

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| The workflow cannot be dispatched, or is not found | The workflow is not on the default branch. |
| Bootstrap exit 3 | `.echelon/toolchain.json` is missing or does not pin an exact `praxis` version. |
| Bootstrap exit 4 | The pinned version or its asset does not exist. Check the release. |
| Bootstrap exit 5 with "attestation" | The pinned release predates attestations, or `gh` could not verify them. Pin a newer release, or skip attestation explicitly. |
| Bootstrap exit 6 | The release asset reports another version. The release is inconsistent, so do not use it. |
| Every mutation is `unauthorized` | `remote.capabilities` is absent from `ros.json`. Only reads are allowed by default. |
| Every mutation is `stale-ref` | The request's `expectedSha` is not the head of the dispatched ref. Make sure the dispatch `ref` matches `repository.ref`. |
| The push fails on a protected branch | Use `persistence: pull-request`. |
| `rate-limited` | GitHub throttled the run's token. Wait, then retry the same request ID; do not mint a new one. |
| `unknown` or `timeout` outcomes | Ask `request.status` with the same ID before doing anything else. |
