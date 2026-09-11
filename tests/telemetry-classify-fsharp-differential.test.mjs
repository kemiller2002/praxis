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

// Golden masters below were captured once from production's own Node
// implementation (tools/ros_telemetry.mjs's ingestTelemetry, invoked the same
// way tools/ros_cli.mjs's `telemetry classify` dispatch itself constructs the
// call) with the exact same call sequence as each test, then frozen here.
// Node is retained in this repository only as the web server's internal
// dependency (DF-ROS-2026-A033) and is no longer executed as a live oracle
// by this test suite.
const GOLDEN = {
  test1Classification: {
    types: ["research", "documentation"],
    rationale: "testing",
    evidence: ["https://example.com/a", "https://example.com/b"],
    rd: null
  },
  test2Classification: {
    types: ["research"],
    rationale: null,
    evidence: [],
    rd: { decisionId: "DEC-1", notes: "some context" }
  }
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-classify-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));
  initializeProject({ target: root, project: "Telemetry Classify Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function writeFixtureExecution(root, executionId, workItemId, status, startedAt) {
  const directory = path.join(root, ".ros", "telemetry", "executions");
  fs.mkdirSync(directory, { recursive: true });
  const record = {
    schemaVersion: "1.0.0",
    executionId,
    workItemId,
    status,
    startedAt,
    identity: { provider: "anthropic", runtime: "claude-code" },
    provenance: { collector: "ros", collectorVersion: "1.0.0", discoveredAt: startedAt, sources: [] },
    classification: { types: ["development"], rationale: null, evidence: [], rd: null },
    capabilities: [],
    metrics: [],
    rawTelemetry: [],
    events: [],
    repository: { start: { available: false } },
    scope: {},
    qualitySignals: [],
    links: {}
  };
  fs.writeFileSync(path.join(directory, `${executionId}.json`), JSON.stringify(record, null, 2));
}

function readExecution(root, executionId) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "telemetry", "executions", `${executionId}.json`), "utf8"));
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", "classify", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test("F# telemetry classify builds the same classification shape as production, with rationale/evidence-link/rd-context", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharpRoot = fixture(t, "basic-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const fsharpResult = runFsharp(fsharpRoot, [
    "EXE-1", "--classification", "research", "--classification", "documentation",
    "--rationale", "testing", "--evidence-link", "https://example.com/a", "--evidence-link", "https://example.com/b"
  ]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpClassification = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(GOLDEN.test1Classification, fsharpClassification);
});

test("F# telemetry classify merges an --rd-context file's JSON verbatim into the classification's rd field", (t) => {
  const fsharpRoot = fixture(t, "rd-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const rd = { decisionId: "DEC-1", notes: "some context" };
  const rdFile = path.join(os.tmpdir(), `rd-context-${process.pid}-${Math.random().toString(36).slice(2)}.json`);
  fs.writeFileSync(rdFile, JSON.stringify(rd));
  t.after(() => fs.rmSync(rdFile, { force: true }));

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--classification", "research", "--rd-context", rdFile]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpClassification = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(GOLDEN.test2Classification, fsharpClassification);
  assert.deepEqual(fsharpClassification.rd, rd);
});

test("F# telemetry classify rejects a call with no --classification, matching production's exact message", (t) => {
  const fsharpRoot = fixture(t, "empty-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /telemetry classify requires at least one --classification/);
});

test("F# telemetry classify never deduplicates a repeated call, matching production's real-clock snapshotId", (t) => {
  const fsharpRoot = fixture(t, "dedup-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  runFsharp(fsharpRoot, ["EXE-1", "--classification", "research"]);
  runFsharp(fsharpRoot, ["EXE-1", "--classification", "documentation"]);
  const fsharpRecord = readExecution(fsharpRoot, "EXE-1");

  const fsharpIngestionEvents = fsharpRecord.events.filter((e) => e.type === "telemetry.snapshot.ingested").length;
  assert.equal(fsharpIngestionEvents, 2);
  assert.deepEqual(fsharpRecord.classification.types, ["documentation"]);
});
