import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-telemetry-resolution-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));
  initializeProject({ target: root, project: "Telemetry Resolution Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, output: `${result.stdout ?? ""}${result.stderr ?? ""}` };
}

function contextItem(root, workItemId) {
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  return context.workItems.find((item) => item.id === workItemId);
}

function writeContextItem(root, workItemId, updater) {
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
  updater(context.workItems.find((item) => item.id === workItemId));
  fs.writeFileSync(contextFile, `${JSON.stringify(context, null, 2)}\n`);
}

function executionRecords(root, workItemId) {
  const directory = path.join(root, ".ros", "telemetry", "executions");
  if (!fs.existsSync(directory)) return [];
  return fs
    .readdirSync(directory)
    .filter((name) => name.endsWith(".json"))
    .map((name) => JSON.parse(fs.readFileSync(path.join(directory, name), "utf8")))
    .filter((record) => record.workItemId === workItemId)
    .sort((a, b) => a.executionId.localeCompare(b.executionId));
}

// No --candidate flags: this reads the same .ros/telemetry/executions/*.json
// files under `root` that production wrote, through the real
// FileTelemetryStateRepository Infrastructure port, rather than a synthetic
// simulation of them.
function fsharpResolveTelemetry(root, state, action, linkedIds, options = {}) {
  const args = [
    "--root", root, "work", "plan", "--resolve-telemetry",
    "--id", "TASK-TELEMETRY", "--type", options.type ?? "task",
    "--state", state, "--action", action,
    "--occurred-at", "2026-09-09T00:00:00Z",
    "--telemetry-enabled"
  ];
  for (const id of linkedIds) args.push("--telemetry-id", id);
  if (action === "block") args.push("--reason", options.reason ?? "reason");
  const result = spawnSync("dotnet", [fsharpCli, ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: JSON.parse(result.stdout), stderr: result.stderr };
}

function resetToReadyWithNoLinkedTelemetry(root, workItemId) {
  writeContextItem(root, workItemId, (item) => {
    item.state = "ready";
    item.semanticState = "ready";
    item.telemetryExecutionIds = [];
  });
}

// Real (not backdated) timestamps: a new execution's own startedAt is always
// production's real wall clock, so a synthetic --occurred-at earlier than
// "now" on a later transition could spuriously fail a chronological-order
// check elsewhere.
const at = () => new Date().toISOString();

test("F# telemetry resolution recovers the single orphaned active execution production adopts on begin", (t) => {
  const root = fixture(t);
  assert.equal(runFsharp(root, ["work", "begin", "--id", "TASK-TELEMETRY", "--occurred-at", at()]).status, 0);
  const original = executionRecords(root, "TASK-TELEMETRY")[0];
  resetToReadyWithNoLinkedTelemetry(root, "TASK-TELEMETRY");

  const again = runFsharp(root, ["work", "begin", "--id", "TASK-TELEMETRY", "--occurred-at", at()]);
  assert.equal(again.status, 0, again.output);
  const committed = contextItem(root, "TASK-TELEMETRY");
  assert.deepEqual(committed.telemetryExecutionIds, [original.executionId]);
  assert.equal(executionRecords(root, "TASK-TELEMETRY").length, 1, "begin recovers rather than duplicates");

  const fsharp = fsharpResolveTelemetry(root, "ready", "begin", []);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.equal(fsharp.json.outcome, "resolved");
  assert.deepEqual(fsharp.json.plan.item.telemetryExecutionIds, committed.telemetryExecutionIds);
  assert.deepEqual(fsharp.json.plan.event.telemetryExecutions, committed.telemetryExecutionIds);
});

test("F# telemetry resolution rejects the same ambiguous orphaned executions production rejects on begin", (t) => {
  const root = fixture(t);
  assert.equal(runFsharp(root, ["work", "begin", "--id", "TASK-TELEMETRY", "--occurred-at", at()]).status, 0);
  assert.equal(runFsharp(root, ["telemetry", "start", "TASK-TELEMETRY", "--execution-id", "EXE-second-link"]).status, 0);
  const [first, second] = executionRecords(root, "TASK-TELEMETRY");
  resetToReadyWithNoLinkedTelemetry(root, "TASK-TELEMETRY");

  const again = runFsharp(root, ["work", "begin", "--id", "TASK-TELEMETRY", "--occurred-at", at()]);
  assert.equal(again.status, 1);
  assert.match(again.output, /multiple detached telemetry executions require explicit selection/);

  const fsharp = fsharpResolveTelemetry(root, "ready", "begin", []);
  assert.equal(fsharp.status, 1);
  assert.equal(fsharp.json.outcome, "rejected");
  assert.equal(fsharp.json.rejection.reason, "ambiguous");
  assert.deepEqual(fsharp.json.rejection.executionIds, [first.executionId, second.executionId].sort());
});

test("F# telemetry resolution links every currently active execution production links on resume", (t) => {
  const root = fixture(t);
  assert.equal(runFsharp(root, ["work", "begin", "--id", "TASK-TELEMETRY", "--occurred-at", at()]).status, 0);
  assert.equal(runFsharp(root, ["telemetry", "start", "TASK-TELEMETRY", "--execution-id", "EXE-second-active"]).status, 0);
  assert.equal(runFsharp(root, ["work", "block", "--id", "TASK-TELEMETRY", "--reason", "waiting", "--occurred-at", at()]).status, 0);
  const candidateStatuses = executionRecords(root, "TASK-TELEMETRY").map((record) => record.status);
  assert.deepEqual(candidateStatuses, ["active", "active"], "block never finalizes");
  writeContextItem(root, "TASK-TELEMETRY", (item) => { item.telemetryExecutionIds = []; });

  const resumed = runFsharp(root, ["work", "resume", "--id", "TASK-TELEMETRY", "--occurred-at", at()]);
  assert.equal(resumed.status, 0, resumed.output);
  const committed = contextItem(root, "TASK-TELEMETRY");

  const fsharp = fsharpResolveTelemetry(root, "blocked", "resume", []);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual([...fsharp.json.plan.item.telemetryExecutionIds].sort(), [...committed.telemetryExecutionIds].sort());
});

test("F# telemetry resolution recovers an orphan then finalizes it on completion, matching production", (t) => {
  const root = fixture(t);
  assert.equal(runFsharp(root, ["work", "begin", "--id", "TASK-TELEMETRY", "--occurred-at", at()]).status, 0);
  const original = executionRecords(root, "TASK-TELEMETRY")[0];
  writeContextItem(root, "TASK-TELEMETRY", (item) => { item.telemetryExecutionIds = []; });

  const completed = runFsharp(root, ["work", "complete", "--id", "TASK-TELEMETRY", "--evidence", `implementation=${path.join(root, "ros.json")}`, "--evidence", `tests=${path.join(root, "ros.json")}`, "--occurred-at", at()]);
  assert.equal(completed.status, 0, completed.output);
  const committed = contextItem(root, "TASK-TELEMETRY");
  assert.deepEqual(committed.telemetryExecutionIds, [original.executionId]);
  assert.equal(executionRecords(root, "TASK-TELEMETRY")[0].status, "finalized");

  // Re-resolve with an empty linked set against the now-finalized real
  // record: ensure-completable's recover step (which allows Active or
  // Finalized candidates) recovers it, landing on the same final ID list
  // production reached via recover-then-finalize.
  const fsharp = fsharpResolveTelemetry(root, "active", "complete", []);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json.plan.item.telemetryExecutionIds, committed.telemetryExecutionIds);
});

test("F# telemetry resolution finalizes an already-linked execution without re-ensuring it, matching production", (t) => {
  const root = fixture(t);
  assert.equal(runFsharp(root, ["work", "begin", "--id", "TASK-TELEMETRY", "--occurred-at", at()]).status, 0);
  const original = executionRecords(root, "TASK-TELEMETRY")[0];

  const completed = runFsharp(root, ["work", "complete", "--id", "TASK-TELEMETRY", "--evidence", `implementation=${path.join(root, "ros.json")}`, "--evidence", `tests=${path.join(root, "ros.json")}`, "--occurred-at", at()]);
  assert.equal(completed.status, 0, completed.output);
  const committed = contextItem(root, "TASK-TELEMETRY");
  assert.deepEqual(committed.telemetryExecutionIds, [original.executionId]);

  // original.executionId is already linked, so ensure-completable is
  // skipped; finalize re-scans for active candidates and finds none (it is
  // already finalized on disk), leaving the linked ID unchanged.
  const fsharp = fsharpResolveTelemetry(root, "active", "complete", [original.executionId]);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json.plan.item.telemetryExecutionIds, committed.telemetryExecutionIds);
});
