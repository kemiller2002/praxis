# Research Operating System

**ROS Version:** 1.0.0  
**REP Specification:** 2.0

Distributed under the [MIT License](LICENSE).

This repository is a shared operating environment for autonomous research
and engineering agents.

## Canonical source hierarchy

1. Scientific Research Journals
2. Research Execution Packages
3. Theory Registry
4. Evidence Registry

Reports, websites, presentations, and training materials are derived from
canonical research artifacts. Generated products must not silently replace
or modify canonical records.

## Agent entry point

Every agent starts with [`BOOTSTRAP.md`](BOOTSTRAP.md).

## Executable contract

Canonical knowledge is stored in Markdown artifacts. JSON registries are
generated views.

```bash
./ros registry build
./ros registry build --dry-run
./ros registry check
./ros validate
python3 -m unittest discover -s tests -v
```

The CLI uses only the Node.js standard library. Validation returns a nonzero
exit code for malformed front matter, invalid or duplicate IDs, broken
references, invalid lifecycle values, nonreciprocal supersession, filename/ID
mismatches, and stale registries.

## Work protocol

ROS 1.0 provides provider-neutral work context, legal `begin`, `block`, `resume`, and `complete` transitions, configurable completion evidence, durable attribution events, and idempotent file-adapter publication. A repository-local backlog (`ros add`, `ros work list|ready|show|start`) lets work be captured cheaply before it has an externally-assigned ID, and graduates into this same protocol via `work start`. See [`docs/work-protocol.md`](docs/work-protocol.md) and, for a UI over the same backlog, [`docs/web-interface.md`](docs/web-interface.md) (`npm run web`).

External project-management products integrate through the normalized [`work adapter contract`](docs/work-adapter-contract.md); they are not embedded in ROS.

Roadmap execution state and repository boundaries are tracked in [`docs/ROADMAP-STATUS.md`](docs/ROADMAP-STATUS.md).

## Starting central aggregation and reporting

The default reporting project is `project-administration`. It is the central
project used when incoming work has no more specific project assignment. A
work item may name another project explicitly; an explicit assignment always
overrides the default.

Build the reporting system in a separate repository. Install ROS into that
repository first, and then use the installed work protocol to govern the
project-specific administration instructions, datastore, ingestion service,
reports, and operational procedures.

The ownership boundary is:

| Owner | Responsibility |
|---|---|
| ROS | Generic work protocol, legal transitions, validation, bootstrap behavior, and adapter contract |
| Central reporting repository | Project administration, repository registration, portfolio data, ingestion, reconciliation, reporting rules, access control, retention, and operations |
| Contributing repository | Implementation, evidence, local workflow mapping, and repository-specific instructions |

Do not add central project-administration policy to the reusable ROS package.
Move a rule into ROS only when it is intended to apply to every ROS-controlled
repository.

### 1. Create and initialize the reporting repository

For example:

```bash
mkdir project-administration
cd project-administration
git init

npx --yes --prefer-online \
  --package=github:kemiller2002/repository-operating-system#main \
  ros-bootstrap init \
  --target . \
  --project "project-administration"

./ros registry check
./ros validate
```

Here `--project` sets the initialized repository's display name. The central
reporting service must separately apply `project-administration` as its
default project assignment when it normalizes ingested work.

For a reproducible installation, replace `main` with a release tag or exact
commit SHA. Once the npm release is available, the equivalent package is
`@echelon-foundry/repository-operating-system@<version>`.

### 2. Govern the setup through ROS

Begin an attributed work item before adding the central repository's
instructions or implementation:

```bash
./ros work begin PM-BOOTSTRAP-001 --type feature --actor <actor-id>
./ros work context PM-BOOTSTRAP-001
```

Add repository-owned instructions covering at least:

- the `Project`, `Repository`, `WorkItem`, `Relationship`, `Transition`, and
  `EvidenceReference` records;
- repository registration and stable repository identities;
- the rule that missing project assignments resolve to
  `project-administration`;
- authenticated ingestion and authorization scopes;
- idempotency by request ID and event ID;
- `success`, `failure`, and `unknown` delivery outcomes;
- retry, reconciliation, backup, retention, and recovery procedures; and
- definitions for each published report.

The central service's project-default configuration should express the rule
directly. For example, if that service uses JSON configuration:

```json
{
  "reporting": {
    "defaultProject": "project-administration"
  }
}
```

This JSON is an example for the central service, not a currently supported
field in `ros.json`.

### 3. Connect contributing repositories

Each contributing repository needs a stable `repository.id` in `ros.json` and
a mapping from its local states to ROS semantic states. For example:

```json
{
  "repository": {
    "id": "example-service",
    "type": "software"
  },
  "workProtocol": {
    "version": "1.0.0",
    "semanticMapping": {
      "backlog": "ready",
      "development": "active",
      "waiting": "blocked",
      "done": "complete"
    }
  }
}
```

Repositories produce immutable events in `.ros/events/events.jsonl`. During a
local conformance test, those events can be collected with the file-backed
publisher:

```bash
./ros adapter publish --target .ros/mock-project-store/events.jsonl
```

That command is a test seam, not a production aggregation transport. A
production adapter should send authenticated `publishRepositoryEvent`
requests to the central service and retain the contract's explicit outcome.

### 4. Prove a reporting slice

Start with two contributing repositories and verify this path end to end:

```text
repository event
  -> authenticated publishRepositoryEvent request
  -> idempotent central ingestion
  -> project assignment (explicit or project-administration)
  -> normalized portfolio records
  -> active and blocked work reports
```

The first reports should cover active work, blocked work, work by project,
work by repository, completed work over time, missing evidence, and stale or
unpublished repository activity. Test duplicate delivery and an `unknown`
outcome before expanding the UI or adding broader portfolio views.

### 5. Complete and validate the setup work

After implementation and tests exist in the reporting repository:

```bash
./ros work complete PM-BOOTSTRAP-001 \
  --evidence implementation=<implementation-path> \
  --evidence tests=<test-path>

./ros registry build
./ros validate
```

The central system owns portfolio state and reporting. It consumes repository
events but does not scrape repositories as its primary integration mechanism
or silently rewrite repository-owned implementation and evidence.

New agents should read the lifecycle, supersession, identifier, confidence,
artifact-tier, and taxonomy documents under `framework/` before creating
canonical records.

## Portable greenfield installation

ROS can be loaded into a separate beginning project through its self-contained
npm package. The package embeds the governance, schemas, templates, validator,
empty registries, and greenfield pilot records; the initialized repository does
not read this source checkout.

After publication, initialize from npm with:

```bash
npx --yes \
  --package=@echelon-foundry/repository-operating-system@<version> \
  ros-bootstrap init \
  --target .
```

Install the newest `main` snapshot with:

```bash
npx --yes \
  --package=@echelon-foundry/repository-operating-system@main \
  ros-bootstrap init \
  --target .
```

Until the npm release exists, install from the GitHub repository:

```bash
npx --yes --prefer-online \
  --package=github:kemiller2002/repository-operating-system#main \
  ros-bootstrap init \
  --target .
```

The project display name is derived from the target folder. Pass
`--project "Different Display Name"` only when an override is needed. This
command checks the remote and installs the latest `main`; use a tag or commit
SHA when reproducibility is more important than freshness. See
[`PACKAGE-USAGE.md`](PACKAGE-USAGE.md) for dry-run, collision, verification,
and release instructions.

## Repository principles

- Preserve provenance.
- Prefer stable identifiers over filenames as references.
- Do not overwrite immutable findings.
- Record what evidence supports each important claim.
- Separate observations, evidence, assumptions, inferences, and conclusions.
- Rebuild generated registries after creating canonical artifacts.
- Leave the repository usable by the next agent.
