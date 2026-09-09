import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { validate } from "../tools/ros_cli.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function contextFile(root) {
  return path.join(root, ".ros", "context", "current.json");
}

function readContext(root) {
  return JSON.parse(fs.readFileSync(contextFile(root), "utf8"));
}

function writeContext(root, context) {
  fs.writeFileSync(contextFile(root), `${JSON.stringify(context, null, 2)}\n`);
}

function fixture(t, options = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-work-attribution-differential-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Work Attribution Differential" });

  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.enabled = false;
  config.workProtocol.enforceAttribution = options.enforce ?? true;
  fs.writeFileSync(configFile, `${JSON.stringify(config, null, 2)}\n`);

  writeContext(root, {
    schemaVersion: "1.0.0",
    repository: config.repository.id,
    workItems: options.workItems ?? [],
    baselineDirtyPaths: options.baselineDirtyPaths ?? []
  });

  execFileSync("git", ["-C", root, "init", "-q"]);
  execFileSync("git", ["-C", root, "config", "user.email", "test@example.invalid"]);
  execFileSync("git", ["-C", root, "config", "user.name", "ROS Test"]);
  execFileSync("git", ["-C", root, "add", "."]);
  execFileSync("git", ["-C", root, "commit", "-qm", "baseline"]);
  return root;
}

function runFsharp(root) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", "validate", "--json"], {
    cwd: repositoryRoot,
    encoding: "utf8"
  });
  return { status: result.status, json: result.stdout ? JSON.parse(result.stdout) : null, stderr: result.stderr };
}

function nodeWorkFindings(root) {
  return validate(root, { checkRegistries: false }).filter((finding) => finding.field === "work_items");
}

test("F# work validate reports no findings when enforcement is disabled, matching production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const root = fixture(t, { enforce: false });
  fs.writeFileSync(path.join(root, "unattributed.txt"), "no enforcement\n");

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json, { valid: true, findings: [] });
  assert.deepEqual(nodeWorkFindings(root), []);
});

test("F# work validate reports no findings when nothing meaningful changed, matching production", (t) => {
  const root = fixture(t);
  fs.writeFileSync(path.join(root, ".ros", "events", "housekeeping-ignored.json"), "{}\n");

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json, { valid: true, findings: [] });
  assert.deepEqual(nodeWorkFindings(root), []);
});

test("F# work validate flags an unattributed meaningful change, matching production", (t) => {
  const root = fixture(t);
  fs.writeFileSync(path.join(root, "src.txt"), "meaningful, unattributed change\n");

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 1, fsharp.stderr);

  const node = nodeWorkFindings(root);
  assert.deepEqual(node, [
    { path: "src.txt", field: "work_items", message: "meaningful change has no active or completed work-item attribution" }
  ]);
  assert.deepEqual(fsharp.json, { valid: false, findings: node.map((finding) => ({ severity: "error", ...finding })) });
});

test("F# work validate excuses a change already present in the baseline, matching production", (t) => {
  const root = fixture(t, { baselineDirtyPaths: ["src.txt"] });
  fs.writeFileSync(path.join(root, "src.txt"), "already dirty at baseline capture\n");

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json, { valid: true, findings: [] });
  assert.deepEqual(nodeWorkFindings(root), []);
});

test("F# work validate treats an event-logged path as attributed, matching production", (t) => {
  const root = fixture(t);
  fs.writeFileSync(path.join(root, "src.txt"), "meaningful, attributed change\n");
  fs.appendFileSync(
    path.join(root, ".ros", "events", "events.jsonl"),
    `${JSON.stringify({ type: "work.completed", paths: ["src.txt"] })}\n`
  );

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json, { valid: true, findings: [] });
  assert.deepEqual(nodeWorkFindings(root), []);
});

test("F# work validate excuses every meaningful change while work is active or blocked, matching production", (t) => {
  const root = fixture(t, {
    workItems: [
      {
        id: "TASK-ACTIVE",
        type: "task",
        state: "active",
        semanticState: "active",
        evidence: [],
        telemetryExecutionIds: []
      }
    ]
  });
  fs.writeFileSync(path.join(root, "src.txt"), "meaningful, excused by active work\n");

  const fsharp = runFsharp(root);
  assert.equal(fsharp.status, 0, fsharp.stderr);
  assert.deepEqual(fsharp.json, { valid: true, findings: [] });
  assert.deepEqual(nodeWorkFindings(root), []);
});
