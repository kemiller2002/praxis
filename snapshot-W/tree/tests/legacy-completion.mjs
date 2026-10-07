// New installations enforce durable checkpoints
// (workProtocol.continuity.requireDurableCheckpoint, DF-ROS-2026-A042):
// meaningful Git-backed work completes only from a verified, pushed
// checkpoint. The Node-parity golden masters below were frozen against
// the pre-continuity completion semantics in fixtures that have no remote
// and never commit, so they opt out explicitly. The enforced behaviour is
// pinned by tests/Ros.Tests/CheckpointGuardTests.fs and RecoveryProofTests.fs.
import fs from "node:fs";
import path from "node:path";

export function optOutOfDurableCheckpoints(root) {
  const file = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(file, "utf8"));
  config.workProtocol.continuity = { requireDurableCheckpoint: false };
  fs.writeFileSync(file, `${JSON.stringify(config, null, 2)}\n`);
}
