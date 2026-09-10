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
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-ingest-otel-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
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
  const nodeRoot = fixture(t, "rich-node");
  const fsharpRoot = fixture(t, "rich-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const nodeRecord = ingestTelemetry(nodeRoot, "EXE-1", richInput, { adapter: "anthropic-claude-otel" });

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "otel-rich", richInput), "--adapter", "anthropic-claude-otel"]);
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

test("F# telemetry ingest --adapter otel-json seeds identity from the bare adapter's own unknown/unknown default and unwraps a records-carrying object, matching production", (t) => {
  const nodeRoot = fixture(t, "wrapped-node");
  const fsharpRoot = fixture(t, "wrapped-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { records: [{ name: "api_request", timestamp: "2026-01-01T00:00:05.000Z", provider: "openai", success: true }] };

  const nodeRecord = ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "otel-json" });
  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "otel-wrapped", input), "--adapter", "otel-json"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(normIdentity(nodeRecord), normIdentity(fsharpRecord));
  assert.deepEqual(normMetrics(nodeRecord), normMetrics(fsharpRecord));
});

for (const [adapter, provider, runtime] of [
  ["google-gemini-otel", "google", "gemini-cli"],
  ["github-copilot-otel", "github", "copilot"]
]) {
  test(`F# telemetry ingest --adapter ${adapter} seeds identity from its own provider/runtime default when nothing is present, matching production`, (t) => {
    const nodeRoot = fixture(t, `${adapter}-node`);
    const fsharpRoot = fixture(t, `${adapter}-fsharp`);

    writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
    writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

    const input = {};

    const nodeRecord = ingestTelemetry(nodeRoot, "EXE-1", input, { adapter });
    const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, `${adapter}-empty`, input), "--adapter", adapter]);
    assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
    const fsharpRecord = JSON.parse(fsharpResult.stdout);

    assert.equal(nodeRecord.identity.provider, provider);
    assert.equal(nodeRecord.identity.runtime, runtime);
    assert.deepEqual(normIdentity(nodeRecord), normIdentity(fsharpRecord));
    assert.equal(nodeRecord.metrics.length, 0);
    assert.equal(fsharpRecord.metrics.length, 0);
  });
}
