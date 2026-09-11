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
// anthropic-claude-hook/google-gemini-hook/github-copilot-hook adapters)
// with the exact same call sequence as each test, then frozen here. Node is
// retained in this repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this test
// suite.
const GOLDEN = {
  posttooluse: {
    identity: { provider: "anthropic", runtime: "claude-code", model: "claude-opus-5", sessionId: "sess-123", agentId: null },
    capabilities: [
      { metricId: undefined, status: "unknown", reason: "provider field preserved but not normalized by this adapter version" },
      { metricId: "telemetry.redactions", status: "derived", reason: "normalized measurement recorded" },
      { metricId: "telemetry.unknown_fields", status: "derived", reason: "normalized measurement recorded" },
      { metricId: "tool.calls", status: "supported-observed", reason: "normalized measurement recorded" },
      { metricId: "tool.failures", status: "supported-observed", reason: "normalized measurement recorded" },
      { metricId: "tool.shell_commands", status: "supported-observed", reason: "normalized measurement recorded" }
    ],
    metrics: [
      { id: "telemetry.redactions", value: 1, scope: "execution", dimensions: "{}" },
      { id: "telemetry.unknown_fields", value: 1, scope: "execution", dimensions: "{}" },
      { id: "tool.calls", value: 1, scope: "tool", dimensions: "{\"toolType\":\"Bash\"}" },
      { id: "tool.failures", value: 1, scope: "tool", dimensions: "{\"toolType\":\"Bash\"}" },
      { id: "tool.shell_commands", value: 1, scope: "tool", dimensions: "{\"toolType\":\"Bash\"}" }
    ],
    events: ["runtime.posttooluse", "telemetry.snapshot.ingested"],
    storedCapabilities: [
      { metricId: undefined, status: "unknown", reason: "provider field preserved but not normalized by this adapter version" },
      { metricId: "telemetry.redactions", status: "derived", reason: "normalized measurement recorded" },
      { metricId: "telemetry.unknown_fields", status: "derived", reason: "normalized measurement recorded" },
      { metricId: "tool.calls", status: "supported-observed", reason: "normalized measurement recorded" },
      { metricId: "tool.failures", status: "supported-observed", reason: "normalized measurement recorded" },
      { metricId: "tool.shell_commands", status: "supported-observed", reason: "normalized measurement recorded" }
    ],
    storedMetrics: [
      { id: "telemetry.redactions", value: 1, scope: "execution", dimensions: "{}" },
      { id: "telemetry.unknown_fields", value: 1, scope: "execution", dimensions: "{}" },
      { id: "tool.calls", value: 1, scope: "tool", dimensions: "{\"toolType\":\"Bash\"}" },
      { id: "tool.failures", value: 1, scope: "tool", dimensions: "{\"toolType\":\"Bash\"}" },
      { id: "tool.shell_commands", value: 1, scope: "tool", dimensions: "{\"toolType\":\"Bash\"}" }
    ]
  },
  subagentstart: {
    identity: { provider: "google", runtime: "gemini-cli", model: null, sessionId: null, agentId: "explore-agent" },
    capabilities: [
      { metricId: "agent.subagents_spawned", status: "supported-observed", reason: "normalized measurement recorded" }
    ],
    metrics: [
      { id: "agent.subagents_spawned", value: 1, scope: "execution", dimensions: "{}" }
    ],
    events: ["runtime.subagentstart", "telemetry.snapshot.ingested"],
    storedCapabilities: [
      { metricId: "agent.subagents_spawned", status: "supported-observed", reason: "normalized measurement recorded" }
    ],
    storedMetrics: [
      { id: "agent.subagents_spawned", value: 1, scope: "execution", dimensions: "{}" }
    ]
  },
  permissiondenied: {
    identity: { provider: "github", runtime: "copilot", model: "gpt-5-copilot", sessionId: null, agentId: null },
    capabilities: [
      { metricId: "agent.approvals_denied", status: "supported-observed", reason: "normalized measurement recorded" }
    ],
    metrics: [
      { id: "agent.approvals_denied", value: 1, scope: "execution", dimensions: "{}" }
    ],
    events: ["runtime.permissiondenied", "telemetry.snapshot.ingested"],
    storedCapabilities: [
      { metricId: "agent.approvals_denied", status: "supported-observed", reason: "normalized measurement recorded" }
    ],
    storedMetrics: [
      { id: "agent.approvals_denied", value: 1, scope: "execution", dimensions: "{}" }
    ]
  }
};

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
    const fsharpRoot = fixture(t, `${label}-fsharp`);

    writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

    const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, `hook-${label}`, input), "--adapter", adapter]);
    assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
    const fsharpRecord = JSON.parse(fsharpResult.stdout);

    const golden = GOLDEN[label];
    assert.deepEqual(golden.identity, normIdentity(fsharpRecord));
    assert.deepEqual(golden.capabilities, normCapabilities(fsharpRecord));
    assert.deepEqual(golden.metrics, normMetrics(fsharpRecord));
    assert.deepEqual(golden.events, normEvents(fsharpRecord));

    const fsharpStored = readExecution(fsharpRoot, "EXE-1");
    assert.deepEqual(golden.storedCapabilities, normCapabilities(fsharpStored));
    assert.deepEqual(golden.storedMetrics, normMetrics(fsharpStored));
  });
}

test("F# telemetry ingest --adapter anthropic-claude-hook does not record tool.failures when nothing indicates an error, matching production", (t) => {
  const fsharpRoot = fixture(t, "no-error-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const input = { hook_event_name: "PostToolUse", tool_name: "Bash", timestamp: "2026-01-01T00:00:05.000Z", tool_response: { result: "ok" } };

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--input", writeInputFile(t, "hook-no-error", input), "--adapter", "anthropic-claude-hook"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = JSON.parse(fsharpResult.stdout);

  assert.equal(fsharpRecord.metrics.filter((m) => m.id === "tool.failures").length, 0);
});
