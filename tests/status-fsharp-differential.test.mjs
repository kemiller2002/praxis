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

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-status-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Status Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  return root;
}

function ros(root, args) {
  const result = spawnSync(path.join(root, "ros"), args, { cwd: root, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function fsharpStatus(root) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "status"], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function normalize(record) {
  return {
    ...record,
    workItems: record.workItems.map(({ telemetryExecutionIds, ...itemRest }) => ({ ...itemRest, hasExecIds: telemetryExecutionIds.length > 0 }))
  };
}

test("F# status matches production for a clean bootstrap with no findings", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const root = fixture(t, "clean");

  const nodeResult = ros(root, ["status"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = fsharpStatus(root);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeParsed = JSON.parse(nodeResult.stdout);
  const fsharpParsed = JSON.parse(fsharpResult.stdout);
  assert.deepEqual(fsharpParsed, nodeParsed);
  assert.equal(nodeParsed.validation, "passed");
  assert.deepEqual(nodeParsed.nextActions, ["Select an allowed work transition or begin a new work item."]);
});

test("F# status matches production with real active/blocked work items, telemetry counts, and a real finding", (t) => {
  const root = fixture(t, "populated");

  assert.equal(ros(root, ["add", "Ready item", "--id", "WI-READY"]).status, 0);
  assert.equal(ros(root, ["work", "ready", "WI-READY"]).status, 0);
  assert.equal(ros(root, ["work", "start", "WI-READY"]).status, 0);
  assert.equal(ros(root, ["add", "Active item", "--id", "WI-ACTIVE"]).status, 0);
  assert.equal(ros(root, ["work", "ready", "WI-ACTIVE"]).status, 0);
  assert.equal(ros(root, ["work", "start", "WI-ACTIVE"]).status, 0);
  assert.equal(ros(root, ["work", "block", "WI-ACTIVE", "--reason", "waiting"]).status, 0);

  const queuePath = path.join(root, ".ros", "work", "queue.json");
  const queue = JSON.parse(fs.readFileSync(queuePath, "utf8"));
  queue.items.push({
    id: "WI-BAD",
    title: "bad",
    status: "not-a-status",
    tags: [],
    priority: "medium",
    attachments: [],
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
    createdBy: "unknown",
    source: "manual"
  });
  fs.writeFileSync(queuePath, JSON.stringify(queue, null, 2));

  const nodeResult = ros(root, ["status"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = fsharpStatus(root);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeParsed = JSON.parse(nodeResult.stdout);
  const fsharpParsed = JSON.parse(fsharpResult.stdout);
  assert.deepEqual(normalize(fsharpParsed), normalize(nodeParsed));
  assert.equal(nodeParsed.validation, "failed");
  assert.equal(nodeParsed.findingCount, 1);
  assert.equal(nodeParsed.telemetry.executionCount, 2);
  assert.equal(nodeParsed.telemetry.activeExecutionCount, 2);
  assert.equal(nodeParsed.workItems.length, 3);
});
