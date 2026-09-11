import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { telemetryFindings } from "../tools/ros_telemetry.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-validate-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Validate Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  execFileSync("git", ["-C", root, "-c", "user.email=a@b.c", "-c", "user.name=a", "add", "-A"]);
  execFileSync("git", ["-C", root, "-c", "user.email=a@b.c", "-c", "user.name=a", "commit", "-q", "-m", "init"]);
  return root;
}

function ros(root, args) {
  const result = spawnSync("node", [path.join(root, "tools", "ros_cli.mjs"), ...args], { cwd: root, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function fsharpValidate(root) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", "validate", "--json"], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function stripSeverity(finding) {
  const { severity, ...rest } = finding;
  return rest;
}

function sortFindings(findings) {
  return [...findings].sort((a, b) => (a.path + a.field + a.message).localeCompare(b.path + b.field + b.message));
}

test("F# telemetry validate matches production's telemetryFindings for a clean, real work-start execution", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const root = fixture(t, "clean");

  assert.equal(ros(root, ["add", "Task one", "--id", "WI-ONE"]).status, 0);
  assert.equal(ros(root, ["work", "ready", "WI-ONE"]).status, 0);
  assert.equal(ros(root, ["work", "start", "WI-ONE"]).status, 0);

  const nodeFindings = sortFindings(telemetryFindings(root));
  const fsharpResult = fsharpValidate(root);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpFindings = sortFindings(JSON.parse(fsharpResult.stdout).findings.map(stripSeverity));

  assert.deepEqual(nodeFindings, []);
  assert.deepEqual(fsharpFindings, []);
});

test("F# telemetry validate matches production for a fully completed, evidence-satisfied, finalized execution", (t) => {
  const root = fixture(t, "complete");

  assert.equal(ros(root, ["add", "Task one", "--id", "WI-ONE"]).status, 0);
  assert.equal(ros(root, ["work", "ready", "WI-ONE"]).status, 0);
  assert.equal(ros(root, ["work", "start", "WI-ONE"]).status, 0);
  fs.writeFileSync(path.join(root, "note.txt"), "hello\n");
  execFileSync("git", ["-C", root, "add", "-A"]);
  const complete = ros(root, ["work", "complete", "WI-ONE", "--evidence", "implementation=note.txt", "--evidence", "tests=note.txt"]);
  assert.equal(complete.status, 0, complete.stderr);

  const nodeFindings = sortFindings(telemetryFindings(root));
  const fsharpResult = fsharpValidate(root);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpFindings = sortFindings(JSON.parse(fsharpResult.stdout).findings.map(stripSeverity));

  assert.deepEqual(nodeFindings, []);
  assert.deepEqual(fsharpFindings, []);
});

test("F# telemetry validate matches production's disabled-with-no-reason and malformed-registry early exits", (t) => {
  const root = fixture(t, "config");

  const config = JSON.parse(fs.readFileSync(path.join(root, "ros.json"), "utf8"));
  config.telemetry = { enabled: false };
  fs.writeFileSync(path.join(root, "ros.json"), JSON.stringify(config, null, 2));

  let nodeFindings = telemetryFindings(root);
  let fsharpFindings = JSON.parse(fsharpValidate(root).stdout).findings.map(stripSeverity);
  assert.deepEqual(nodeFindings, [{ path: "ros.json", field: "telemetry.disabledReason", message: "disabled telemetry requires an explicit reason" }]);
  assert.deepEqual(fsharpFindings, nodeFindings);

  config.telemetry = { enabled: true };
  fs.writeFileSync(path.join(root, "ros.json"), JSON.stringify(config, null, 2));
  fs.writeFileSync(path.join(root, "telemetry", "metrics.json"), JSON.stringify({ schemaVersion: "1.0.0", metrics: [{ id: "a" }, { id: "a" }] }));

  nodeFindings = telemetryFindings(root);
  fsharpFindings = JSON.parse(fsharpValidate(root).stdout).findings.map(stripSeverity);
  assert.deepEqual(nodeFindings, [{ path: "telemetry/metrics.json", field: "schemaVersion", message: "telemetry metric registry has an invalid definition for 'a'" }]);
  assert.deepEqual(fsharpFindings, nodeFindings);
});

test("F# telemetry validate matches production for a deliberately broken hand-written execution record", (t) => {
  const root = fixture(t, "broken");

  const executionsDir = path.join(root, ".ros", "telemetry", "executions");
  fs.mkdirSync(executionsDir, { recursive: true });
  fs.writeFileSync(
    path.join(executionsDir, "EXE-BROKEN-0001.json"),
    JSON.stringify({
      schemaVersion: "1.0.0",
      executionId: "EXE-WRONG-ID",
      workItemId: "",
      identity: { provider: "anthropic", runtime: "claude-code", model: 5 },
      provenance: { collector: "ros", collectorVersion: "1.0.0", discoveredAt: "2026-01-01T00:00:00.000Z" },
      startedAt: "2026-01-01T00:00:00.000Z",
      status: "weird",
      capabilities: [
        {
          status: "supported-observed",
          metricId: "time.wall_ms",
          source: { type: "ros-clock", name: "x", mechanism: "y" },
          discoveredAt: "2026-01-01T00:00:00.000Z",
          lastAssessedAt: "not-a-date"
        }
      ],
      metrics: [
        {
          id: "time.wall_ms",
          measurementId: "m1",
          value: -5,
          quality: "observed",
          source: { type: "ros-clock", name: "x", mechanism: "y" },
          collectedAt: "2026-01-01T00:00:00.000Z",
          schemaVersion: "1.0.0",
          scope: "execution",
          aggregation: "sum",
          unit: "milliseconds"
        }
      ],
      rawTelemetry: [],
      events: [],
      repository: {},
      links: {},
      classification: { types: ["development", "development"] }
    })
  );

  const nodeFindings = sortFindings(telemetryFindings(root));
  const fsharpResult = fsharpValidate(root);
  const fsharpFindings = sortFindings(JSON.parse(fsharpResult.stdout).findings.map(stripSeverity));

  assert.ok(nodeFindings.length > 0);
  assert.deepEqual(fsharpFindings, nodeFindings);
});

test("F# telemetry validate matches production for capability history ordering, quality signals, raw redaction, and cross-record linkage", (t) => {
  const root = fixture(t, "raw");

  assert.equal(ros(root, ["add", "Task one", "--id", "WI-ONE"]).status, 0);
  assert.equal(ros(root, ["work", "ready", "WI-ONE"]).status, 0);
  assert.equal(ros(root, ["work", "start", "WI-ONE"]).status, 0);

  const executionsDir = path.join(root, ".ros", "telemetry", "executions");
  fs.writeFileSync(
    path.join(executionsDir, "EXE-RAW-0001.json"),
    JSON.stringify({
      schemaVersion: "1.0.0",
      executionId: "EXE-RAW-0001",
      workItemId: "WI-ONE",
      identity: { provider: "anthropic", runtime: "claude-code" },
      provenance: { collector: "ros", collectorVersion: "1.0.0", discoveredAt: "2026-01-01T00:00:00.000Z" },
      startedAt: "2026-01-01T00:00:00.000Z",
      status: "active",
      capabilities: [
        {
          status: "supported-observed",
          metricId: "time.wall_ms",
          source: { type: "ros-clock", name: "x", mechanism: "y" },
          discoveredAt: "2026-01-01T00:00:00.000Z",
          history: [
            { status: "unknown", source: { type: "ros-clock", name: "x", mechanism: "y" }, discoveredAt: "2026-01-01T00:00:00.000Z", recordedAt: "2026-01-03T00:00:00.000Z" },
            { status: "unknown", source: { type: "ros-clock", name: "x", mechanism: "y" }, discoveredAt: "2026-01-01T00:00:00.000Z", recordedAt: "2026-01-02T00:00:00.000Z" }
          ]
        }
      ],
      metrics: [],
      qualitySignals: [{ detector: "not-a-real-detector", source: { type: "human-report", name: "x", mechanism: "y" } }],
      rawTelemetry: [
        { snapshotId: "SNAP-1", adapter: "generic", source: { type: "runtime-output", name: "x", mechanism: "y" }, collectedAt: "2026-01-01T00:00:00.000Z", payload: { password: "hunter2", safe_field: "ok" } }
      ],
      events: [],
      repository: {},
      links: {},
      classification: { types: ["development"] }
    })
  );

  const nodeFindings = sortFindings(telemetryFindings(root).filter((f) => f.path.includes("EXE-RAW")));
  const fsharpResult = fsharpValidate(root);
  const fsharpFindings = sortFindings(JSON.parse(fsharpResult.stdout).findings.map(stripSeverity).filter((f) => f.path.includes("EXE-RAW")));

  assert.ok(nodeFindings.length >= 4, "expected at least the history/detector/redaction/linkage findings");
  assert.deepEqual(fsharpFindings, nodeFindings);
});

test("F# telemetry validate matches production for a completed work item with unfinalized linked telemetry", (t) => {
  const root = fixture(t, "unfinalized");

  assert.equal(ros(root, ["add", "Task one", "--id", "WI-ONE"]).status, 0);
  assert.equal(ros(root, ["work", "ready", "WI-ONE"]).status, 0);
  assert.equal(ros(root, ["work", "start", "WI-ONE"]).status, 0);
  fs.writeFileSync(path.join(root, "note.txt"), "hello\n");
  execFileSync("git", ["-C", root, "add", "-A"]);
  assert.equal(ros(root, ["work", "complete", "WI-ONE", "--evidence", "implementation=note.txt", "--evidence", "tests=note.txt"]).status, 0);

  const executionsDir = path.join(root, ".ros", "telemetry", "executions");
  fs.writeFileSync(
    path.join(executionsDir, "EXE-UNFIN-0001.json"),
    JSON.stringify({
      schemaVersion: "1.0.0",
      executionId: "EXE-UNFIN-0001",
      workItemId: "WI-ONE",
      identity: { provider: "anthropic", runtime: "claude-code" },
      provenance: { collector: "ros", collectorVersion: "1.0.0", discoveredAt: "2026-01-01T00:00:00.000Z" },
      startedAt: "2026-01-01T00:00:00.000Z",
      status: "active",
      capabilities: [],
      metrics: [],
      rawTelemetry: [],
      events: [],
      repository: {},
      links: {},
      classification: { types: ["development"] }
    })
  );

  const contextPath = path.join(root, ".ros", "context", "current.json");
  const context = JSON.parse(fs.readFileSync(contextPath, "utf8"));
  for (const item of context.workItems) {
    if (item.id === "WI-ONE") (item.telemetryExecutionIds ??= []).push("EXE-UNFIN-0001");
  }
  fs.writeFileSync(contextPath, JSON.stringify(context, null, 2));

  const nodeFindings = sortFindings(telemetryFindings(root));
  const fsharpResult = fsharpValidate(root);
  const fsharpFindings = sortFindings(JSON.parse(fsharpResult.stdout).findings.map(stripSeverity));

  assert.deepEqual(
    nodeFindings.filter((f) => f.field === "status"),
    [{ path: ".ros/telemetry/executions/EXE-UNFIN-0001.json", field: "status", message: "completed work item 'WI-ONE' has unfinalized telemetry" }]
  );
  assert.deepEqual(fsharpFindings, nodeFindings);
});
