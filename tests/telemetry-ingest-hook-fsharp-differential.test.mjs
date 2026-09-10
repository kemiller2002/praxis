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
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-ingest-hook-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Ingest Hook Differential" });
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
  return { provider: record.identity.provider, runtime: record.identity.runtime, model: record.identity.model ?? null, sessionId: record.identity.sessionId ?? null, agentId: record.identity.agentId ?? null };
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

const cases = [
  {
    adapter: "anthropic-claude-hook",
    label: "posttooluse",
    input: {
      hook_event_name: "PostToolUse",
      tool_name: "Bash",
      timestamp: "2026-01-01T00:00:05.000Z",
      session_id: "sess-123",
      model: { id: "claude-opus-5" },
      tool_response: { error: "boom" }
    }
  },
  {
    adapter: "google-gemini-hook",
    label: "subagentstart",
    input: {
      hook_event_name: "SubagentStart",
      timestamp: "2026-01-01T00:00:05.000Z",
      agent_type: "explore-agent"
    }
  },
  {
    adapter: "github-copilot-hook",
    label: "permissiondenied",
    input: {
      hook_event_name: "PermissionDenied",
      timestamp: "2026-01-01T00:00:05.000Z",
      model: "gpt-5-copilot"
    }
  }
];

for (const { adapter, label, input } of cases) {
  test(`F# telemetry ingest --adapter ${adapter} maps identity/capabilities/metrics/events identically to production's own adaptHook (${label})`, (t) => {
    assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
    const nodeRoot = fixture(t, `${label}-node`);
    const fsharpRoot = fixture(t, `${label}-fsharp`);

    writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
    writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

    const nodeRecord = ingestTelemetry(nodeRoot, "EXE-1", input, { adapter });

    const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, `hook-${label}`, input), "--adapter", adapter]);
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
}

test("F# telemetry ingest --adapter anthropic-claude-hook does not record tool.failures when nothing indicates an error, matching production", (t) => {
  const nodeRoot = fixture(t, "no-error-node");
  const fsharpRoot = fixture(t, "no-error-fsharp");

  writeFixtureExecution(nodeRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { hook_event_name: "PostToolUse", tool_name: "Bash", timestamp: "2026-01-01T00:00:05.000Z", tool_response: { result: "ok" } };

  const nodeRecord = ingestTelemetry(nodeRoot, "EXE-1", input, { adapter: "anthropic-claude-hook" });
  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "hook-no-error", input), "--adapter", "anthropic-claude-hook"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeRecord.metrics.filter((m) => m.id === "tool.failures").length, 0);
  assert.equal(fsharpRecord.metrics.filter((m) => m.id === "tool.failures").length, 0);
});
