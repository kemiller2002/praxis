# Local containment qualification: development container, 2026-10-12

This is a record from the development container where Praxis Linux
containment was built (#248). It is preserved as local evidence only.

- **It is not a CI qualification.** CI qualification is currently
  **unavailable** until a dedicated self-hosted runner is provisioned (see
  [`docs/containment.md`](../../containment.md#ci-qualification)).
- **It qualifies no other machine.** The launch gate rejects this evidence on
  any host whose kernel, architecture, profile digest or tool digests differ.

| Field | Value |
|---|---|
| Status | qualified (exit 0) |
| Probes | 35/35 passed |
| Dimensions | filesystem, process, network, credential and environment all `enforced` |
| Mechanism | `unshare(user,net,mount,pid,ipc,uts,kill-child)` + pivot_root to read-only binds + `prlimit` + `setpriv` (no_new_privs, no capabilities, locked securebits) |
| Profile digest | `sha256:2d36f27514297652cc4f0257121b5895e7e44add708c369ea719e7bf7bf4ec44` |
| Platform | Linux 6.18.44-fc-v114, x86_64, util-linux 2.39.3 |
| Account | root inside a container, mapped to namespace root with no capabilities |
| Commit | `a6b4dc2` |
| How it was produced | the `qualify` step of `.github/workflows/containment-qualification.yml`, run by hand with `RUNNER_TEMP` set to a local directory |

Unsupported controls on this host are listed in `containment-evidence.json`:
- seccomp;
- a distinct host uid;
- cgroup memory and CPU limits;
- commits from a linked worktree.

Files:
- `containment-evidence.json`: the `praxis.containment-evidence/1` record;
- `qualify.json`: the qualify command's output;
- `status.json`: the gate decision;
- `summary.txt`: platform facts.
