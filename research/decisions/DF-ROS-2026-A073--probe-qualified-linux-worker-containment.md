---
id: DF-ROS-2026-A073
title: Qualify local worker containment by real probes; Linux namespaces only, other OSes refuse
status: draft
version: 1.0.0
owners: [repository-governance]
created: 2026-10-12
updated: 2026-10-12
research_area: repository-operating-system
decision_type: architecture
supports: [RQ-ROS-2026-A028]
related_documents: [DF-ROS-2026-A064, DF-ROS-2026-A070, docs/containment.md, docs/local-worker-portability.md, docs/execution-runtime.md]
tags: [agents, isolation, containment, security, linux, portability]
confidence: medium
derived_from: [DF-ROS-2026-A064]
provenance:
  contributions:
    EXE-20261012T185235269Z-42495e91:
      operations: [created]
      at: 2026-10-12T18:53:03.126Z
      actor:
        kind: agent
        id: anthropic/claude-code
        provider: anthropic
        model: unknown
        runtime: claude-code
      reason: "Record probe-qualified Linux worker containment and honest refusal on macOS/Windows (WI-0092, re-identified from WI-0089 after an ID collision with PR #246; GH-231/GH-235 phase 6)"
---

# Context

DF-ROS-2026-A064 separated portable worker mechanisms from OS isolation and
deferred isolation. The portable supervisor (`LocalWorkerProcess`) bounds a
process but runs it as the host user with full read access to host state:
policy, signer material, journals, evidence and credentials. Phase 6 of
#231/#235 requires host containment before any worker whose role needs it
(for example a future custom-code worker, #243) may launch. The repository
already has a containment vocabulary: `ContainmentProfile` with five
dimensions and `RestrictionStatus` (PRX-SEC-010/011) and a host-reported
`praxis.containment-evidence/1` file (PRX-SEC-001/013), but nothing produced
that evidence and nothing gated a launch on it.

On the development host (Linux 6.18, root in a container) `bwrap`, `nsjail`
and `firejail` are absent; util-linux `unshare`, `setpriv` and `prlimit` are
present and `unshare --user --map-root-user --net --mount --pid --fork` works.
An earlier prototype that only remounted the host tree read-only isolated
writes and network but still let the worker read files outside, so
credentials were not isolated.

# Decision

1. **Probes, not configuration, establish containment.** A qualification
   launches a fixed probe script through the same confined adapter and the
   real supervisor a worker uses, and judges 35 probes from host
   observations plus the in-sandbox report. A probe passes only when both
   exist and agree; a host-observed violation fails; anything else is
   `unknown`. A dimension is `enforced` only when all its probes pass,
   `unrestricted` when one fails, `unknown` otherwise, and `unavailable` when
   no mechanism exists. Unknown is never upgraded or collapsed into failure.
2. **Reuse the existing model.** The record is `praxis.containment-evidence/1`
   with additive fields (profile, profile digest, mechanism, platform,
   kernel, tool digests, probes, unsupported controls). Restrictions are
   derived from probes by `LocalContainment.restrictions` and parsed back
   through `ExecutionJson.readContainmentEvidence`; any disagreement refuses.
   No parallel containment model is introduced.
3. **Linux mechanism `linux-userns-pivot/1`.** The host builds the whole argv:
   `unshare` user/net/mount/pid/ipc/uts with `--kill-child`; a constant setup
   script, pinned by digest, that pivots into a tmpfs root holding only
   read-only `nosuid,nodev` binds of configured toolchain paths, a private
   `/tmp`, minimal `/dev`, a fresh `/proc` and the writable worker directory,
   detaches the old root, remounts `/` read-only and disables nested user
   namespaces; then `prlimit` and `setpriv` (no_new_privs, every capability set
   cleared, locked securebits). Paths reach the script only as arguments.
   Pivoting into a minimal root (rather than hiding selected paths with tmpfs)
   makes reads deny-by-default: unknown credential locations are absent.
4. **Pure path rules.** Read-only binds may not overlap sealed paths (host
   roots, repository, checkout root, configuration and evidence directories,
   credential stores, `/proc`, `/sys`, `/dev`, `/run`, `/etc/shadow`, ...) or
   contain protected paths (`/`, `/etc`, `/var`, `/home`, `/root`, `/tmp`,
   the user's home). The worker directory must be strictly inside the
   checkout root, the worker executable inside a bind, and the environment
   within an allowlist; a violating spec is refused, never trimmed.
5. **Gate.** A host-configuration worker may declare `"containment":
   "required"`. Before any checkout is provisioned it is admitted only when
   the platform is Linux and a stored qualification matches this host's
   current kernel, architecture, profile digest (scripts, settings, binds,
   allowlist, limits) and tool digests, covers exactly the current probe ids,
   is within `maxEvidenceAgeMinutes`, and shows every dimension `enforced`.
   Every qualification run replaces the evidence, so a failing run revokes
   admission. Workers without the requirement are unchanged.
6. **Honest platforms.** macOS and Windows have no implemented, probed
   mechanism. They qualify as `unavailable` in every dimension and refuse
   contained launches. CI asserts only that; no parity is claimed.

# Alternatives and consequences

- *bubblewrap/nsjail/firejail*: mature, but absent here and an extra pinned
  dependency; the same namespace primitives are available from util-linux.
  A future profile may use them if installed and probed the same way.
- *Read-only remount of the host tree plus tmpfs over known secret paths*:
  rejected because reads stay allow-by-default (the prototype read outside
  files); any unlisted credential location would leak.
- *Trusting the in-sandbox report alone*: rejected; host-observable effects
  (file digests, planted secrets, listener accepts, sentinel liveness) decide
  whenever they exist.
- *Running the worker as a distinct host uid*: needs a subordinate-id mapping
  and ownership changes in the worker directory; deferred. The worker runs as
  the invoking uid mapped to namespace root with no capabilities, so read
  isolation relies on mount visibility, not DAC.

Consequences: a contained worker cannot reach the linked worktree's common
`.git` directory (it is in the sealed repository), so it cannot commit; #243
needs an output protocol for returning changes. There is no seccomp filter and
no cgroup memory/CPU accounting. Hosts that restrict unprivileged user
namespaces (Ubuntu AppArmor `apparmor_restrict_unprivileged_userns=1`, as on
GitHub-hosted Ubuntu runners by default) qualify as `unknown` and refuse until
the operator changes that policy; CI therefore exercises the refusal path there
unless a host prerequisite is provisioned.

# Validation

On the development host the suite qualified every dimension (35/35 probes
passed). The 9 `local containment:` tests passed; forcing the wrong
expectation failed the three real-probe tests; removing `--net` from the
adapter produced `network unrestricted` with three failed probes and an
unqualified record. Three-OS CI results are recorded only after they run.
