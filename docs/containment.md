# Host containment for local workers

Work: [#231](https://github.com/kemiller2002/praxis/issues/231),
[#235](https://github.com/kemiller2002/praxis/issues/235) (phase 6), local item `WI-0092` (re-identified from WI-0089 after an ID collision).
Decision: [DF-ROS-2026-A073](../research/decisions/DF-ROS-2026-A073--probe-qualified-linux-worker-containment.md).

A Git worktree, a prompt, a provider permission mode or a post-hoc diff is
**not** containment. Praxis treats a local worker as contained only when the
host launched it through an OS mechanism *and* a real probe suite, run on this
host through that same launch path, observed every boundary holding. Until
then every containment dimension stays `unknown` (PRX-SEC-012), and a worker
whose host configuration requires containment does not start.

## What exists

| Piece | Where | Effects |
|---|---|---|
| Probe outcomes, qualification record, profile derivation | `Praxis.Domain.Work.LocalContainment` | pure |
| `praxis.containment-evidence/1` record (render/read) | `Praxis.Contracts.Work.LocalContainmentJson` | pure |
| Linux launch construction, path rules, launch gate | `Praxis.Application.Work.LinuxContainment` | pure |
| Tool digests, platform facts, staging roots, evidence files | `Praxis.Infrastructure.Work.LinuxContainmentHost` | host |
| Probe suite and gate against stored evidence | `Praxis.Infrastructure.Work.LocalContainmentQualification` | host |
| Host-configuration binding, runtime gate | `Praxis.Infrastructure.Planning.LocalHostContainment` | host |
| `praxis local containment qualify\|status` | `Praxis.Cli.LocalContainmentCommands` | none (prints) |

The evidence record reuses the existing `praxis.containment-evidence/1` schema
and `ContainmentProfile` (PRX-SEC-001/010/011/013): `schema`, `host` and
`restrictions` are exactly what `ExecutionJson.readContainmentEvidence` reads,
so a qualification file can also be passed to `execution start
--containment-evidence`. Additive fields carry `qualified`, `profile`,
`profileDigest`, `mechanism`, `platform` (OS, kernel, architecture), `tools`
(path and SHA-256 of every executed tool), every `probes[]` entry
(`id`, `dimension`, `expected`, `observed`, `result`, `detail`),
`unsupportedControls` and `qualifiedAt`. Reading a record recomputes the
restrictions from the probes and refuses any disagreement, so an edited
`status` cannot outrank its own probes.

## Per-OS qualification

| | Linux (`linux-userns-pivot/1`) | macOS | Windows |
|---|---|---|---|
| Mechanism | `unshare` user, net, mount, pid, ipc and uts namespaces with `--kill-child`; a fixed setup script pivots into a minimal root; `prlimit`; `setpriv` | none implemented | none implemented |
| Qualification | real probes; qualified only when all 35 pass | `unsupported`: every dimension `unavailable` | `unsupported`: every dimension `unavailable` |
| Contained launch | only with current, matching, fully passing evidence | refused | refused |
| CI (three-OS portability job) | runs the probes where an independent `unshare` precheck succeeds; otherwise asserts refusal | asserts refusal | asserts refusal |

No parity is claimed. macOS (`sandbox-exec` is deprecated and unprobed here)
and Windows (AppContainer/job objects) need their own mechanism and probe
implementation before they can report anything but `unavailable`.

### Linux boundaries

| Boundary | Permitted | Denied (probed) |
|---|---|---|
| Filesystem | write in the provisioned worker directory; a private size-limited `/tmp`; read the configured read-only toolchain binds | writes to the repository, policy and signer files, journal, spool, submissions, evidence, host-configuration directory, sibling worktrees, host `/tmp`, `/usr` (even after a remount attempt), and through a symlink planted in the worker directory |
| Credential / host state | only the read-only binds (default: merged-`/usr` toolchain plus `/etc/passwd`, `group`, `nsswitch.conf`, `hosts`, `ld.so.cache`, `localtime`, `alternatives`, `ssl/certs`) | reading planted secrets in the policy, journal, spool, submissions, evidence and host-configuration roots, directly or through a symlink; visibility of the invoking user's credential stores (`~/.ssh`, `~/.config`, `~/.aws`, `~/.ccr`, ...), `/etc/shadow`, `/etc/gshadow`, `/etc/sudoers`, `/etc/ssh` |
| Network | loopback inside the empty network namespace | connecting to a host loopback listener (host observes no accept); external connect; any interface other than `lo` |
| Subprocess | spawning children inside the PID namespace | seeing a host sentinel process (from the worker and from a child); signalling it (host observes it alive); reading its `/proc/PID/environ`; setuid escalation (`su`); `mount -o remount,rw`; nested user namespaces (`max_user_namespaces=0`); `NoNewPrivs=1` and every capability set zero; `RLIMIT_NPROC`/`RLIMIT_NOFILE` as configured |
| Environment | only names in the profile's allowlist (default `PATH`, `LANG`, `HOME`, `TMPDIR`) plus shell-intrinsic `PWD`, `OLDPWD`, `SHLVL`, `_` | host variables (tokens, proxy settings); a spec carrying a non-allowlisted variable is refused before launch |

### Known unsupported controls (Linux)

- **seccomp**: no pinned seccomp tool is used, so system calls are not filtered.
- **Distinct host uid**: the worker runs as the invoking uid mapped to namespace
  root with no capabilities. Read isolation comes from mount visibility, not
  DAC. A kernel namespace escape would run as the invoking user (root on a
  root-run host).
- **cgroups**: no memory or CPU accounting; `prlimit` bounds processes, open
  files, file size and core dumps only.
- **Git from a linked worktree**: the worktree's common `.git` directory is in
  the sealed repository, so a contained worker cannot commit. A custom-code
  worker (#243) needs an output protocol (for example a patch or bundle in the
  worker directory) before it can return changes; that is not built here.
- **Host-dependent prerequisites**: unprivileged user namespaces must be
  allowed. Ubuntu 23.10+ restricts them through AppArmor
  (`kernel.apparmor_restrict_unprivileged_userns=1`); such a host qualifies as
  `unknown` and refuses until the operator changes that policy. `/proc` must be
  mountable inside the namespace.

## How the Linux launch is built

The host, never the worker, builds the whole command line
(`LinuxContainment.wrap`):

```
unshare --user --map-root-user --net --mount --pid --ipc --uts --fork --kill-child --
  sh -c SETUP praxis-contain STAGE WORKDIR READ-ONLY-PATHS SCRATCH-MB --
  prlimit --nproc=N --nofile=N --fsize=B --core=0 --
  setpriv --no-new-privs --inh-caps=-all --ambient-caps=-all --bounding-set=-all
          --securebits=+noroot,+noroot_locked,+no_setuid_fixup,+no_setuid_fixup_locked,+keep_caps_locked --
  WORKER-EXECUTABLE WORKER-ARGS...
```

`SETUP` is a constant of the program (`LinuxContainment.setupScript`), pinned
by `setupScriptDigest` inside the profile digest; paths reach it only as
arguments, with globbing disabled. It mounts a tmpfs on an empty host-created
staging directory, recreates symlinks and read-only, `nosuid,nodev` binds of
each configured path (missing ones are skipped), mounts a fresh `proc`, a
private `/tmp` and `null`/`zero`/`random`/`urandom`, binds the worker
directory read-write, `pivot_root`s, detaches the old root, remounts `/`
read-only, sets `max_user_namespaces` to 0 and the hostname, restores the
worker's `PATH` and `exec`s. Nothing of the host filesystem outside the binds
exists inside.

Path rules (pure, `LinuxContainment.checkBinds`/`wrap`): every path is
canonical POSIX; a read-only bind may not overlap a **sealed** path (host roots,
repository, checkout root, host-configuration and evidence directories,
credential stores, `/proc`, `/sys`, `/dev`, `/run`, `/boot`, `/etc/shadow`, ...)
and may not contain a **protected** path (`/`, `/etc`, `/var`, `/home`,
`/root`, `/tmp`, the invoking user's home), so `/etc/ssl/certs` or a pinned
`/opt/dotnet` may be bound but `/etc` or `$HOME` may not. The worker directory
must be strictly inside the checkout root and overlap nothing else; the
worker executable must lie inside a read-only bind; the worker's environment
must be within the allowlist; and the confined argv must fit the supervisor's
existing 64-argument bound. The worker's own executable pin is re-verified
before confinement because the supervisor then pins `unshare`.

## Probes

`praxis local containment qualify --host-config FILE [--json]` runs the suite
for real: it plants a random secret file in each host root and the
host-configuration directory, starts a host sentinel process carrying a secret
environment variable, opens a host loopback listener, and launches a fixed
probe script (`LocalContainmentQualification.probeScript`, also in the profile
digest) through `LinuxContainmentHost.confine` and the real
`LocalWorkerProcess` supervisor. The host then judges each probe:

- a probe **passes** only when the in-sandbox report exists *and* the host's
  own observation agrees (target files unchanged by digest, planted secrets
  absent from all output, listener never accepted, sentinel alive, inside file
  holds the nonce);
- a host-observed violation **fails** even without a report;
- anything unobserved is **unknown**, never a pass and never a failure.

A dimension is `enforced` only when it has probes and all pass, `unrestricted`
when any fails, `unknown` otherwise, and `unavailable` when no mechanism
exists. The evidence file is replaced on every run, so a failed run also
revokes admission. Exit code `0` means qualified, `3` unqualified or refused.

`praxis local containment status --host-config FILE [--json]` reports the
launch gate's decision without launching.

## Gating

A host configuration (`praxis.local-host/1`) opts in with:

```json
"containment": {
  "evidence": "/var/lib/praxis-host/containment.json",
  "readOnlyPaths": ["/usr", "/bin", "/lib", "/lib64", "/etc/passwd", "/opt/dotnet"],
  "environmentAllowlist": ["PATH", "LANG", "HOME", "TMPDIR", "DOTNET_ROOT"],
  "maxProcesses": 256, "maxOpenFiles": 1024, "maxFileBytes": 1073741824,
  "scratchMegabytes": 256, "maxEvidenceAgeMinutes": 1440
},
"workers": [ { "adapterId": "...", "workerId": "...", "containment": "required", ... } ]
```

Only `evidence` is required; the rest have the defaults shown in
`LocalHostConfiguration.defaultReadOnlyPaths` and the parser. The evidence
directory must exist outside the repository. A worker with `"containment":
"required"` and no `containment` block refuses at configuration load.

When `praxis local run` prepares such a worker, `LocalHostContainment.admit`
runs *before any checkout is provisioned*. It admits only if the current
platform is Linux, the stored record parses and covers exactly the current
probe ids, its host/profile, platform, kernel, profile digest (scripts,
settings, read-only paths, allowlist, limits) and tool digests all match this
host now, it is no older than `maxEvidenceAgeMinutes`, and every dimension is
`enforced`. Otherwise the launch is refused with the reason. Admitted specs
are wrapped by `LocalHostContainment.confine`; the journaled dispatcher's
existing exact-input and budget checks still apply, and staging directories
are removed after the run. Workers without the requirement are unchanged.

## Verification

Local, this host (Linux 6.18, root in a container, util-linux 2.39.3, no
bwrap/nsjail/firejail): `PRAXIS_TEST_FILTER="local containment:"` ran 9 tests,
9 passed, with the real-probe branch selected. Forcing
`PRAXIS_TEST_CONTAINMENT=unavailable` on the same host made the three
real-probe tests fail, showing they are not vacuous; removing `--net` from the
adapter made `qualify` report `network unrestricted` with three failed probes.
CI results are reported only after the jobs run.

## CI qualification

Qualification describes one machine, never a platform. A passing record
qualifies only the host whose kernel, architecture, profile digest and tool
digests it names. The launch gate already rejects evidence from any other
host. CI therefore reports qualification per runner, and a pass on one
runner never qualifies another.

| Where | What runs | What it proves | Reported as |
|---|---|---|---|
| GitHub-hosted runners (`praxis-validation.yml`, `local-worker-portability`, step "Host containment fail-closed gate") | `local containment:` tests | Fail-closed behavior. On hosted Ubuntu, unprivileged user namespaces are blocked, so qualification and contained launch must **refuse**. macOS and Windows must refuse. | a passing gate, **not** a qualification |
| A provisioned, disposable self-hosted runner (`containment-qualification.yml`) | `praxis local containment qualify` with the real denied-operation probes | The five dimensions are enforced on that runner | `qualified`, `unqualified` or `error` for that runner only |
| Anywhere else | — | — | **unavailable** |

Hosted runners are never reconfigured to make probes pass. The workflows do
not change `kernel.apparmor_restrict_unprivileged_userns` or any other
security setting.

**Current status: CI containment qualification is unavailable.** No
qualification runner has been provisioned yet, so nothing has run on one.
Until a run on a provisioned runner records `qualified`, the containment
status in CI is "unavailable", not "passed". Autonomous custom-code execution
stays disabled (#243).

### The qualification workflow

`.github/workflows/containment-qualification.yml`:

- **Trigger.** Manual `workflow_dispatch` only, and only for `main`. There are
  no `pull_request` or `pull_request_target` triggers, so untrusted PR code
  never reaches the runner. The workflow definition comes from the
  protected default branch.
- **Approval.** It uses the `containment-qualification` environment, which
  must be protected with required reviewers.
- **Token and secrets.** `permissions: contents: read`, no secrets, and
  checkout with `persist-credentials: false`.
- **Runner check.** It refuses unless the runner is self-hosted and carries
  the labels `self-hosted, linux, x64, praxis-containment-qualification`. The
  provisioner must also have written `/etc/praxis-containment-runner` with
  the lines `ephemeral=true` and `dedicated=true`, and `unshare`, `setpriv`,
  `prlimit`, `git` and `sha256sum` must be present.
- **Host state.** It builds the CLI and writes a private host configuration
  and host roots under `$RUNNER_TEMP`, outside the checkout.
- **Probes.** It runs `qualify` and `status`, which execute the real probe
  suite described under [Probes](#probes).
- **Record.** It produces and uploads, for 90 days, a record of:
  - OS, kernel and architecture;
  - util-linux version;
  - isolation mechanism and profile digest (the policy);
  - tool digests;
  - every probe outcome;
  - each dimension's status;
  - unsupported controls;
  - the qualification status;
  - the commit and runner name.

  It also writes a summary to the job page.
- **Cleanup.** It deletes all host state, even on failure. The job fails
  unless the record says `qualified`.

### Runner provisioning requirements

The qualification runner must be all of the following.

- **Dedicated and disposable.** Use a fresh VM or bare-metal image per job,
  registered with `--ephemeral` so it takes exactly one job and is destroyed
  afterwards. Never reuse it for other workflows or repositories, and never
  attach it to a runner group that untrusted workflows can target.
- **Isolated.** It holds no production credentials, cloud metadata access,
  SSH keys, package-publish tokens or network paths to internal systems.
  Outbound access is only what `actions/checkout`, `setup-dotnet` and NuGet
  restore need.
- **Unprivileged runner account.** The runner service must not run as root.
  Qualification exercises unprivileged user namespaces, which is the
  deployment shape the gate admits.
- **Kernel and tools.**
  - Linux with unprivileged user namespaces enabled on that disposable VM by
    its own image policy. This is never done on GitHub-hosted runners.
  - util-linux providing `unshare`, `setpriv` and `prlimit`.
  - `git`.
  - A merged-`/usr` layout, or `readOnlyPaths` adjusted in a reviewed change.
- **Registered labels.** `self-hosted`, `linux`, `x64`,
  `praxis-containment-qualification`, plus the marker file above, written by
  the provisioning image and not by a workflow.
- **Repository settings.**
  - Create the `containment-qualification` environment with required
    reviewers and no secrets.
  - Restrict the runner group to this repository and to this workflow.
  - Keep fork PR workflows from self-hosted runners (the GitHub default for
    public repositories; enforce it for private ones).

A qualification on that runner does not qualify developer machines or other
runners. Each host that will launch contained workers runs its own `qualify`
and keeps its own evidence file, and the launch gate checks that the evidence
matches the host.

### Preserved local evidence

`docs/containment-evidence/local-dev-host-2026-10-12/` holds a qualification
record from the development container where this feature was built. It was
produced by running the workflow's qualification step by hand at commit
`a6b4dc2`:
- `qualified`;
- 35/35 probes;
- all five dimensions `enforced`;
- Linux `6.18.44-fc-v114`, x86_64, util-linux 2.39.3;
- the process runs as root inside a container.

It shows that the mechanism works on that machine. It is **not** CI
qualification and qualifies no other host.
