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
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-context-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Work Context Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  return root;
}

function ros(root, args) {
  const result = spawnSync("node", [path.join(root, "tools", "ros_cli.mjs"), ...args], { cwd: root, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", "context", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function normView(record) {
  const items = record.workItems.map((item) => {
    const { updatedAt, completedAt, telemetryExecutionIds, ...rest } = item;
    return { ...rest, hasExecIds: Array.isArray(telemetryExecutionIds) };
  });
  return { schemaVersion: record.schemaVersion, protocolVersion: record.protocolVersion, repository: record.repository, actor: record.actor, workItems: items };
}

test("F# work context (no ID) returns every work item with allowedActions/requiredEvidenceForCompletion identical to production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const node = fixture(t, "list-node");
  const fsharp = fixture(t, "list-fsharp");

  for (const root of [node, fsharp]) {
    assert.equal(ros(root, ["add", "Ready item", "--id", "WI-READY"]).status, 0);
    assert.equal(ros(root, ["work", "ready", "WI-READY"]).status, 0);
    assert.equal(ros(root, ["work", "start", "WI-READY"]).status, 0);
    assert.equal(ros(root, ["add", "Active item", "--id", "WI-ACTIVE"]).status, 0);
    assert.equal(ros(root, ["work", "ready", "WI-ACTIVE"]).status, 0);
    assert.equal(ros(root, ["work", "start", "WI-ACTIVE"]).status, 0);
    assert.equal(ros(root, ["work", "block", "WI-ACTIVE", "--reason", "waiting"]).status, 0);
  }

  const nodeResult = ros(node, ["work", "context"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = runFsharp(fsharp, []);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(normView(JSON.parse(nodeResult.stdout)), normView(JSON.parse(fsharpResult.stdout)));
});

test("F# work context ID filters to the requested item, preserving unmodeled fields, identical to production", (t) => {
  const node = fixture(t, "single-node");
  const fsharp = fixture(t, "single-fsharp");

  for (const root of [node, fsharp]) {
    assert.equal(ros(root, ["add", "Active item", "--id", "WI-ACTIVE"]).status, 0);
    assert.equal(ros(root, ["work", "ready", "WI-ACTIVE"]).status, 0);
    assert.equal(ros(root, ["work", "start", "WI-ACTIVE"]).status, 0);
  }

  const nodeResult = ros(node, ["work", "context", "WI-ACTIVE"]);
  assert.equal(nodeResult.status, 0, nodeResult.stderr);
  const fsharpResult = runFsharp(fsharp, ["WI-ACTIVE"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(normView(JSON.parse(nodeResult.stdout)), normView(JSON.parse(fsharpResult.stdout)));
});

test("F# work context rejects an unknown ID with production's exact message", (t) => {
  const node = fixture(t, "unknown-node");
  const fsharp = fixture(t, "unknown-fsharp");

  const nodeResult = ros(node, ["work", "context", "WI-NOPE"]);
  const fsharpResult = runFsharp(fsharp, ["WI-NOPE"]);

  assert.equal(nodeResult.status, 1);
  assert.equal(fsharpResult.status, 1);
  const expected = "work item 'WI-NOPE' is not in repository context";
  assert.match(nodeResult.stderr, new RegExp(expected.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});
