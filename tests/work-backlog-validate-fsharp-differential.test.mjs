import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { validate } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");
const queueRelativePath = ".ros/work/queue.json";

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-backlog-validate-differential-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Backlog Validate Differential" });

  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.enabled = false;
  config.workProtocol.enforceAttribution = false;
  fs.writeFileSync(configFile, `${JSON.stringify(config, null, 2)}\n`);
  return root;
}

function writeQueue(root, items) {
  fs.writeFileSync(
    path.join(root, queueRelativePath),
    `${JSON.stringify({ schemaVersion: "1.0.0", repository: "repository", nextSeq: items.length + 1, items }, null, 2)}\n`
  );
}

function runFsharp(root) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", "backlog-validate", "--json"], {
    cwd: repositoryRoot,
    encoding: "utf8"
  });
  return { status: result.status, json: result.stdout ? JSON.parse(result.stdout) : null, stderr: result.stderr };
}

function nodeQueueFindings(root) {
  return validate(root, { checkRegistries: false }).filter((finding) => finding.path === queueRelativePath);
}

test("F# work backlog-validate reports no findings for a well-formed queue, matching production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const root = fixture(t);
  writeQueue(root, [
    { id: "WI-0001", status: "ready", priority: "high" },
    { id: "WI-0002", status: "captured" }
  ]);

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json, { valid: true, findings: [] });
  assert.deepEqual(nodeQueueFindings(root), []);
});

test("F# work backlog-validate flags a duplicate id only on its second occurrence, matching production", (t) => {
  const root = fixture(t);
  writeQueue(root, [
    { id: "WI-0001", status: "ready" },
    { id: "WI-0001", status: "blocked" }
  ]);

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 1, fsharp.stderr);

  const node = nodeQueueFindings(root);
  assert.deepEqual(node, [{ path: queueRelativePath, field: "id", message: "duplicate backlog id 'WI-0001'" }]);
  assert.deepEqual(fsharp.json, { valid: false, findings: node.map((finding) => ({ severity: "error", ...finding })) });
});

test("F# work backlog-validate flags an invalid id, matching production", (t) => {
  const root = fixture(t);
  writeQueue(root, [{ id: "not-an-id", status: "ready" }]);

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 1, fsharp.stderr);

  const node = nodeQueueFindings(root);
  assert.deepEqual(node, [{ path: queueRelativePath, field: "id", message: "invalid backlog id 'not-an-id'" }]);
  assert.deepEqual(fsharp.json, { valid: false, findings: node.map((finding) => ({ severity: "error", ...finding })) });
});

test("F# work backlog-validate flags an invalid status, matching production", (t) => {
  const root = fixture(t);
  writeQueue(root, [{ id: "WI-0001", status: "in-progress" }]);

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 1, fsharp.stderr);

  const node = nodeQueueFindings(root);
  assert.deepEqual(node, [{ path: queueRelativePath, field: "status", message: "invalid status 'in-progress' for 'WI-0001'" }]);
  assert.deepEqual(fsharp.json, { valid: false, findings: node.map((finding) => ({ severity: "error", ...finding })) });
});

test("F# work backlog-validate flags an invalid priority, matching production", (t) => {
  const root = fixture(t);
  writeQueue(root, [{ id: "WI-0001", status: "ready", priority: "urgent" }]);

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 1, fsharp.stderr);

  const node = nodeQueueFindings(root);
  assert.deepEqual(node, [{ path: queueRelativePath, field: "priority", message: "invalid priority 'urgent' for 'WI-0001'" }]);
  assert.deepEqual(fsharp.json, { valid: false, findings: node.map((finding) => ({ severity: "error", ...finding })) });
});

test("F# work backlog-validate reports no findings for an absent queue file, matching production", (t) => {
  const root = fixture(t);
  fs.rmSync(path.join(root, queueRelativePath), { force: true });

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json, { valid: true, findings: [] });
  assert.deepEqual(nodeQueueFindings(root), []);
});
