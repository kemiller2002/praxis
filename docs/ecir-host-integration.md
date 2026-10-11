# ECIR host integration status

Work: https://github.com/kemiller2002/praxis/issues/220.
Proposal: [DF-ROS-2026-A059](../research/decisions/DF-ROS-2026-A059--ecir-host-authority-and-dispatch.md).

## Implemented, isolated infrastructure

- `EcirHostPolicy.resolve`: an independently provisioned host grant binds the
  repository, qualified release, group/cohort and exact member-to-original-source
  map. Duplicate/missing grants, members or source identities are refused.
- `EcirHostPolicy.dispatch`: check pins; verify with a fresh clock after Ordo;
  stage; reload policy/membership; verify expiry again; invoke the host's
  revision/expiry compare-and-commit. Staging refusal causes no commit.
- `EcirHostAdministration`: obtain authenticated reviewer identity from the
  host, sign through its private-key service, independently verify the returned
  signature, and atomically apply receipt/policy plus audit. Revocation requires
  host administrative rights, removes the key and records an audit event.
- `EcirDispatchTransaction`: compare expected file hashes, reserve a journal
  atomically outside the repository, write an exact bounded state set, recover
  process interruptions without redispatch, and refuse hash/path/repository
  conflicts. The caller must protect the journal and hold repository locks.
- `EcirNativeStage`: invoke the existing native begin and group-recording
  planners in an isolated copy; preserve the original branch, worktree and
  actual execution IDs; emit only the allowed state writes. The integration
  test commits and recovers those exact member telemetry and envelope writes.
- `EcirMemberGuard`: refuse direct native and fallback-envelope begins for
  declared ECIR members. Group mutations and those begins share the
  work-protocol lock. Repository configuration cannot override a stored ECIR
  declaration to bypass the guard.

The ports are contracts for a protected host implementation. They do not make
an arbitrary callback trustworthy. No default host adapter or production test
key is registered, and no CLI approval/configuration flag opens ECIR execution.
The journal primitive alone grants no authority. Its tests cover process
interruption; they do not prove power-loss durability on every filesystem.

## Qualification gaps observed on 2026-10-10

Current Ordo source includes `ecir validate` and fixtures. Its distribution
version is 1.5.0, while `release/echelon.release-input.json` declares no provided
ECIR capability. Conditor's current registry pins Ordo 1.5.0; version/integrity
qualification is not evidence that a released binary implements ECIR. The
actual released artifact and its semantic operation must be checked and pinned.

Current Dokimos source provides an ECIR audit explicitly reporting
`source-trace-only; execution and tests unverified`. This is useful independent
source-conservation evidence, but cannot replace the behavioral acceptance
oracle required before the first real cohort. No such oracle was inferred.

## Local deployment direction (2026-10-10)

The user requires one coordinating agent handing bounded work to other local
agents without external services. The host authority can be embedded in a
local Praxis controller: it does not require a remote service or always-on
daemon. Separate authenticated local operator delegation and actual OS
protection remain necessary; agent-written approval flags remain insufficient.
See [the local coordination target](local-agent-coordination.md),
RQ-ROS-2026-A028, DF-ROS-2026-A060 and issue #226. Hosted Fides/Arca and remote
publication are optional. Strict offline operation additionally requires an
installed local model backend and validators. The new local checkpoint profile
is proposed, not an implicit waiver of current remote durability rules.

## Next bounded implementation

1. Add an embedded protected local controller adapter with separate agent/operator authority,
   authenticated reviewer/admin identity, anti-rollback policy revisions,
   durable atomic policy/audit updates, and serialized dispatch/revocation.
   Use authenticated local operator delegation; no self-reported actor flag.
   Fides integration may be added later and is not a local prerequisite.
2. Connect native staging and actual member telemetry to the protected host
   journal; only the private host-authorized execution route may invoke it.
   Use one documented lock order: work-protocol, work-groups, host policy.
   Cover direct work, group mutation and remote routes. A pending journal must
   be recovered or refused before another affected transition.
3. Qualify the actual released Ordo ECIR operation through Conditor, including
   known-valid and known-invalid artifacts; retain exact artifact fingerprints.
4. Freeze independent behavioral acceptance and run one small real cohort.
   Measure model/build/test/Git/CI/rework separately before scaling batches.

## Validation commands

```sh
dotnet build tests/Praxis.Tests/Praxis.Tests.fsproj --configuration Release
PRAXIS_TEST_FILTER=ECIR dotnet tests/Praxis.Tests/bin/Release/net10.0/Praxis.Tests.dll
./praxis registry check
./praxis validate
```

All 37 focused ECIR tests, seven dispatcher tests and ten group execution
tests passed. They cover changed host pins, unqualified releases, revocation,
membership changes, expiry during validation and staging, CAS refusal, failed
administrative audit persistence, incorrect signer output, concurrent journal
preparation, crashes at every write boundary, repeated recovery, late conflicts,
unsafe paths, dangling links, tampering and wrong-repository replay. CI found a
Unix rename reservation race; preparation now exclusively creates and flushes
the final journal, with 100 two-contender races in the regression test. A crash
before that flush may leave an incomplete journal: recovery retains it and
fails closed before any target write, requiring host investigation.
