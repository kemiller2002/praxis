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
// implementation (tools/ros_telemetry.mjs's ingestTelemetry with the
// anthropic-claude-otel/otel-json/google-gemini-otel/github-copilot-otel
// adapters) with the exact same call sequence as each test, then frozen
// here. Node is retained in this repository only as the web server's
// internal dependency (DF-ROS-2026-A033) and is no longer executed as a live
// oracle by this test suite.
const GOLDEN = {
  test1Identity: { provider: "anthropic", runtime: "claude-code", model: "claude-opus-5", sessionId: "sess-1" },
  test1Capabilities: [
    { metricId: "agent.turns", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.compactions", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "model.request_failures", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "model.requests", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "runtime.memory_peak_bytes", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "time.model_ms", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "tokens.input", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "tokens.output", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "tool.calls", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "tool.failures", status: "supported-observed", reason: "normalized measurement recorded" }
  ],
  test1Metrics: [
    { id: "agent.turns", value: 3, scope: "execution", dimensions: "{}" },
    { id: "context.compactions", value: 1, scope: "execution", dimensions: "{}" },
    { id: "model.request_failures", value: 1, scope: "operation", dimensions: "{}" },
    { id: "model.requests", value: 1, scope: "operation", dimensions: "{}" },
    { id: "runtime.memory_peak_bytes", value: 512000, scope: "execution", dimensions: "{}" },
    { id: "time.model_ms", value: 450, scope: "operation", dimensions: "{\"event\":\"claude_code.api_request\"}" },
    { id: "tokens.input", value: 50, scope: "operation", dimensions: "{\"event\":\"gen_ai.something\"}" },
    { id: "tokens.input", value: 123, scope: "operation", dimensions: "{\"tokenType\":\"input\"}" },
    { id: "tokens.output", value: 20, scope: "operation", dimensions: "{\"event\":\"gen_ai.something\"}" },
    { id: "tool.calls", value: 1, scope: "tool", dimensions: "{\"toolType\":\"Bash\"}" },
    { id: "tool.failures", value: 1, scope: "tool", dimensions: "{\"toolType\":\"Bash\"}" }
  ],
  test1Events: ["telemetry.snapshot.ingested"],
  test1StoredCapabilities: [
    { metricId: "agent.turns", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "context.compactions", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "model.request_failures", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "model.requests", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "runtime.memory_peak_bytes", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "time.model_ms", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "tokens.input", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "tokens.output", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "tool.calls", status: "supported-observed", reason: "normalized measurement recorded" },
    { metricId: "tool.failures", status: "supported-observed", reason: "normalized measurement recorded" }
  ],
  test1StoredMetrics: [
    { id: "agent.turns", value: 3, scope: "execution", dimensions: "{}" },
    { id: "context.compactions", value: 1, scope: "execution", dimensions: "{}" },
    { id: "model.request_failures", value: 1, scope: "operation", dimensions: "{}" },
    { id: "model.requests", value: 1, scope: "operation", dimensions: "{}" },
    { id: "runtime.memory_peak_bytes", value: 512000, scope: "execution", dimensions: "{}" },
    { id: "time.model_ms", value: 450, scope: "operation", dimensions: "{\"event\":\"claude_code.api_request\"}" },
    { id: "tokens.input", value: 50, scope: "operation", dimensions: "{\"event\":\"gen_ai.something\"}" },
    { id: "tokens.input", value: 123, scope: "operation", dimensions: "{\"tokenType\":\"input\"}" },
    { id: "tokens.output", value: 20, scope: "operation", dimensions: "{\"event\":\"gen_ai.something\"}" },
    { id: "tool.calls", value: 1, scope: "tool", dimensions: "{\"toolType\":\"Bash\"}" },
    { id: "tool.failures", value: 1, scope: "tool", dimensions: "{\"toolType\":\"Bash\"}" }
  ],
  test2Identity: { provider: "openai", runtime: "unknown" },
  test2Metrics: [
    { id: "model.requests", value: 1, scope: "operation", dimensions: "{}" }
  ],
  test3: {
    "google-gemini-otel": { identity: { provider: "google", runtime: "gemini-cli" }, provider: "google", runtime: "gemini-cli" },
    "github-copilot-otel": { identity: { provider: "github", runtime: "copilot" }, provider: "github", runtime: "copilot" }
  }
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-ingest-otel-${label}-`));
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
  initializeProject({ target: root, project: "Telemetry Ingest Otel Differential" });
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
    .map((m) => ({ id: m.id, value: m.value, scope: m.scope ?? null, dimensions: JSON.stringify(m.dimensions ?? null) }))
    .sort((a, b) => a.id.localeCompare(b.id) || a.value - b.value);
}

function normEvents(record) {
  return record.events.map((e) => e.type).sort();
}

const richInput = [
  {
    name: "gen_ai.client.token.usage",
    timestamp: "2026-01-01T00:00:05.000Z",
    resource: { attributes: { "gen_ai.provider.name": "anthropic" } },
    "gen_ai.response.model": "claude-opus-5",
    "session.id": "sess-1",
    type: "input",
    value: 123
  },
  { name: "claude_code.api_request", timestamp: "2026-01-01T00:00:06.000Z", success: false, duration_ms: 450 },
  { name: "claude_code.tool_result", timestamp: "2026-01-01T00:00:07.000Z", tool_name: "Bash", success: "false" },
  { name: "gemini_cli.agent.turns", timestamp: "2026-01-01T00:00:08.000Z", value: 3 },
  { name: "gemini_cli.chat_compression", timestamp: "2026-01-01T00:00:09.000Z" },
  { name: "gemini_cli.memory.usage", timestamp: "2026-01-01T00:00:10.000Z", memory_type: "rss", value: 512000 },
  { name: "gen_ai.something", timeUnixNano: 1767225611000000000, input_tokens: 50, output_tokens: 20 }
];

test("F# telemetry ingest --adapter anthropic-claude-otel maps identity/capabilities/metrics/events identically to production's own adaptOtel across nested lookups, direct fields, token-usage types, api/tool events, and a numeric nanosecond timestamp", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharpRoot = fixture(t, "rich-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "otel-rich", richInput), "--adapter", "anthropic-claude-otel"]);
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

test("F# telemetry ingest --adapter otel-json seeds identity from the bare adapter's own unknown/unknown default and unwraps a records-carrying object, matching production", (t) => {
  const fsharpRoot = fixture(t, "wrapped-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { records: [{ name: "api_request", timestamp: "2026-01-01T00:00:05.000Z", provider: "openai", success: true }] };

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "otel-wrapped", input), "--adapter", "otel-json"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(GOLDEN.test2Identity, normIdentity(fsharpRecord));
  assert.deepEqual(GOLDEN.test2Metrics, normMetrics(fsharpRecord));
});

for (const [adapter, provider, runtime] of [
  ["google-gemini-otel", "google", "gemini-cli"],
  ["github-copilot-otel", "github", "copilot"]
]) {
  test(`F# telemetry ingest --adapter ${adapter} seeds identity from its own provider/runtime default when nothing is present, matching production`, (t) => {
    const fsharpRoot = fixture(t, `${adapter}-fsharp`);

    writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

    const input = {};

    const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, `${adapter}-empty`, input), "--adapter", adapter]);
    assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
    const fsharpRecord = JSON.parse(fsharpResult.stdout);

    const golden = GOLDEN.test3[adapter];
    assert.equal(golden.provider, provider);
    assert.equal(golden.runtime, runtime);
    assert.deepEqual(golden.identity, normIdentity(fsharpRecord));
    assert.equal(fsharpRecord.metrics.length, 0);
  });
}
