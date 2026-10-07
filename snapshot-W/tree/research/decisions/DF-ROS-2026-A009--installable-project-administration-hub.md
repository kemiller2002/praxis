---
id: DF-ROS-2026-A009
title: Installable project-administration hub profile
status: accepted
version: 1.0.0
confidence: medium-high
created: 2026-08-22
updated: 2026-08-22
owners: [repository-governance]
related_documents: [docs/project-administration-hub.md, docs/work-backlog-guide.md, research/decisions/DF-ROS-2026-A008--repository-local-work-backlog.md]
supersedes: []
superseded_by: []
tags: [work-protocol, project-management, bootstrap, hub]
---

# Decision

ROS ships a second installable bootstrap profile, `project-administration`
(`ros-bootstrap init --profile project-administration`), alongside the
existing `greenfield` profile. It scaffolds a hub: a registry of other ROS
repositories by local filesystem path, a CLI (`ros-hub`) and web UI to
create a work item in any registered repository, and a read-only aggregated
view of work across all of them.

The hub never reads or writes a registered repository's files directly.
Every operation shells out to that repository's own `./ros`
(`execFile` with an argv array, never a shell), so a work item created
through the hub is indistinguishable afterward from one created by hand in
that repository -- same ID sequence, same queue.json, same validation and
evidence rules. The hub adds no second source of truth for any repository's
work, and each registered repository stays fully usable independently, as
[[DF-ROS-2026-A008]] already requires of the local backlog it builds on.

`lib/bootstrap.mjs`'s profile mechanism is now generic (`starter/<profile>/manifest.json`,
discovered by directory listing) rather than hardcoded to `greenfield`, so
the manifest -- not new branching logic in the installer -- defines what
each profile installs.

## Rationale and alternatives

This is a materially different capability from [[DF-ROS-2026-A008]]: that
decision deliberately kept ROS's local backlog from becoming a second
work-item authority *within one repository*. This one manages *other*
repositories by path and creates work in them -- exactly the "central
project-administration" role the README's ownership table and
[[DF-ROS-2026-A008]] itself assign to a separate repository, not ROS core.
Building it as ordinary files inside a single, hand-maintained
`project-administration` checkout was rejected: the user explicitly asked
for it to be "installable like ROS... a template system that can be set up
and used over," and a one-off checkout can't be reinitialized, reused for a
second hub, or upgraded the way a profile can.

Implementing cross-repository access via the existing external work adapter
contract (`docs/work-adapter-contract.md`) was considered and rejected for
this use case: that contract is authenticated, network-transport-shaped,
and intentionally has no `createWorkItem`/`listWorkItems` operation yet
("deferred until a real consumer demonstrates a stable need"). What was
actually asked for is simpler and local-machine-scoped -- a JSON file of
paths, and running each repository's own CLI in its own directory. Reusing
the adapter contract's authentication and versioning machinery for a
single-machine, single-user tool would be solving a problem (multi-machine,
multi-principal access) that wasn't presented, at real implementation cost.

A shared `tools/http_body.mjs` was extracted from `ros_server.mjs`'s
existing multipart parser rather than duplicating it into
`ros_hub_server.mjs`, since both HTTP adapters need the same request-body
parsing and neither owns any domain logic that should differ.

## Consequences

- New profile: `starter/project-administration/`, plus new packaged
  sources `tools/ros_hub_cli.mjs`, `tools/ros_hub_server.mjs`,
  `tools/http_body.mjs`, `web-hub/*`. `tools/ros_server.mjs` and `web/*`
  (previously source-only, not distributed) are now also in `package.json`'s
  `files`, since the hub profile's manifest needs them installable from the
  published package, not just this source checkout.
- A registered repository must have a ROS installation recent enough to
  support the backlog commands (`ros add`, `ros work ...`); an older
  installation surfaces as a clear per-repository error in aggregation and
  create, not a crash or a silent no-op.
- Registration is local-filesystem-path based and therefore single-machine
  by construction. Cross-machine coordination remains explicitly out of
  scope and would be a different decision (built on the adapter contract,
  when a concrete consumer needs it).
- The hub server has no authentication, same as the single-repo web
  interface, but with a larger blast radius (it can act on every registered
  repository, not just one) -- documented prominently in both the CLI
  startup banner and `docs/project-administration-hub.md`.
- No ingestion service, reconciliation, access control, retention, or
  reporting beyond the raw aggregated table exists. The README's
  ownership-table vision for a central reporting repository is broader than
  this; this decision covers only the registration/creation/aggregation
  slice explicitly requested.

## Validation and evolution

Tests cover: registration (id derivation from the target's `ros.json`,
duplicate-path and duplicate-id rejection, rejection of a non-ROS or
`./ros`-less directory), work creation landing in the real target
repository's actual `queue.json` (not a hub-side copy), aggregation merging
rows across repositories, a moved/deleted repository surfacing as an
isolated per-repository error without breaking the rest of the view,
unregistration, the HTTP API (including real multipart file uploads with
temp-file cleanup verified), and the bootstrap profile itself (installs
cleanly, validates immediately, and a freshly-scaffolded hub's own
`ros-hub` binary works against a separately-scaffolded spoke repository).
Adding authentication, multi-machine access, or a portfolio datastore would
each need their own decision rather than silently expanding this one.
