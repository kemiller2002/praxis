# ROS Work Protocol 1.0

ROS owns the versioned protocol, legal transitions, repository validation, and adapter contract. A consuming repository owns code and evidence. An external project-management service owns work-item truth, prioritization, and portfolio state. Disagreement is reported; no layer silently overwrites another.

## Local protocol

```bash
./ros work begin FEAT-142 --type feature
./ros work context FEAT-142
./ros work block FEAT-142 --reason "waiting for fixture"
./ros work resume FEAT-142
./ros work complete FEAT-142 \
  --evidence implementation=src/feature.js \
  --evidence tests=tests/feature.test.js
./ros validate
./ros validate --json
./ros status
```

The legal semantic core is `ready -> active -> blocked -> active` and `active -> complete`. Local states may be supplied with `--local-state`; `ros.json` maps repository states to the shared semantic vocabulary. Research completion accepts an independent `--conclusion`, including `inconclusive`.

`work context` is the normal agent entry point. It reports current state, legal next actions, and evidence required for completion. `status` combines compact work state with repository validation and recommended next actions. Validation errors include deterministic repair guidance; `validate --json` provides a stable structured result for agents and CI consumers.

`.ros/context/current.json` is local work context. `.ros/events/events.jsonl` contains small immutable, idempotently identified semantic events and durable file attribution. These files do not replace the external work item.

Completion validates configured evidence types and paths before changing state. `./ros validate` rejects meaningful dirty paths when enforcement is enabled and neither active context nor a completed event attributes them. CI is the authoritative enforcement boundary; hooks are optional convenience.

Deterministic housekeeping may use the configured `mechanical` work type. It still requires an explicit work-item identity and event, but the default profile does not require implementation/test evidence for that type.

## Adapter contract

The stable executable interface is `getWorkItem`, `transitionWorkItem`, and `publishRepositoryEvent`. Protocol 1.0 implements a file-backed adapter for conformance tests:

The normalized, testable contract is defined in [`work-adapter-contract.md`](work-adapter-contract.md). Its initial executable operations are `getWorkItem`, `transitionWorkItem`, and `publishRepositoryEvent`; broad listing is deferred.

```bash
./ros adapter publish --target .ros/mock-project-store/events.jsonl
```

Event IDs make retries idempotent. Successful local publication creates `.ros/publications.json` receipts without mutating immutable events. A write error returns failure and creates no success receipt; ROS never treats failure or an unknown remote outcome as success. Production adapters must add authentication, authorization, repository identity checks, version negotiation, retry policy, and explicit `success|failure|unknown` outcomes.

## Adoption and versioning

Initialize a repository with `ros-bootstrap init`, configure `repository` and `workProtocol` in `ros.json`, and call `./ros validate` in CI. Repositories pin a package/protocol version. Breaking semantic or event-schema changes require a new major protocol version; additive evidence types and local mappings are compatible minor changes.

Deferred: remote reads and transitions, signed events, review/approval transitions, commit graph indexing, global aggregation, and UI. These belong behind the adapter or in the external project-management system—not in ROS core.
