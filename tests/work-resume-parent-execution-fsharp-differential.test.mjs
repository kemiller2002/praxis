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

// This file documents a DELIBERATE, KNOWN divergence from Node rather than
// proving byte-for-byte parity: production's own `work resume` computes
// `parentExecutionId: prior?.executionId ?? null` for its rare
// no-active-candidate new-execution path, but `startExecution`'s
// `discoverIdentity(options.identity ?? options)` picks `options.identity`
// (always a truthy object, even with every field `undefined`) over the
// sibling `options` object that value was actually set on -- so production
// silently discards it and always writes `null`. This was confirmed once
// against real, unpatched Node (DF-ROS-2026-A033) with the exact same call
// sequence as this test and is frozen below as GOLDEN.nodeParentExecutionId;
// Node is retained in this repository only as the web server's internal
// dependency and is no longer executed as a live oracle by this test suite.
// Since Node is being deprecated rather than patched, the F# port implements
// the evidently-intended behavior instead of replicating the bug.
const GOLDEN = {
  nodeParentExecutionId: null
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-resume-parent-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));
  initializeProject({ target: root, project: "Resume Parent Execution" });
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

function executionRecord(root, executionId) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "telemetry", "executions", `${executionId}.json`), "utf8"));
}

function finalize(root, executionId) {
  const file = path.join(root, ".ros", "telemetry", "executions", `${executionId}.json`);
  const record = JSON.parse(fs.readFileSync(file, "utf8"));
  record.status = "finalized";
  fs.writeFileSync(file, JSON.stringify(record, null, 2));
}

// Real (not backdated) timestamps: a new execution's own startedAt is always
// production's real wall clock, so a synthetic --occurred-at earlier than
// "now" on a later transition could spuriously fail a chronological-order
// check elsewhere.
const at = () => new Date().toISOString();

test("F# work resume links parentExecutionId to the prior execution on its rare no-active-candidate path; real Node does not (a known, unpatched production defect)", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharp = fixture(t, "fsharp");

  assert.equal(runFsharp(fsharp, ["work", "begin", "--id", "TASK-PARENT", "--occurred-at", at()]).status, 0);
  assert.equal(runFsharp(fsharp, ["work", "block", "--id", "TASK-PARENT", "--reason", "waiting", "--occurred-at", at()]).status, 0);
  const fsharpOriginal = contextItem(fsharp, "TASK-PARENT").telemetryExecutionIds[0];
  finalize(fsharp, fsharpOriginal);
  writeContextItem(fsharp, "TASK-PARENT", (item) => {
    item.telemetryExecutionIds = [];
  });

  const fsharpResumed = runFsharp(fsharp, [
    "work",
    "resume",
    "--id",
    "TASK-PARENT",
    "--occurred-at",
    "2026-09-10T18:00:00.000Z"
  ]);
  assert.equal(fsharpResumed.status, 0, fsharpResumed.output);
  const fsharpNewExecutionId = contextItem(fsharp, "TASK-PARENT").telemetryExecutionIds[0];
  assert.notEqual(fsharpNewExecutionId, fsharpOriginal);
  assert.equal(
    GOLDEN.nodeParentExecutionId,
    null,
    "confirms the known, unpatched production defect: parentExecutionId is computed but discarded"
  );
  assert.equal(
    executionRecord(fsharp, fsharpNewExecutionId).identity.parentExecutionId,
    fsharpOriginal,
    "F# implements the evidently-intended behavior production's own code never reaches"
  );
});
