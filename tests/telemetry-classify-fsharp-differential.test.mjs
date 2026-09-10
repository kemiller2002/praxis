import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { ingestTelemetry } from "../tools/ros_telemetry.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-classify-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
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

// Mirrors production's `telemetry classify` dispatch construction exactly
// (tools/ros_cli.mjs) -- there is no standalone exported "classify"
// function to import, since production itself inlines this at the CLI
// layer, so this reproduces the same object shape passed to
// `ingestTelemetry` (its own `snapshotId: classification-${Date.now()}` is
// real-clock, not content-addressed, so it is never compared byte-for-byte
// between Node and F#).
function nodeClassify(root, target, { classifications, rationale = null, evidenceLinks = [], rd = null }) {
  return ingestTelemetry(root, target, {
    snapshotId: `classification-${Date.now()}`,
    classification: { types: classifications, rationale, evidence: evidenceLinks, rd },
    raw: {}
  });
}

test("F# telemetry classify builds the same classification shape as production, with rationale/evidence-link/rd-context", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t, "basic-node");
  const fsharpRoot = fixture(t, "basic-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const nodeRecord = nodeClassify(nodeRoot, "EXE-1", {
    classifications: ["research", "documentation"],
    rationale: "testing",
    evidenceLinks: ["https://example.com/a", "https://example.com/b"]
  });

  const fsharpResult = runFsharp(fsharpRoot, [
    "EXE-1", "--classification", "research", "--classification", "documentation",
    "--rationale", "testing", "--evidence-link", "https://example.com/a", "--evidence-link", "https://example.com/b"
  ]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpClassification = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(nodeRecord.classification, fsharpClassification);
});

test("F# telemetry classify merges an --rd-context file's JSON verbatim into the classification's rd field", (t) => {
  const nodeRoot = fixture(t, "rd-node");
  const fsharpRoot = fixture(t, "rd-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const rd = { decisionId: "DEC-1", notes: "some context" };
  const nodeRecord = nodeClassify(nodeRoot, "EXE-1", { classifications: ["research"], rd });

  const rdFile = path.join(os.tmpdir(), `rd-context-${process.pid}-${Math.random().toString(36).slice(2)}.json`);
  fs.writeFileSync(rdFile, JSON.stringify(rd));
  t.after(() => fs.rmSync(rdFile, { force: true }));

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--classification", "research", "--rd-context", rdFile]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpClassification = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(nodeRecord.classification, fsharpClassification);
  assert.deepEqual(fsharpClassification.rd, rd);
});

test("F# telemetry classify rejects a call with no --classification, matching production's exact message", (t) => {
  const nodeRoot = fixture(t, "empty-node");
  const fsharpRoot = fixture(t, "empty-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /telemetry classify requires at least one --classification/);
});

test("F# telemetry classify never deduplicates a repeated call, matching production's real-clock snapshotId", (t) => {
  const nodeRoot = fixture(t, "dedup-node");
  const fsharpRoot = fixture(t, "dedup-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  nodeClassify(nodeRoot, "EXE-1", { classifications: ["research"] });
  nodeClassify(nodeRoot, "EXE-1", { classifications: ["documentation"] });
  const nodeRecord = readExecution(nodeRoot, "EXE-1");

  runFsharp(fsharpRoot, ["EXE-1", "--classification", "research"]);
  runFsharp(fsharpRoot, ["EXE-1", "--classification", "documentation"]);
  const fsharpRecord = readExecution(fsharpRoot, "EXE-1");

  const nodeIngestionEvents = nodeRecord.events.filter((e) => e.type === "telemetry.snapshot.ingested").length;
  const fsharpIngestionEvents = fsharpRecord.events.filter((e) => e.type === "telemetry.snapshot.ingested").length;
  assert.equal(nodeIngestionEvents, 2);
  assert.equal(fsharpIngestionEvents, 2);
  assert.deepEqual(nodeRecord.classification.types, ["documentation"]);
  assert.deepEqual(fsharpRecord.classification.types, ["documentation"]);
});
