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

// Golden masters below were captured once from production's own Node `work
// context` command with the exact same call sequence as each test, then
// frozen here. Node is retained in this repository only as the web server's
// internal dependency (DF-ROS-2026-A033) and is no longer executed as a live
// oracle by this test suite.
const GOLDEN = {
  list: {
    schemaVersion: "1.0.0",
    protocolVersion: "1.0.0",
    repository: "work-context-differential",
    actor: "ros-bootstrap",
    workItems: [
      {
        id: installWorkItemId,
        type: "mechanical",
        state: "complete",
        semanticState: "complete",
        evidence: [{ type: "installation", path: ".ros/installation.json" }],
        allowedActions: [],
        requiredEvidenceForCompletion: [],
        hasExecIds: false
      },
      {
        id: "WI-READY",
        type: "task",
        state: "active",
        semanticState: "active",
        evidence: [],
        allowedActions: ["block", "complete"],
        requiredEvidenceForCompletion: ["implementation", "tests"],
        hasExecIds: true
      },
      {
        id: "WI-ACTIVE",
        type: "task",
        state: "blocked",
        semanticState: "blocked",
        evidence: [],
        blockReason: "waiting",
        allowedActions: ["resume"],
        requiredEvidenceForCompletion: ["implementation", "tests"],
        hasExecIds: true
      }
    ]
  },
  single: {
    schemaVersion: "1.0.0",
    protocolVersion: "1.0.0",
    repository: "work-context-differential",
    actor: "ros-bootstrap",
    workItems: [
      {
        id: "WI-ACTIVE",
        type: "task",
        state: "active",
        semanticState: "active",
        evidence: [],
        allowedActions: ["block", "complete"],
        requiredEvidenceForCompletion: ["implementation", "tests"],
        hasExecIds: true
      }
    ]
  }
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-context-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));
  initializeProject({ target: root, project: "Work Context Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  return root;
}

function fsharpWork(root, command, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", command, ...args], { cwd: repositoryRoot, encoding: "utf8" });
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

// Real (not backdated) timestamps: a new execution's own startedAt is always
// production's real wall clock, so a synthetic --occurred-at earlier than
// "now" on a later transition could spuriously fail a chronological-order
// check elsewhere.
const at = () => new Date().toISOString();

test("F# work context (no ID) returns every work item with allowedActions/requiredEvidenceForCompletion identical to production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharp = fixture(t, "list-fsharp");

  assert.equal(fsharpWork(fsharp, "capture", ["--id", "WI-READY", "--title", "Ready item", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(fsharp, "backlog-transition", ["--id", "WI-READY", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(fsharp, "start", ["--id", "WI-READY", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(fsharp, "capture", ["--id", "WI-ACTIVE", "--title", "Active item", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(fsharp, "backlog-transition", ["--id", "WI-ACTIVE", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(fsharp, "start", ["--id", "WI-ACTIVE", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(fsharp, "block", ["--id", "WI-ACTIVE", "--reason", "waiting", "--occurred-at", at()]).status, 0);

  const fsharpResult = runFsharp(fsharp, []);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(normView(JSON.parse(fsharpResult.stdout)), GOLDEN.list);
});

test("F# work context ID filters to the requested item, preserving unmodeled fields, identical to production", (t) => {
  const fsharp = fixture(t, "single-fsharp");

  assert.equal(fsharpWork(fsharp, "capture", ["--id", "WI-ACTIVE", "--title", "Active item", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(fsharp, "backlog-transition", ["--id", "WI-ACTIVE", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(fsharp, "start", ["--id", "WI-ACTIVE", "--occurred-at", at()]).status, 0);

  const fsharpResult = runFsharp(fsharp, ["WI-ACTIVE"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(normView(JSON.parse(fsharpResult.stdout)), GOLDEN.single);
});

test("F# work context rejects an unknown ID with production's exact message", (t) => {
  const fsharp = fixture(t, "unknown-fsharp");

  const fsharpResult = runFsharp(fsharp, ["WI-NOPE"]);

  assert.equal(fsharpResult.status, 1);
  const expected = "work item 'WI-NOPE' is not in repository context";
  assert.equal(fsharpResult.stderr.trim(), `ERROR ${expected}`);
});
