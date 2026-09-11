import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const installWorkItemId = `ROS-INSTALL-${JSON.parse(fs.readFileSync(path.join(repositoryRoot, "package.json"), "utf8")).version.replaceAll(".", "-")}`;
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// Golden masters below were captured once from production's own Node
// implementation (tools/ros_cli.mjs's startWork/transition) with the exact
// same call sequence as each test, then frozen here. Node is retained in
// this repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this
// test suite.
const GOLDEN = {
  test1Context: {
    schemaVersion: "1.0.0",
    protocolVersion: "1.0.0",
    repository: "work-resume-differential",
    actor: "ros-bootstrap",
    baselineDirtyPaths: [],
    workItems: [
      {
        id: installWorkItemId,
        type: "mechanical",
        state: "complete",
        semanticState: "complete",
        evidence: [{ type: "installation", path: ".ros/installation.json" }]
      },
      {
        id: "WI-BLOCKED",
        type: "task",
        state: "active",
        semanticState: "active",
        evidence: [],
        blockReason: "waiting on review"
      }
    ]
  },
  test1SemanticState: "active",
  test1ExecIdsLength: 1,
  test1Events: [
    {
      schemaVersion: "1.0.0", type: "work.started", workItem: "WI-BLOCKED", repository: "work-resume-differential",
      protocolVersion: "1.0.0", evidence: [], paths: [], publication: { status: "pending" }
    },
    {
      schemaVersion: "1.0.0", type: "work.resumed", workItem: "WI-BLOCKED", repository: "work-resume-differential",
      protocolVersion: "1.0.0", evidence: [], paths: [], publication: { status: "pending" }
    }
  ],
  test1ExecutionsLength: 1,
  test2SemanticState: "active",
  test2ExecIdsLength: 1,
  test2ExecutionsLength: 1,
  test2ExecutionsWorkItemId: "WI-NEVER-BEGUN",
  test3Message: "work item 'WI-GHOST' is not in repository context",
  test4Message: "cannot resume 'WI-ACTIVE' from 'active'"
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-resume-${label}-`));
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
  initializeProject({ target: root, project: "Work Resume Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function readContext(root) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "context", "current.json"), "utf8"));
}

function readEvents(root) {
  const file = path.join(root, ".ros", "events", "events.jsonl");
  return fs.existsSync(file) ? fs.readFileSync(file, "utf8").split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line)) : [];
}

function readExecutions(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8")));
}

function runFsharp(root, command, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", command, ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

const VOLATILE_KEYS = new Set([
  "executionId", "startedAt", "discoveredAt", "lastAssessedAt", "recordedAt", "collectedAt",
  "measurementId", "commit", "branch", "dirtyPaths", "dirty", "commits", "occurredAt",
  "eventId", "updatedAt", "telemetryExecutionIds", "telemetryExecutions", "completedAt"
]);

function stripVolatile(value) {
  if (Array.isArray(value)) return value.map(stripVolatile);
  if (value && typeof value === "object") {
    const result = {};
    for (const [key, child] of Object.entries(value)) {
      if (VOLATILE_KEYS.has(key)) continue;
      result[key] = stripVolatile(child);
    }
    return result;
  }
  return value;
}

test("F# work resume matches production's real resume transition for a blocked item with an active execution already linked", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharpRoot = fixture(t, "linked-fsharp");

  runFsharp(fsharpRoot, "start", ["--id", "WI-BLOCKED", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  // The F# side has no `work block` command yet, so its context is driven
  // directly to "blocked" the same way a future `work block` slice would
  // leave it -- this test exercises `work resume` in isolation.
  const contextFile = path.join(fsharpRoot, ".ros", "context", "current.json");
  const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  const item = context.workItems.find((entry) => entry.id === "WI-BLOCKED");
  item.state = "blocked";
  item.semanticState = "blocked";
  item.blockReason = "waiting on review";
  fs.writeFileSync(contextFile, `${JSON.stringify(context, null, 2)}\n`);

  const fsharpResult = runFsharp(fsharpRoot, "resume", ["--id", "WI-BLOCKED", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpContext = readContext(fsharpRoot);
  assert.deepEqual(stripVolatile(fsharpContext), GOLDEN.test1Context);

  const fsharpItem = fsharpContext.workItems.find((entry) => entry.id === "WI-BLOCKED");
  assert.equal(GOLDEN.test1SemanticState, "active");
  assert.equal(fsharpItem.semanticState, "active");
  // Resuming an item with an active execution already linked never creates
  // a new one: the linked execution count stays exactly 1.
  assert.equal(GOLDEN.test1ExecIdsLength, 1);
  assert.equal(fsharpItem.telemetryExecutionIds.length, 1);

  // The Node fixture went through a real `work block` (recording a
  // `work.blocked` event); the F# fixture reached "blocked" by directly
  // editing context.json (F# has no `work block` command yet), so only the
  // `work.started`/`work.resumed` events -- the ones either side actually
  // produced through a real effect -- are compared here.
  const relevantTypes = new Set(["work.started", "work.resumed"]);
  const fsharpEvents = readEvents(fsharpRoot).filter((event) => relevantTypes.has(event.type));
  assert.deepEqual(stripVolatile(fsharpEvents), GOLDEN.test1Events);
  const fsharpResumed = fsharpEvents.find((event) => event.type === "work.resumed");
  assert.ok(fsharpResumed);

  const fsharpExecutions = readExecutions(fsharpRoot);
  assert.equal(GOLDEN.test1ExecutionsLength, 1);
  assert.equal(fsharpExecutions.length, 1);
});

test("F# work resume creates a new telemetry execution when the item has no active one linked, matching production", (t) => {
  const fsharpRoot = fixture(t, "no-active-fsharp");

  // A context item blocked directly from "ready" (never begun) has no
  // telemetry execution linked at all -- not reachable through any current
  // CLI command, so the fixture seeds it directly, matching the established
  // pattern of hand-writing fixture state a real command cannot yet
  // produce.
  const contextFile = path.join(fsharpRoot, ".ros", "context", "current.json");
  const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  context.workItems.push({
    id: "WI-NEVER-BEGUN",
    type: "task",
    state: "blocked",
    semanticState: "blocked",
    evidence: [],
    blockReason: "waiting on a dependency",
    updatedAt: "2026-01-01T00:00:00.000Z"
  });
  context.startedAt ??= "2026-01-01T00:00:00.000Z";
  fs.writeFileSync(contextFile, `${JSON.stringify(context, null, 2)}\n`);

  const fsharpResult = runFsharp(fsharpRoot, "resume", ["--id", "WI-NEVER-BEGUN", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpItem = readContext(fsharpRoot).workItems.find((entry) => entry.id === "WI-NEVER-BEGUN");
  assert.equal(GOLDEN.test2SemanticState, "active");
  assert.equal(fsharpItem.semanticState, "active");
  assert.equal(GOLDEN.test2ExecIdsLength, 1);
  assert.equal(fsharpItem.telemetryExecutionIds.length, 1);

  const fsharpExecutions = readExecutions(fsharpRoot);
  assert.equal(GOLDEN.test2ExecutionsLength, 1);
  assert.equal(fsharpExecutions.length, 1);
  assert.equal(GOLDEN.test2ExecutionsWorkItemId, "WI-NEVER-BEGUN");
  assert.equal(fsharpExecutions[0].workItemId, "WI-NEVER-BEGUN");
});

test("F# work resume rejects an id that is not in the live context, matching production's exact message", (t) => {
  const fsharpRoot = fixture(t, "missing-fsharp");

  const fsharpResult = runFsharp(fsharpRoot, "resume", ["--id", "WI-GHOST", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(GOLDEN.test3Message, /work item 'WI-GHOST' is not in repository context/);
  assert.match(fsharpResult.stderr, /work item 'WI-GHOST' is not in repository context/);
});

test("F# work resume rejects resuming an already-active item with production's exact illegal-transition message", (t) => {
  const fsharpRoot = fixture(t, "active-fsharp");

  runFsharp(fsharpRoot, "start", ["--id", "WI-ACTIVE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  const fsharpResult = runFsharp(fsharpRoot, "resume", ["--id", "WI-ACTIVE", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(GOLDEN.test4Message, /cannot resume 'WI-ACTIVE' from 'active'/);
  assert.match(fsharpResult.stderr, /cannot resume 'WI-ACTIVE' from 'active'/);
});
