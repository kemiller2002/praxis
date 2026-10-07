# Installation registration (`praxis installation`)

Project Administration owns the canonical Echelon installation inventory —
what system is installed where. Praxis is its registration client. Echelon
Registry remains the catalog of what systems exist.

```
praxis installation register --system <system-id> --version <system-version>
```

## Examples

```
# Praxis registers itself (also automatic after `praxis init`/`upgrade`
# when an administration integration is configured)
praxis installation register --system praxis --version 3.6.0 \
  --source-repository kemiller2002/praxis --distribution github-release --release v3.6.0

# Ordo, installed from its native release, on this repository
praxis installation register --system ordo --version 1.4.0 \
  --distribution github-release --release v1.4.0 \
  --artifact ordo-linux-x64.tar.gz --digest sha256:26389b98…

# Conditor on an explicitly identified workstation
praxis installation register --system conditor --version 0.1.0 \
  --target-kind environment --target-id ws-primary --execution EXE-…

praxis installation remove    --system ordo --version 1.4.0
praxis installation reconcile --system ordo --version 1.4.0 --state indeterminate
praxis installation status                                   # this repository
praxis installation list --system ordo --version 1.4.0
praxis installation history --target environment:ws-primary
```

All commands accept `--json`. Other options: `--target-kind
repository|environment`, `--target-id`, `--source-repository`,
`--distribution`, `--release`, `--artifact`, `--digest`, `--evidence
KIND=REF`, `--occurred-at`, `--execution`, `--work-item`, `--operation-id`,
`--catalog`, `--config`, `--require`.

- **Target.** Inside a Git repository the repository target is inferred from
  the `origin` remote (`owner/repo`). An environment target needs an
  explicit logical ID (`--target-id` or `environment.id` in the
  configuration). Praxis never infers or sends a hostname, username, home
  directory, local path, serial or MAC address.
- **Idempotency.** The operation ID is derived from the request (or given
  with `--operation-id`), so a retry replays instead of duplicating;
  registering an unchanged installation reports `unchanged`.
- **Provenance.** The resolved Praxis actor and any `--execution` /
  `--work-item` travel with the request.

## Configuration

`.echelon/administration.json` (or `ECHELON_ADMINISTRATION_CONFIG`, or
`--config PATH`):

```json
{
  "schema": "echelon.administration/v1",
  "provider": "project-administration",
  "required": false,
  "environment": { "id": "ws-primary" },
  "catalog": "../echelon-registry/registry/systems.json",
  "transport": { "kind": "local", "command": ["administration"], "store": "../../project-administration" }
}
```

or, without a local checkout:

```json
{
  "schema": "echelon.administration/v1",
  "transport": { "kind": "github-workflow", "repository": "kemiller2002/project-administration", "workflow": "installation-request.yml", "ref": "main" }
}
```

Relative paths resolve against the configuration file's directory. The
local transport runs Project Administration's own `administration`
executable against its checkout; the GitHub transport dispatches its
`installation-request.yml` workflow (outcome `submitted`, recorded
asynchronously; queries need a local transport). Praxis never reads or
writes Project Administration's files.

## Optional vs required

Registration is an optional integration. With nothing configured, or with
the provider unreachable, the outcome is `unavailable` (exit 0) and every
Praxis, Ordo and Conditor command keeps working. Set `"required": true` (or
pass `--require`) to make failure exit 6. Outcomes: `recorded`, `replayed`,
`unchanged`, `submitted` (exit 0); `invalid` (2); `refused` (3);
`unavailable`/`misconfigured` (0, or 6 when required).
