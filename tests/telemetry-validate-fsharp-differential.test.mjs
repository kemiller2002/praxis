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
// implementation (tools/ros_telemetry.mjs's telemetryFindings), replaying
// the exact same setup sequence as each test against production's own
// `tools/ros_cli.mjs` (this repo's copy, since the disposable per-test
// fixture no longer bundles one -- production's greenfield manifest no
// longer includes these modules), then frozen here. Node is retained in
// this repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this
// test suite.
const GOLDEN = {
  test4Findings: [
    { path: ".ros/telemetry/executions/EXE-BROKEN-0001.json", field: "capabilities[0].lastAssessedAt", message: "capability assessment timestamp must be valid" },
    { path: ".ros/telemetry/executions/EXE-BROKEN-0001.json", field: "classification.types", message: "work classifications must be unique" },
    { path: ".ros/telemetry/executions/EXE-BROKEN-0001.json", field: "executionId", message: "execution filename must match executionId" },
    { path: ".ros/telemetry/executions/EXE-BROKEN-0001.json", field: "identity.model", message: "identity value must be a string or null" },
    { path: ".ros/telemetry/executions/EXE-BROKEN-0001.json", field: "metrics[0].quality", message: "ROS-derived metric cannot be represented as observed or estimated" },
    { path: ".ros/telemetry/executions/EXE-BROKEN-0001.json", field: "metrics[0].value", message: "metric value must be a finite non-negative number" },
    { path: ".ros/telemetry/executions/EXE-BROKEN-0001.json", field: "status", message: "invalid execution status 'weird'" },
    { path: ".ros/telemetry/executions/EXE-BROKEN-0001.json", field: "workItemId", message: "work-item linkage is required" }
  ],
  test5Findings: [
    { path: ".ros/telemetry/executions/EXE-RAW-0001.json", field: "capabilities[0].history[0]", message: "capability state recording order must be chronological" },
    { path: ".ros/telemetry/executions/EXE-RAW-0001.json", field: "qualitySignals[0].detector", message: "invalid quality-signal detector 'not-a-real-detector'" },
    { path: ".ros/telemetry/executions/EXE-RAW-0001.json", field: "rawTelemetry[0].payload", message: "sensitive raw field is not redacted: $.password" },
    { path: ".ros/telemetry/executions/EXE-RAW-0001.json", field: "workItemId", message: "execution 'EXE-RAW-0001' is not linked back from work item 'WI-ONE'" }
  ],
  test6Findings: [
    { path: ".ros/telemetry/executions/EXE-UNFIN-0001.json", field: "status", message: "completed work item 'WI-ONE' has unfinalized telemetry" }
  ]
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-validate-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
  initializeProject({ target: root, project: "Telemetry Validate Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  execFileSync("git", ["-C", root, "-c", "user.email=a@b.c", "-c", "user.name=a", "add", "-A"]);
  execFileSync("git", ["-C", root, "-c", "user.email=a@b.c", "-c", "user.name=a", "commit", "-q", "-m", "init"]);
  return root;
}

const at = () => new Date().toISOString();

function fsharpWork(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", ...args], { cwd: repositoryRoot, encoding: "utf8" });
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

  assert.equal(fsharpWork(root, ["capture", "--id", "WI-ONE", "--title", "Task one", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["backlog-transition", "--id", "WI-ONE", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["start", "--id", "WI-ONE", "--occurred-at", at()]).status, 0);

  const fsharpResult = fsharpValidate(root);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpFindings = sortFindings(JSON.parse(fsharpResult.stdout).findings.map(stripSeverity));

  assert.deepEqual(fsharpFindings, []);
});

test("F# telemetry validate matches production for a fully completed, evidence-satisfied, finalized execution", (t) => {
  const root = fixture(t, "complete");

  assert.equal(fsharpWork(root, ["capture", "--id", "WI-ONE", "--title", "Task one", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["backlog-transition", "--id", "WI-ONE", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["start", "--id", "WI-ONE", "--occurred-at", at()]).status, 0);
  fs.writeFileSync(path.join(root, "note.txt"), "hello\n");
  execFileSync("git", ["-C", root, "add", "-A"]);
  const complete = fsharpWork(root, ["complete", "--id", "WI-ONE", "--occurred-at", at(), "--evidence", "implementation=note.txt", "--evidence", "tests=note.txt"]);
  assert.equal(complete.status, 0, complete.stderr);

  const fsharpResult = fsharpValidate(root);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpFindings = sortFindings(JSON.parse(fsharpResult.stdout).findings.map(stripSeverity));

  assert.deepEqual(fsharpFindings, []);
});

test("F# telemetry validate matches production's disabled-with-no-reason and malformed-registry early exits", (t) => {
  const root = fixture(t, "config");

  const config = JSON.parse(fs.readFileSync(path.join(root, "ros.json"), "utf8"));
  config.telemetry = { enabled: false };
  fs.writeFileSync(path.join(root, "ros.json"), JSON.stringify(config, null, 2));

  let fsharpFindings = JSON.parse(fsharpValidate(root).stdout).findings.map(stripSeverity);
  assert.deepEqual(fsharpFindings, [{ path: "ros.json", field: "telemetry.disabledReason", message: "disabled telemetry requires an explicit reason" }]);

  config.telemetry = { enabled: true };
  fs.writeFileSync(path.join(root, "ros.json"), JSON.stringify(config, null, 2));
  fs.writeFileSync(path.join(root, "telemetry", "metrics.json"), JSON.stringify({ schemaVersion: "1.0.0", metrics: [{ id: "a" }, { id: "a" }] }));

  fsharpFindings = JSON.parse(fsharpValidate(root).stdout).findings.map(stripSeverity);
  assert.deepEqual(fsharpFindings, [{ path: "telemetry/metrics.json", field: "schemaVersion", message: "telemetry metric registry has an invalid definition for 'a'" }]);
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

  const fsharpResult = fsharpValidate(root);
  const fsharpFindings = sortFindings(JSON.parse(fsharpResult.stdout).findings.map(stripSeverity));

  assert.ok(GOLDEN.test4Findings.length > 0);
  assert.deepEqual(fsharpFindings, GOLDEN.test4Findings);
});

test("F# telemetry validate matches production for capability history ordering, quality signals, raw redaction, and cross-record linkage", (t) => {
  const root = fixture(t, "raw");

  assert.equal(fsharpWork(root, ["capture", "--id", "WI-ONE", "--title", "Task one", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["backlog-transition", "--id", "WI-ONE", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["start", "--id", "WI-ONE", "--occurred-at", at()]).status, 0);

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

  const fsharpResult = fsharpValidate(root);
  const fsharpFindings = sortFindings(JSON.parse(fsharpResult.stdout).findings.map(stripSeverity).filter((f) => f.path.includes("EXE-RAW")));

  assert.ok(GOLDEN.test5Findings.length >= 4, "expected at least the history/detector/redaction/linkage findings");
  assert.deepEqual(fsharpFindings, GOLDEN.test5Findings);
});

test("F# telemetry validate matches production for a completed work item with unfinalized linked telemetry", (t) => {
  const root = fixture(t, "unfinalized");

  assert.equal(fsharpWork(root, ["capture", "--id", "WI-ONE", "--title", "Task one", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["backlog-transition", "--id", "WI-ONE", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["start", "--id", "WI-ONE", "--occurred-at", at()]).status, 0);
  fs.writeFileSync(path.join(root, "note.txt"), "hello\n");
  execFileSync("git", ["-C", root, "add", "-A"]);
  assert.equal(fsharpWork(root, ["complete", "--id", "WI-ONE", "--occurred-at", at(), "--evidence", "implementation=note.txt", "--evidence", "tests=note.txt"]).status, 0);

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

  const fsharpResult = fsharpValidate(root);
  const fsharpFindings = sortFindings(JSON.parse(fsharpResult.stdout).findings.map(stripSeverity));

  assert.deepEqual(
    GOLDEN.test6Findings.filter((f) => f.field === "status"),
    [{ path: ".ros/telemetry/executions/EXE-UNFIN-0001.json", field: "status", message: "completed work item 'WI-ONE' has unfinalized telemetry" }]
  );
  assert.deepEqual(fsharpFindings, GOLDEN.test6Findings);
});
