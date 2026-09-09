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
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Resolution Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function ros(root, args) {
  const result = spawnSync(path.join(root, "ros"), args, { cwd: root, encoding: "utf8" });
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

function fsharpResolveTelemetry(state, action, linkedIds, candidates, options = {}) {
  const args = [
    fsharpCli, "work", "plan", "--resolve-telemetry",
    "--id", "TASK-TELEMETRY", "--type", options.type ?? "task",
    "--state", state, "--action", action,
    "--occurred-at", "2026-09-09T00:00:00Z",
    "--telemetry-enabled"
  ];
  for (const id of linkedIds) args.push("--telemetry-id", id);
  for (const candidate of candidates) args.push("--candidate", `${candidate.executionId}=${candidate.status}`);
  if (action === "block") args.push("--reason", options.reason ?? "reason");
  const result = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: JSON.parse(result.stdout), stderr: result.stderr };
}

function resetToReadyWithNoLinkedTelemetry(root, workItemId) {
  writeContextItem(root, workItemId, (item) => {
    item.state = "ready";
    item.semanticState = "ready";
    item.telemetryExecutionIds = [];
  });
}

test("F# telemetry resolution recovers the single orphaned active execution production adopts on begin", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "TASK-TELEMETRY"]).status, 0);
  const original = executionRecords(root, "TASK-TELEMETRY")[0];
  resetToReadyWithNoLinkedTelemetry(root, "TASK-TELEMETRY");

  const again = ros(root, ["work", "begin", "TASK-TELEMETRY"]);
  assert.equal(again.status, 0, again.output);
  const node = contextItem(root, "TASK-TELEMETRY");
  assert.deepEqual(node.telemetryExecutionIds, [original.executionId]);
  assert.equal(executionRecords(root, "TASK-TELEMETRY").length, 1, "begin recovers rather than duplicates");

  const fsharp = fsharpResolveTelemetry("ready", "begin", [], [{ executionId: original.executionId, status: "active" }]);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.equal(fsharp.json.outcome, "resolved");
  assert.deepEqual(fsharp.json.plan.item.telemetryExecutionIds, node.telemetryExecutionIds);
  assert.deepEqual(fsharp.json.plan.event.telemetryExecutions, node.telemetryExecutionIds);
});

test("F# telemetry resolution rejects the same ambiguous orphaned executions production rejects on begin", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "TASK-TELEMETRY"]).status, 0);
  assert.equal(ros(root, ["telemetry", "start", "TASK-TELEMETRY", "--execution-id", "EXE-second-link"]).status, 0);
  const [first, second] = executionRecords(root, "TASK-TELEMETRY");
  resetToReadyWithNoLinkedTelemetry(root, "TASK-TELEMETRY");

  const again = ros(root, ["work", "begin", "TASK-TELEMETRY"]);
  assert.equal(again.status, 1);
  assert.match(again.output, /multiple detached telemetry executions require explicit selection/);

  const fsharp = fsharpResolveTelemetry("ready", "begin", [], [
    { executionId: first.executionId, status: "active" },
    { executionId: second.executionId, status: "active" }
  ]);
  assert.equal(fsharp.status, 1);
  assert.equal(fsharp.json.outcome, "rejected");
  assert.equal(fsharp.json.rejection.reason, "ambiguous");
  assert.deepEqual(fsharp.json.rejection.executionIds, [first.executionId, second.executionId].sort());
});

test("F# telemetry resolution links every currently active execution production links on resume", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "TASK-TELEMETRY"]).status, 0);
  assert.equal(ros(root, ["telemetry", "start", "TASK-TELEMETRY", "--execution-id", "EXE-second-active"]).status, 0);
  assert.equal(ros(root, ["work", "block", "TASK-TELEMETRY", "--reason", "waiting"]).status, 0);
  const candidates = executionRecords(root, "TASK-TELEMETRY").map((record) => ({ executionId: record.executionId, status: record.status }));
  assert.deepEqual(candidates.map((candidate) => candidate.status), ["active", "active"], "block never finalizes");
  writeContextItem(root, "TASK-TELEMETRY", (item) => { item.telemetryExecutionIds = []; });

  const resumed = ros(root, ["work", "resume", "TASK-TELEMETRY"]);
  assert.equal(resumed.status, 0, resumed.output);
  const node = contextItem(root, "TASK-TELEMETRY");
  assert.deepEqual([...node.telemetryExecutionIds].sort(), candidates.map((candidate) => candidate.executionId).sort());

  const fsharp = fsharpResolveTelemetry("blocked", "resume", [], candidates);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual([...fsharp.json.plan.item.telemetryExecutionIds].sort(), [...node.telemetryExecutionIds].sort());
});

test("F# telemetry resolution recovers an orphan then finalizes it on completion, matching production", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "TASK-TELEMETRY"]).status, 0);
  const original = executionRecords(root, "TASK-TELEMETRY")[0];
  writeContextItem(root, "TASK-TELEMETRY", (item) => { item.telemetryExecutionIds = []; });

  const completed = ros(root, ["work", "complete", "TASK-TELEMETRY", "--evidence", `implementation=${path.join(root, "ros.json")}`, "--evidence", `tests=${path.join(root, "ros.json")}`]);
  assert.equal(completed.status, 0, completed.output);
  const node = contextItem(root, "TASK-TELEMETRY");
  assert.deepEqual(node.telemetryExecutionIds, [original.executionId]);
  assert.equal(executionRecords(root, "TASK-TELEMETRY")[0].status, "finalized");

  const fsharp = fsharpResolveTelemetry("active", "complete", [], [{ executionId: original.executionId, status: "active" }]);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json.plan.item.telemetryExecutionIds, node.telemetryExecutionIds);
});

test("F# telemetry resolution finalizes an already-linked execution without re-ensuring it, matching production", (t) => {
  const root = fixture(t);
  assert.equal(ros(root, ["work", "begin", "TASK-TELEMETRY"]).status, 0);
  const original = executionRecords(root, "TASK-TELEMETRY")[0];

  const completed = ros(root, ["work", "complete", "TASK-TELEMETRY", "--evidence", `implementation=${path.join(root, "ros.json")}`, "--evidence", `tests=${path.join(root, "ros.json")}`]);
  assert.equal(completed.status, 0, completed.output);
  const node = contextItem(root, "TASK-TELEMETRY");
  assert.deepEqual(node.telemetryExecutionIds, [original.executionId]);

  const fsharp = fsharpResolveTelemetry("active", "complete", [original.executionId], [
    { executionId: original.executionId, status: "active" }
  ]);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json.plan.item.telemetryExecutionIds, node.telemetryExecutionIds);
});
