# Echelon Doctor

Echelon Doctor is the environment-level diagnostic and inventory surface for the Echelon Foundry engineering toolchain.

It does not replace domain-specific diagnostics owned by Ordo, Praxis, Visual Engineering, Communication Engineering, Limen, or other tools. It answers: what Echelon capabilities are present here, which versions are active, and is the environment coherent enough for those tools to run?

## Commands

    echelon doctor
    echelon doctor --verbose
    echelon doctor --fix
    echelon doctor --json
    echelon inventory
    echelon inventory --json

`doctor --fix` and `doctor --json` cannot be combined. Repair can download and activate tool versions, so machine-readable diagnostic stdout remains side-effect free.

## Three installation surfaces

### Native tools

Native executable tool versions live under the Echelon home, normally `~/.echelon/tools/<tool>/<version>/`. Doctor reports every version directory and the active version when one exists. Ordo and Praxis are currently first-class native tools.

### Repository component manifests

Repository lifecycle tools write manifests under `.echelon/*.json`. Doctor discovers manifests that expose `tool` plus `installedVersion` (or `version`). It ignores `.echelon/toolchain.json` and `*.config.json`, because those are requirements/configuration rather than installation evidence.

This lets Doctor discover repository-installed capabilities such as Visual Engineering, Communication Engineering, Limen, Research Publisher, and future Echelon tools without hard-coding every product name.

### Installed Echelon npm packages

Doctor scans repository-local `node_modules/@echelon-foundry/` and reads each package's own `package.json`. These entries mean the package is physically installed, not merely declared.

## Health semantics

`healthy`: no errors or warnings were found.

`warning`: the environment is usable, but reproducibility or convenience is incomplete.

`error`: an invariant required for reliable execution is broken.

Warning-only reports exit 0. Reports with errors exit 1. Invalid arguments exit 2.

## Finding codes

| Code | Severity | Meaning |
| --- | --- | --- |
| `ECHELON-DOC-001` | warning | Echelon bin directory is not on PATH |
| `ECHELON-DOC-002` | warning | Current directory is not inside a Git repository |
| `ECHELON-DOC-010` | error | Ordo has no active native version |
| `ECHELON-DOC-011` | error | Praxis has no active native version |
| `ECHELON-DOC-020` | error | Required Echelon command is missing or cannot execute |
| `ECHELON-DOC-021` | error | Command reports a version different from the active native version |
| `ECHELON-DOC-030` | warning | Repository has no `.echelon/toolchain.json` |
| `ECHELON-DOC-031` | warning | Toolchain manifest does not pin Ordo |
| `ECHELON-DOC-032` | warning | Toolchain manifest does not pin Praxis |
| `ECHELON-DOC-033` | error | Active Ordo does not satisfy the repository pin |
| `ECHELON-DOC-034` | error | Active Praxis does not satisfy the repository pin |
| `ECHELON-DOC-040` | error | Ordo repository verification failed |
| `ECHELON-DOC-041` | error | Praxis repository validation failed |

Agents should branch on `code` and `severity`, not English message text.

## JSON contract

`echelon doctor --json` writes exactly one JSON document to stdout. The contract has `schemaVersion: 1` and is defined by `schemas/echelon-doctor-v1.schema.json`.

`echelon inventory --json` is defined by `schemas/echelon-inventory-v1.schema.json`.

The Doctor report contains machine/platform and Echelon paths, native tools, command health, repository root and requirements, Ordo/Praxis validation status, repository component manifests, installed Echelon npm packages, coded findings, and summary counts.

## Repair boundary

`echelon doctor --fix` repairs only deterministic native-tool mechanics: missing command wrappers, missing unambiguous activation, and exact repository pin mismatches.

Doctor deliberately does not rewrite repository-managed `.sde/`, `.ros/`, Visual Engineering, Communication Engineering, or Limen content; modify shell startup files; choose arbitrarily among multiple unpinned versions; run npm installs; or upgrade repository components merely because a newer release may exist.

Those actions remain owned by the corresponding tool or explicit user action.
