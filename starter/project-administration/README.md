# {{PROJECT_NAME}}

This repository is a **project-administration hub** running Praxis
{{ROS_VERSION}}. It is a separate, independent Praxis
repository — its job is to coordinate work across *other* Praxis repositories,
not to hold their code or evidence.

## What this is (and isn't)

- It registers other Praxis repositories by their local filesystem path.
- It creates work items in a registered repository by running **that
  repository's own `./praxis`** — it never edits another repository's files
  directly.
- It shows a combined, read-only view of work across every registered
  repository.
- It does **not** own or duplicate any other repository's work-item state.
  Each repository remains independently authoritative for its own work,
  exactly as if you'd run its `./praxis` commands yourself from its own
  directory. See [`docs/project-administration-hub.md`](docs/project-administration-hub.md).

This hub also has its own local work backlog (`./praxis add`, `./praxis work ...`)
for tracking the hub's own administrative work — see
[`docs/work-backlog-guide.md`](docs/work-backlog-guide.md).

## Getting started

The hub is built into this repository's `./praxis`; no Node.js or npm is
required.

```bash
./praxis hub serve
```

Opens `http://127.0.0.1:4320`. Register a repository, then create or browse
work items across everything you've registered.

The hub's own local backlog UI runs separately:

```bash
./praxis web serve
```

Or from the command line (`./praxis-hub ARGS` is the same as `./praxis hub ARGS`;
`./ros-hub` and `./ros` remain compatibility aliases):

```bash
./praxis-hub register /path/to/some/other/repo --name "Some Repo"
./praxis-hub repos
./praxis-hub create SOME-REPO-ID "Fix the thing" --tag bug --priority high
./praxis-hub work
```

## Local operating commands (for this repository's own work)

```bash
./praxis add "Describe the work"
./praxis work
./praxis status
./praxis registry check
./praxis validate
```

The installed snapshot is self-contained; it does not read from the source
Praxis repository. `.ros/installation.json` records the package version and
checksums of installed files.
