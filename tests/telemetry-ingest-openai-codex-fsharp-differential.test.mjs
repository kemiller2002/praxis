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
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-ingest-codex-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Ingest Codex Differential" });
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
    identity: { provider: "unknown", runtime: "unknown" },
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

function writeInputFile(t, name, value) {
  const file = path.join(os.tmpdir(), `${name}-${process.pid}-${Math.random().toString(36).slice(2)}.json`);
  fs.writeFileSync(file, typeof value === "string" ? value : JSON.stringify(value));
  t.after(() => fs.rmSync(file, { force: true }));
  return file;
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", "ingest", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function readExecution(root, executionId) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "telemetry", "executions", `${executionId}.json`), "utf8"));
}

function normIdentity(record) {
  return { provider: record.identity.provider, runtime: record.identity.runtime, model: record.identity.model };
}

function normCapabilities(record) {
  return record.capabilities.map((c) => ({ metricId: c.metricId, status: c.status, reason: c.reason })).sort((a, b) => (a.metricId ?? "").localeCompare(b.metricId ?? ""));
}

function normMetrics(record) {
  return record.metrics
    .map((m) => ({ id: m.id, value: m.value, scope: m.scope ?? null, dimensions: JSON.stringify(m.dimensions ?? null) }))
    .sort((a, b) => a.id.localeCompare(b.id) || a.value - b.value);
}

function normEvents(record) {
  return record.events.map((e) => e.type).sort();
}

test("F# telemetry ingest --adapter openai-codex maps identity/capabilities/metrics/events identically to production's own adaptOpenAICodex", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t, "basic-node");
  const fsharpRoot = fixture(t, "basic-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = [
    { type: "session.started", timestamp: "2026-01-01T00:00:00.000Z", model: "gpt-5-codex" },
    {
      type: "turn.completed",
      timestamp: "2026-01-01T00:00:05.000Z",
      server_model: "gpt-5-codex-2026",
      usage: { input_tokens: 100, output_tokens: 50, cached_input_tokens: 20, total_tokens: 150, model_context_window: 128000 }
    },
    {
      type: "turn.completed",
      timestamp: "2026-01-01T00:00:10.000Z",
      usage: { input_tokens: 200, output_tokens: 75, reasoning_output_tokens: 30 }
    }
  ];

  const nodeRecord = ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "openai-codex" });

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "codex-basic", input), "--adapter", "openai-codex"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(normIdentity(nodeRecord), normIdentity(fsharpRecord));
  assert.deepEqual(normCapabilities(nodeRecord), normCapabilities(fsharpRecord));
  assert.deepEqual(normMetrics(nodeRecord), normMetrics(fsharpRecord));
  assert.deepEqual(normEvents(nodeRecord), normEvents(fsharpRecord));

  const nodeStored = readExecution(nodeRoot, "EXE-1");
  const fsharpStored = readExecution(fsharpRoot, "EXE-1");
  assert.deepEqual(normCapabilities(nodeStored), normCapabilities(fsharpStored));
  assert.deepEqual(normMetrics(nodeStored), normMetrics(fsharpStored));
});

test("F# telemetry ingest --adapter openai-codex resolves identity.model from the last record carrying one, in reverse order, matching production", (t) => {
  const nodeRoot = fixture(t, "identity-node");
  const fsharpRoot = fixture(t, "identity-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = [
    { type: "turn.completed", timestamp: "2026-01-01T00:00:05.000Z", server_model: "gpt-early", usage: { input_tokens: 1 } },
    { type: "session.info", timestamp: "2026-01-01T00:00:06.000Z", model: "gpt-late" }
  ];

  const nodeRecord = ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "openai-codex" });
  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "codex-identity", input), "--adapter", "openai-codex"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeRecord.identity.model, "gpt-late");
  assert.equal(fsharpRecord.identity.model, "gpt-late");
});

test("F# telemetry ingest --adapter openai-codex records an unknown usage field as a discovered raw capability, matching production", (t) => {
  const nodeRoot = fixture(t, "unknown-node");
  const fsharpRoot = fixture(t, "unknown-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = [{ type: "turn.completed", timestamp: "2026-01-01T00:00:05.000Z", usage: { input_tokens: 5, unknown_extra_field: 999 } }];

  ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "openai-codex" });
  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "codex-unknown", input), "--adapter", "openai-codex"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const nodeStored = readExecution(nodeRoot, "EXE-1");
  const fsharpStored = readExecution(fsharpRoot, "EXE-1");
  const nodeDiscovered = nodeStored.rawTelemetry[0]?.discoveredFields ?? [];
  const fsharpDiscovered = fsharpStored.rawTelemetry[0]?.discoveredFields ?? [];
  assert.deepEqual(nodeDiscovered.sort(), fsharpDiscovered.sort());
  assert.deepEqual(nodeDiscovered.sort(), ["$[].usage.unknown_extra_field"]);
});
