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
// implementation (tools/ros_telemetry.mjs's ingestTelemetry with adapter
// "anthropic-claude-statusline") with the exact same call sequence as each
// test, then frozen here. Node is retained in this repository only as the
// web server's internal dependency (DF-ROS-2026-A033) and is no longer
// executed as a live oracle by this test suite.
const GOLDEN = {
  test1Identity: {
    provider: "anthropic",
    runtime: "claude-code",
    runtimeVersion: "1.2.3",
    model: "claude-opus-5",
    sessionId: "sess-abc",
    agentId: "explore-agent"
  },
  test1Capabilities: [
    { metricId: "context.current_cache_read_tokens", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.current_cache_write_tokens", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.current_input_tokens", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.current_output_tokens", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.utilization", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.window_size", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "cost.session_cumulative", status: "estimated", reason: "normalized measurement recorded" }
  ],
  test1Metrics: [
    { id: "context.current_cache_read_tokens", value: 300, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "context.current_cache_write_tokens", value: 50, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "context.current_input_tokens", value: 1000, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "context.current_output_tokens", value: 200, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "context.utilization", value: 0.425, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "context.window_size", value: 200000, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "cost.session_cumulative", value: 1.2345, scope: "session", quality: "estimated", confidence: "medium", currency: "USD" }
  ],
  test1Events: ["telemetry.snapshot.ingested"],
  test1StoredCapabilities: [
    { metricId: "context.current_cache_read_tokens", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.current_cache_write_tokens", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.current_input_tokens", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.current_output_tokens", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.utilization", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.window_size", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "cost.session_cumulative", status: "estimated", reason: "normalized measurement recorded" }
  ],
  test1StoredMetrics: [
    { id: "context.current_cache_read_tokens", value: 300, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "context.current_cache_write_tokens", value: 50, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "context.current_input_tokens", value: 1000, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "context.current_output_tokens", value: 200, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "context.utilization", value: 0.425, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "context.window_size", value: 200000, scope: "session", quality: "observed", confidence: null, currency: null },
    { id: "cost.session_cumulative", value: 1.2345, scope: "session", quality: "estimated", confidence: "medium", currency: "USD" }
  ],
  test2Capabilities: [
    { metricId: "context.current_cache_read_tokens", status: "supported-unavailable", reason: "adapter recognizes the field but it was unavailable in this snapshot" },
    { metricId: "context.current_cache_write_tokens", status: "supported-unavailable", reason: "adapter recognizes the field but it was unavailable in this snapshot" },
    { metricId: "context.current_input_tokens", status: "supported-unavailable", reason: "adapter recognizes the field but it was unavailable in this snapshot" },
    { metricId: "context.current_output_tokens", status: "supported-unavailable", reason: "adapter recognizes the field but it was unavailable in this snapshot" },
    { metricId: "context.utilization", status: "supported-unavailable", reason: "adapter recognizes the field but it was unavailable in this snapshot" },
    { metricId: "context.window_size", status: "supported-unavailable", reason: "adapter recognizes the field but it was unavailable in this snapshot" },
    { metricId: "cost.session_cumulative", status: "supported-unavailable", reason: "adapter recognizes the field but it was unavailable in this snapshot" }
  ],
  test3CostStatus: "estimated"
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-ingest-statusline-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));
  initializeProject({ target: root, project: "Telemetry Ingest Claude Statusline Differential" });
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
  return { ...record.identity };
}

function normCapabilities(record) {
  return record.capabilities.map((c) => ({ metricId: c.metricId, status: c.status, reason: c.reason })).sort((a, b) => (a.metricId ?? "").localeCompare(b.metricId ?? ""));
}

function normMetrics(record) {
  return record.metrics
    .map((m) => ({ id: m.id, value: m.value, scope: m.scope ?? null, quality: m.quality ?? null, confidence: m.confidence ?? null, currency: m.currency ?? null }))
    .sort((a, b) => a.id.localeCompare(b.id) || a.value - b.value);
}

function normEvents(record) {
  return record.events.map((e) => e.type).sort();
}

test("F# telemetry ingest --adapter anthropic-claude-statusline maps identity/capabilities/metrics/events identically to production's own adaptClaudeStatusline", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharpRoot = fixture(t, "basic-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = {
    version: "1.2.3",
    session_id: "sess-abc",
    model: { id: "claude-opus-5" },
    agent: { name: "explore-agent" },
    context_window: {
      context_window_size: 200000,
      used_percentage: 42.5,
      current_usage: { input_tokens: 1000, output_tokens: 200, cache_creation_input_tokens: 50, cache_read_input_tokens: 300 }
    },
    cost: { total_cost_usd: 1.2345 }
  };

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "statusline-basic", input), "--adapter", "anthropic-claude-statusline"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(GOLDEN.test1Identity, normIdentity(fsharpRecord));
  assert.deepEqual(GOLDEN.test1Capabilities, normCapabilities(fsharpRecord));
  assert.deepEqual(GOLDEN.test1Metrics, normMetrics(fsharpRecord));
  assert.deepEqual(GOLDEN.test1Events, normEvents(fsharpRecord));

  const fsharpStored = readExecution(fsharpRoot, "EXE-1");
  assert.deepEqual(GOLDEN.test1StoredCapabilities, normCapabilities(fsharpStored));
  assert.deepEqual(GOLDEN.test1StoredMetrics, normMetrics(fsharpStored));
});

test("F# telemetry ingest --adapter anthropic-claude-statusline declares every capability as supported-unavailable and records no metrics when nothing is present, matching production", (t) => {
  const fsharpRoot = fixture(t, "empty-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { version: "0.9.0" };

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "statusline-empty", input), "--adapter", "anthropic-claude-statusline"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(GOLDEN.test2Capabilities, normCapabilities(fsharpRecord));
  assert.equal(fsharpRecord.metrics.length, 0);
});

test("F# telemetry ingest --adapter anthropic-claude-statusline marks a present cost.session_cumulative as estimated rather than supported-observed, matching production", (t) => {
  const fsharpRoot = fixture(t, "cost-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { cost: { total_cost_usd: 0.42 } };

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "statusline-cost", input), "--adapter", "anthropic-claude-statusline"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  const fsharpCost = fsharpRecord.capabilities.find((c) => c.metricId === "cost.session_cumulative");
  assert.equal(GOLDEN.test3CostStatus, "estimated");
  assert.equal(fsharpCost.status, "estimated");
});
