#!/usr/bin/env node
// Reports one activity observation to a running ROS Central host
// (`ros-central`, src/Ros.Host). This is opt-in producer tooling, not
// part of `./ros validate`/`build`/`test` -- a repository's normal local
// workflow has zero dependency on this script or on Central being
// reachable at all, per AGENTS.md's Integration Architecture Rules
// ("local repos stay independent"; see
// docs/migrations/central-integration/MIGRATION-PLAN.md). Refuses to run
// unless ROS_CENTRAL_ENABLED=true, since connecting to Central is a
// migration-controlled, explicitly-opted-into behavior, never a default.

import fs from "node:fs";

export async function reportActivity(centralUrl, activity, producer = "unspecified") {
  const response = await fetch(new URL("/integration/v1/activities", centralUrl), {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Ros-Producer": producer },
    body: JSON.stringify(activity)
  });

  const body = await response.json().catch(() => ({}));
  return { status: response.status, body };
}

function usageAndExit() {
  console.error("Usage: ROS_CENTRAL_ENABLED=true node tools/ros_central_client.mjs <centralUrl> <activityJsonFile> [producer]");
  process.exit(1);
}

async function main() {
  if (process.env.ROS_CENTRAL_ENABLED !== "true") {
    console.error(
      'ROS_CENTRAL_ENABLED is not "true" -- refusing to contact Central. This is a migration control, not a bug; see AGENTS.md\'s Integration Architecture Rules.'
    );
    process.exit(1);
  }

  const [centralUrl, activityFile, producer] = process.argv.slice(2);
  if (!centralUrl || !activityFile) usageAndExit();

  const activity = JSON.parse(fs.readFileSync(activityFile, "utf8"));
  const result = await reportActivity(centralUrl, activity, producer);

  console.log(JSON.stringify(result, null, 2));
  process.exit(result.status >= 200 && result.status < 300 ? 0 : 1);
}

if (import.meta.url === `file://${process.argv[1]}`) {
  main();
}
