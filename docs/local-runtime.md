# Local coordinator/worker runtime (`praxis local`)

Work: [GH-234](https://github.com/kemiller2002/praxis/issues/234),
[GH-235](https://github.com/kemiller2002/praxis/issues/235).
Decision: [DF-ROS-2026-A070](../research/decisions/DF-ROS-2026-A070--concrete-local-coordinator-worker-runtime.md).

`praxis local run` starts a real worker for an operator-signed packet, has the
controller independently build, test and check the worker's commit, integrates
an accepted candidate into a target branch with an expected-head update, and
completes the native work item. Everything runs on one machine; there is no
service, daemon or hosted coordinator.

| Role | Who | What they do |
| --- | --- | --- |
| Operator | a person | Owns the host directory, the signing key and the host configuration; reviews and signs grants and packets. |
| Coordinator | an agent or person | Plans work items and groups, proposes packets (`praxis local packet propose`). |
| Worker | an installed agent CLI or local model | Edits files in its own checkout; its output is a claim. |
| Controller | `praxis local run` | Verifies signatures and native state, dispatches, runs validators, stages, integrates and completes. |
| Validators | operator-selected commands | Build, tests (TRX), architecture, scope and per-requirement acceptance. |

The controller never accepts a worker's claimed pass. Workers can compile for
diagnostics, but only independently observed checks authorize integration and
completion. See [worker checklists](worker-checklists.md).

## What this is not

- **Not OS containment.** The portable profile does not stop a worker running
  as the same user from reading or changing host files. Equivalent isolation
  is deferred. Use separate OS accounts and the `unix-private` access profile
  where you need more than cooperative protection.
- **Not a signing service.** Praxis never generates, stores or uses private
  keys. It writes the exact bytes to sign and verifies your signature.
- **Not autonomous approval.** Each packet runs only with an operator signature
  over its exact contents, under a current grant.

## Prerequisites

- Praxis from this repository (.NET 10: `dotnet build Praxis.slnx -c Release`
  then `./praxis`), or an installed native release containing `praxis local`.
- Git 2.38 or newer (tree-only merge staging).
- An ECDSA P-256 signer: `openssl` works on Linux, macOS and Windows (Git for
  Windows ships it), or a hardware/key-management tool producing DER or
  64-byte P1363 signatures.
- A worker backend, one of:
  - an installed, authenticated agent CLI that edits files in its working
    directory (`--agent-command`, prompt on stdin or as `{prompt}`);
  - a local Ollama server (`--ollama-model`);
  - an unauthenticated local OpenAI-compatible server such as llama.cpp,
    vLLM or LM Studio (`--openai-model`). No API key is read.

## 1. Repository policy

In the target repository's `ros.json`, name the work types that may complete
only through this runtime:

```json
"workProtocol": { "workerChecklists": { "delegatedTypes": ["delegated-task"] } }
```

`praxis local run` refuses members of any other type, and standalone
`praxis work complete` (and runtime-free envelope reconciliation) refuse these
types, so delegated work cannot bypass its checklist. Without the setting the
runtime refuses to run and standalone completion keeps its previous policy.

## 2. Host directory and configuration

Create a host directory **outside** the repository with seven subdirectories,
mode `0700` for the `unix-private` profile:

```bash
HOST=$HOME/.praxis-host/myrepo
mkdir -p $HOST/{policy,journal,spool,submissions,integration,evidence,checkouts}
chmod 700 $HOST $HOST/*
```

Write `$HOST/config.json` (`praxis.local-host/1`); see the
[example](examples/local-host.json). Every executable and tool-closure file
is pinned by `sha256:`. Validator commands may use only `{candidate}` (a fresh
checkout of the exact candidate) and, for test validators, `{trx}` (a fresh TRX
path); worker commands may use only `{worktree}`. Any other brace is refused.
Changing any pinned input changes the acceptance pin, so packets signed
earlier stop verifying.

Required validators are `build`, `tests` (must produce TRX with a positive
executed count), `architecture` and `scope`; `acceptanceValidators` are what
packets name per requirement. `completionEvidence` maps the repository's
native evidence types to `member-changes` or `acceptance-artifacts` in the
integrated tree. Workers' `maxOutputBytes` is at most 65536.

A worker using the bundled bridge with a local OpenAI-compatible server:

```json
{ "adapterId": "bridge-qwen-a", "workerId": "qwen-worker-a",
  "roles": ["implementation"], "operatingSystems": ["linux", "macos", "windows"],
  "command": { "executable": "/usr/local/share/dotnet/dotnet", "sha256": "sha256:...",
    "arguments": ["/opt/praxis/praxis.dll", "local", "worker-bridge", "--git", "/usr/bin/git",
                  "--openai-model", "qwen2.5-coder-1.5b", "--openai-endpoint", "http://127.0.0.1:8080"],
    "timeoutMilliseconds": 600000, "maxOutputBytes": 65536,
    "dependencies": { "/opt/praxis/praxis.dll": "sha256:..." } } }
```

For an installed agent CLI, replace the backend flags with
`--agent-command /path/to/agent --agent-arg ...` (for example the agent's
non-interactive "edit files in the current directory" mode). The bridge
commits only delegated paths as the worker identity, refuses multi-member
packets, and reports `failed` when the backend changes nothing or touches a
path outside the delegation.

## 3. Operator key and host policy

```bash
openssl ecparam -name prime256v1 -genkey -noout -out operator.key   # keep private
openssl ec -in operator.key -pubout -out operator.pub
./praxis local operator init --host-config $HOST/config.json --public-key operator.pub \
  --key-id op-1 --approver "Operator Name" --revision policy-1 --declare-qualified-release
```

`--declare-qualified-release` is the operator's own statement that this
Praxis release and host are qualified for local delegation; nothing infers it.

## 4. Native preparation (coordinator)

```bash
./praxis add "Implement R-A" --id WI-A
./praxis work backlog-transition --id WI-A --action ready --occurred-at "$(date -u +%FT%T.000Z)"
./praxis work group create --group GROUP-APP-001 --member WI-A --occurred-at "$(date -u +%FT%T.000Z)"
./praxis work begin --id WI-COORD --occurred-at "$(date -u +%FT%T.000Z)"     # coordinator's own item
git commit -am "Delegation source"; SOURCE=$(git rev-parse HEAD)
```

After the grant (step 5), begin the member as a delegated type and create the
worker's child execution under the coordinator:

```bash
./praxis plan execute-group GROUP-APP-001 --occurred-at "$(date -u +%FT%T.000Z)" --member WI-A --type delegated-task
./praxis plan execute-group GROUP-APP-001 --occurred-at "$(date -u +%FT%T.000Z)"   # closes the group execution
./praxis execution start --work-item WI-COORD --role implementation --baseline $SOURCE \
  --parent <coordinator execution> --agent qwen-worker-a --actor-kind agent --json
git update-ref refs/heads/praxis-integration $SOURCE                              # integration target
```

The member's execution baseline must be the grant's source commit.

## 5. Grant a cohort (operator)

```bash
./praxis local operator grant-draft --host-config $HOST/config.json --key-id op-1 --approver "Operator Name" \
  --group GROUP-APP-001 --cohort C-A --source $SOURCE --manifest docs/manifest.json --blueprint docs/blueprint.json \
  --member WI-A=docs/requirements.md#R-A --decision DEC-A --draft grant-A.json --payload grant-A.bin
openssl dgst -sha256 -sign operator.key -out grant-A.sig grant-A.bin
./praxis local operator grant --host-config $HOST/config.json --draft grant-A.json --signature grant-A.sig --revision policy-2
```

Manifest and blueprint digests are read from the committed source. Every
policy change advances the revision; packets signed under an older revision
no longer verify.

## 6. Propose, review and delegate a packet

```bash
./praxis local packet propose --host-config $HOST/config.json --group GROUP-APP-001 --worker qwen-worker-a \
  --role implementation --dispatch D-A --attempt 1 --parent <coordinator execution> --child <worker execution> \
  --member WI-A=<member execution> --decision DEC-A --allow src/text_tools.py \
  --acceptance OBL-A=WI-A:accept-slugify [--prerequisite WI-0] --timeout-seconds 600 --out A.packet.json
./praxis local operator review --host-config $HOST/config.json --packet A.packet.json \
  --key-id op-1 --approver "Operator Name" --payload A.bin            # prints the full assignment
openssl dgst -sha256 -sign operator.key -out A.sig A.bin
./praxis local operator delegate --host-config $HOST/config.json --packet A.packet.json \
  --key-id op-1 --approver "Operator Name" --signature A.sig --out A.delegation.json
```

Source, cohort, receipt, revision, requirement keys, prerequisite digests and
acceptance pins are filled from protected state, never from the proposer.

## 7. Run

```bash
./praxis local run --host-config $HOST/config.json \
  --dispatch A.packet.json=A.delegation.json [--dispatch B.packet.json=B.delegation.json] \
  --target refs/heads/praxis-integration --json
```

Dispatches run in order and stop at the first refusal. Exit `0` means every
member completed natively; `3` means refused (the message names the failing
check); `2` means invalid arguments. `praxis local status --host-config FILE
--dispatch ID` shows the dispatch and integration journals. The worker's child
execution is closed with the integrated candidate. Merge or push the target
branch with your normal review process; the runtime never touches your
checked-out branch or working tree.

## Recovery

| Situation | What `praxis local run` does on the same packet/delegation |
| --- | --- |
| No journal | Dispatches the worker once. |
| Worker exited and archived, later step refused | Resumes at acceptance; never relaunches the worker. |
| Launch uncertain, reserved or running | Refuses; reconcile the dispatch journal before any new attempt. |
| Integration prepared, target unchanged | Re-verifies everything, then attempts the expected-head update. |
| Target already at the candidate | Records the observation; no second update. |
| Target moved elsewhere | Refuses; never rewrites the target. |
| Completion interrupted after telemetry finalization | Resumes completion with every check repeated, if the delegation is still valid. |
| Delegation expired before completion | Refuses. Use a new attempt or record an explicit abandonment. |

A failed or refused candidate leaves the member active and the target
unchanged. A retry is a new packet (new dispatch/attempt ID) and a new
signature. Validator evidence is archived in the host `evidence` directory;
worker checkouts stay under `checkouts` for inspection.

## Diagnostics

- `REFUSED dispatch D-A stopped: <row>:checklist-check-failed`: read the
  validator evidence JSON in `$HOST/evidence` (stdout/stderr, outcome).
- `native local assignment refused`: the member, group, envelope or telemetry
  state differs from the packet; check `praxis work context`.
- `local delegation ...`: signature, revision, grant or time window differs;
  re-propose and re-sign under the current policy.
- `execution lifecycle lease unavailable`: another native writer or a host
  scope holds `work-protocol`/`work-groups`; retry when it finishes.

## Live qualification (2026-10-12)

A coordinator/worker pilot ran in an ephemeral Linux cloud container with a
real local model, using the CLI published from commit `754d387e`. It is a
functional qualification of the runtime, not a model-quality benchmark, and
not OS containment (portable process mechanism, one OS account).

| Part | What ran |
| --- | --- |
| Coordinator | The Claude Code session doing this work (native identity `anthropic/claude-code`): planned four work items and groups, began members as `delegated-task`, created worker child executions, proposed packets. |
| Operator | `openssl` P-256 key generated in the container; grants and delegations signed with `openssl dgst -sha256 -sign` (DER); Praxis verified each signature. The executor acted as operator for this pilot; in production a person holds the key. |
| Workers | `praxis local worker-bridge --openai-model` against a local llama.cpp server (`llama-cpp-python` 0.3.36) serving Qwen2.5-Coder-1.5B-Instruct Q4_K_M (`sha256:cc324af070c2ecbfd324a30884d2f951a7ff756aba85cb811a6ec436933bb046`), CPU only. Four worker identities, `qwen-worker-a` to `-d`. |
| Validators | Host-owned Python tools outside the repository, pinned by digest: in-memory compile check, unit tests writing TRX, an import allow-list architecture rule, a top-level layout scope rule, and host-owned acceptance tests per requirement. `unix-private` file access profile. |
| Target | A Praxis-initialized Python project; integration ref `refs/heads/praxis-integration`. |

Results on the final build:

| Dispatch | Worker output | Outcome | Wall time |
| --- | --- | --- | --- |
| D-PILOT-A (slugify) | real model commit | all checks passed; fast-forward; WI-A complete | 56 s |
| D-PILOT-B (word count, prerequisite WI-A) | real model commit from the original source | merge-staged onto A, rechecked; WI-B complete | 52 s |
| D-PILOT-C (Roman numerals) | real model commit | merge-staged; 2 acceptance tests incl. range errors passed; WI-C complete | 73 s |
| D-PILOT-D (palindrome) | real model commit (correct code) | operator acceptance test changed after signing: **refused** (stale validator pin); target unchanged; WI-D stayed active | 11 s |
| D-PILOT-D, rerun after restoring the signed test | archived exit | resumed at acceptance without relaunching the worker; WI-D complete | 45 s |

35 validator evidence files were archived; every run that authorized
integration executed tests (positive TRX counts). Each member completed
natively with `implementation` and `tests` evidence from the integrated tree,
each worker's child execution closed with its integrated candidate, and
`praxis validate` passed in the pilot repository.

An earlier run of the same pilot, on a build whose bridge sent the whole
requirements document, produced a genuine model failure: the model copied the
requirement text into `src/text_tools.py`. Compile, architecture, unit-test and
acceptance checks all failed independently and the target did not move. That
led to the bridge sending only the anchored requirement section.

The member envelopes record the controller's checkout HEAD as their candidate,
as the standalone completion mirror does; the integrated commit is recorded on
each worker's child execution and in the integration journal.

To reproduce with your own backend, follow steps 1 to 7 above; the pilot's
validator tools are ordinary scripts and any pinned commands that produce the
same outcomes (and TRX for tests) can replace them.
