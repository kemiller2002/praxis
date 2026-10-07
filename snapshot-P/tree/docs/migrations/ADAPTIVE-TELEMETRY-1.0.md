# Adaptive telemetry 1.0 migration

Adaptive telemetry adds a new execution schema; it does not rewrite the work protocol 1.0 event or state-transition semantics.

Existing completed work items remain valid without telemetry. A work item enters the new contract when a new `work begin` creates `telemetryExecutionIds`; from that point completion requires finalization. ROS never synthesizes provider usage for historical work.

To upgrade an existing installation:

1. begin an attributed ROS upgrade item;
2. install `tools/ros_telemetry.mjs`, `tools/ros_persistence.mjs`, `telemetry/metrics.json`, `schemas/execution-telemetry.schema.json`, and `docs/development-telemetry.md` beside the updated `tools/ros_cli.mjs`;
3. add the `telemetry` configuration from the current starter `ros.json`; add both `.ros/telemetry/**` and `.ros/locks/**` to `workProtocol.ignoredPaths`, and ignore `.ros/locks/` in Git, so telemetry coordination does not attribute itself as product work;
4. install or merge the provider router files only when they do not replace project-owned instructions;
5. run `./ros validate`, begin a disposable work item, confirm a record appears under `.ros/telemetry/executions/`, complete it, and confirm the record finalizes;
6. run the repository's tests and inspect raw-snapshot privacy settings before enabling runtime hooks.

The code defaults telemetry to enabled when the configuration block is absent, but the metric registry is required. To disable collection for a constrained repository, set `telemetry.enabled` to `false` and record `telemetry.disabledReason`; do not delete existing records.

The additive retention, capability-history, and synchronization fields do not require rewriting existing telemetry `1.0.0` records. Missing optional history and byte-count fields remain readable. Future breaking schema changes must preserve the original per-execution files or provide a lossless, auditable conversion. Unknown future provider fields are already backward-compatible because they live in the raw layer.
