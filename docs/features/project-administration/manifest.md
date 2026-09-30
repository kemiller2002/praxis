# Feature Manifest — Project administration

## Purpose

Route changes to the local hub that registers Praxis repositories and invokes each
repository's public Praxis work interface without becoming its work-state source
of truth.

## Ownership

- State, including presentation state: `.ros/hub/registry.json` and derived
  `.ros/hub/registry.md`; decision `DF-ROS-2026-A009` (implementation language:
  `DF-ROS-2026-A049`, superseding `DF-ROS-2026-A033`'s Node web/hub exception).
- Pure model and decisions: `src/Ros.Cli/Hub.fs` module `HubRegistry` (registry
  parse/render, Markdown projection, `register`/`unregister`/`find`, spoke
  repository-id resolution, spoke command lines, row annotation/merge, `praxis hub`
  option parsing).
- Transitions / commands / messages: `src/Ros.Cli/Hub.fs` module `Hub`
  (`registerRepo`, `unregisterRepo`, `listRepos`, `createWork`, `listWork`,
  `runSpoke`, and the `praxis hub` command dispatcher `run`).
- Invariants and guards: repository identity/path checks (`HubRegistry.register`,
  `Hub.registerRepo`) and per-spoke CLI outcome parsing (`Hub.runSpoke`).
- Capabilities / authority: the hub may register local paths and invoke the
  target's own `./praxis` (falling back to `./ros` for a spoke installed
  before the rename); each spoke remains authoritative for its work.
- Important effects and effect contracts: hub registry file writes, local
  subprocess calls (argument vectors, never a shell), HTTP upload temporary
  directories (`ros-hub-upload-*`, always removed), and HTTP requests.

## Interfaces

- Inbound: `praxis hub register|unregister|repos|create|work|serve`, the
  `praxis-hub` shell shorthand (`./praxis hub "$@"`; `ros-hub` is its
  compatibility alias), and `praxis hub serve`'s
  server-rendered pages and `/api/*` JSON routes (`HubWeb`, over `HttpHost` in
  `src/Ros.Cli/WebHttp.fs`).
- Outbound: hub registry JSON/Markdown, aggregated spoke JSON/errors, and
  delegated spoke work changes.

## Tests and verification

- Local behavior tests: `tests/Ros.Tests/HubTests.fs` (registry model and
  compatibility, command lines, routes, `praxis hub` end to end, `praxis hub serve`
  JSON API and form flows against real spoke repositories).
- Boundary/contract tests: spoke `./praxis` JSON/exit behavior; the registry file
  format is pinned byte for byte by a fixture; no formal versioned hub API
  schema exists.
- Integration/live verification: `praxis init --profile project-administration`
  scaffolding is covered by `tests/Ros.Tests/LifecycleTests.fs`.

## Dependencies

- Allowed direct dependencies: local filesystem, child process,
  `System.Net.HttpListener`, and spoke Praxis public interface. No Node.js, npm,
  or browser JavaScript.
- Required composition context: project-administration profile and writable
  `.ros/hub/` state.

## Modification boundaries

- Normal: `src/Ros.Cli/Hub.fs`, `src/Ros.Cli/WebHttp.fs`, `web-hub/styles.css`,
  project-administration starter files, docs, and tests.
- Escalation required: any attempt to make the hub authoritative for spoke work,
  expose it beyond the trusted local boundary, or couple it to Time Entry.

## Local agent instructions

- `AGENTS.md` and `docs/project-administration-hub.md`.

## Maintenance

- Owner: repository-governance
- Last checked against implementation: 2026-09-28
- Known gaps: registry writes are non-atomic/unlocked; HTTP has no
  authentication; attachment content types are not recorded by `work attach`.
