# ROS Central Service and Integration Contract Upgrade — Compatibility Matrix

This matrix records, per existing component, what must remain true
throughout the migration and how that will be checked. It complements
`CURRENT-SYSTEM-INVENTORY.md` (what exists) and `MIGRATION-PLAN.md` (when
things happen) with an explicit, checkable compatibility obligation per
component, so any future phase can be verified against a concrete
promise rather than a general intention.

| Component | Compatibility obligation | Verification mechanism | Owner of the check |
|---|---|---|---|
| `Ros.Domain` (existing modules) | Public types/functions in `Artifacts`, `Git`, `Telemetry`, `Work` unchanged in shape and behavior | `Ros.Tests.dll` unit suite (376 tests at baseline) must stay green with zero modified assertions in existing test files | `ros-validation.yml`, every commit |
| `Ros.Contracts` | Existing JSON wire shapes for artifacts, work, telemetry, git status unchanged | Node differential suite's frozen golden-master literals (part of the 187-test F# differential suite) must match byte-for-byte | `ros-validation.yml`, every commit |
| `Ros.Application` | Existing operation behavior (begin/complete/block/resume work, telemetry recording, artifact operations) unchanged | Same differential suite, exercised end-to-end through the CLI | `ros-validation.yml`, every commit |
| `Ros.Infrastructure` | File formats under `.ros/`, `research/`, `registries/` unchanged; no new project reads/writes these paths from outside the existing repository layer | `Ros.Tests.dll` real-filesystem round-trip tests; manual review of any new project's file access (none should touch these paths) | `ros-validation.yml` plus review at each PR introducing a new project |
| `Ros.Cli` (`ros-fs` / `./ros`) | Every existing subcommand, flag, JSON output shape, and exit code unchanged; no renames without aliases | F# differential/CLI suite (187 tests) plus Python `test_ros_cli.py` (7 tests) | `ros-validation.yml`, every commit |
| `ros_cli.mjs` / `ros_cli.py` | Unaffected by this migration; governed by the pre-existing, independent Node/Python→F# parity effort documented in `docs/migrations/fsharp/` | Existing Node/Python test suites (55 + 7 tests) | `ros-validation.yml`, every commit |
| `ros_server.mjs` (`web/`) | Unaffected; continues to operate with zero dependency on Central | Node `ros-server` test file | `ros-validation.yml`, every commit |
| `ros_hub_server.mjs` (`web-hub/`) | Continues to operate standalone; not required to call Central; any future integration with `Ros.ProjectAdministration` is additive and optional, never a hard dependency | Node `ros-hub` test file; explicit manual check at Phase 7 that this server still starts and serves requests with `ROS_CENTRAL_ENABLED=false` | `ros-validation.yml` every commit; manual check at Phase 7 |
| `ros-validation.yml` | Not replaced or restructured by this migration; remains the sole existing CI gate for repository-local ROS | Direct diff review of the workflow file on every PR touching `.github/workflows/` | Reviewer, every PR |
| `publish.yml` | Not modified by this migration; npm/GitHub Release publishing behavior unchanged | Direct diff review; existing publish smoke checks (`release:check` script) | Reviewer, every PR touching `.github/workflows/` or `package.json` |
| `AGENTS.md`, `.sde/` | All existing rules remain in force; only additive governance sections are introduced (the three permanent rules from `MIGRATION-PLAN.md`), and only in a dedicated, reviewed commit — not bundled into this Phase 0 documentation pass | Manual review at the time the addition is proposed | User, at the time of that specific PR |
| `schemas/*.schema.json` | Unchanged; new integration contract schema is a separate, versioned surface under `tests/contracts/`, never merged into or replacing these | Existing schema-consuming tests; review that no PR under this migration edits `schemas/` | Reviewer, every PR under this migration |
| `starter/project-administration` | Unchanged; name collision with the new central "Project Administration" domain is cosmetic only, noted for human readers, not resolved by renaming either side | N/A (no behavior at stake) | N/A |

## New-component compatibility obligations (apply once each is created)

These do not yet exist at Phase 0, but their compatibility promises are
fixed now so that Phase 1+ work is held to them from its first commit
rather than retrofitted later.

| New component | Compatibility obligation | Verification mechanism |
|---|---|---|
| `Ros.Integration` | Zero dependencies beyond `FSharp.Core`/`System.*`; never references `Ros.Domain`, `Ros.Infrastructure`, or any transport/framework/DB library; contract version field independent of package semver from its first release | `dotnet list package` / project-reference audit in CI; a compile-time check that the `.fsproj` has no forbidden `PackageReference`/`ProjectReference` |
| `Ros.Integration` serialization | Old contract-version JSON must always deserialize into the current package's model via an explicit `V1`/`V2`/`Adapter` boundary; no historical version logic leaks into current model code | Golden-file tests under `tests/contracts/activity-observation-v1.json`; compatibility fixtures under `tests/Ros.Integration.Compatibility.Tests/fixtures/v1/*.json` |
| `Ros.Integration.Consumer.Tests` | References only the published package's public surface, never `Ros.Domain` or any internal project | CI check that the test project's `ProjectReference`/`PackageReference` list contains no internal ROS project |
| `Ros.Host` endpoints | A retried request with the same producer-generated ID (`activityId`/`executionId`) never creates a duplicate record; malformed transport → `400`; valid payload, illegal ROS transition → `422`; genuine state conflict → `409`; unexpected failure → `500` | Idempotency unit tests keyed on `source + external ID`; HTTP-level integration tests asserting each status code path |
| `Ros.ProjectAdministration` | `ExternalActivityState` and `OutboundDeliveryState` only permit their named legal transitions; no arbitrary status assignment path exists in the code | Unit tests enumerating every legal transition and asserting all others are rejected |
| Outbox (`OutboundIntegration`) | A delivery attempt that fails and retries never produces a duplicate downstream write; failure/attempt count/last error are always recorded before a retry is attempted | Unit tests for create/retry/completion/duplicate-suppression |
| `integration-package-ci.yml` | Never publishes a package; scoped to `src/Ros.Integration/**` so it cannot fire on unrelated changes and cannot mask a `ros-validation.yml` failure | Path-filter review; confirm both workflows appear as separate, independently-passing checks on a PR touching only `Ros.Integration` |
| `publish-integration-package.yml` | Fires only on `ros-integration-v*` tags; never on ordinary commits; uses `secrets.GITHUB_TOKEN` with `packages: write`, never a long-lived PAT; refuses to publish if any required test fails | Trigger-condition review; a dry run against a non-matching tag/branch to confirm it does not fire |
| Feature flags (`ROS_CENTRAL_ENABLED`, `ROS_EXTERNAL_ACTIVITY_ENABLED`, `ROS_CHRONA_EXPORT_ENABLED`) | Default `false`/off; existing behavior is authoritative until a flag is explicitly enabled; disabling a flag alone is sufficient to roll back that phase's behavior | A test asserting default-off behavior for each flag; a rollback rehearsal at the end of the phase that introduces each flag |

## Cross-cutting check applied at every phase boundary

Before any phase in `MIGRATION-PLAN.md` is considered complete, the same
full local validation gate captured in `BASELINE.md` (`npm run
test:all`, `./ros registry check`, `./ros validate`) is re-run from a
clean build and compared against the baseline's 625/0/625 result. Any new
failure is root-caused against this matrix and the known-flake entry in
`BASELINE.md` before being attributed to the migration or dismissed as
pre-existing.
