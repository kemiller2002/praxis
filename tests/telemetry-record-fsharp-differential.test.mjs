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

// This test's golden capability data includes runtime-identity detection
// (Ros.Domain.Telemetry.Identity.discover), which whitelists whichever CI/
// agent environment the caller happens to run in (CLAUDE_CODE_SESSION_ID,
// GITHUB_ACTIONS, etc.) ahead of an explicit override. Clearing every
// whitelisted variable makes the captured execution's identity
// deterministic across environments (a contributor's own machine, this
// sandbox, or a real CI runner) instead of baking in whichever one
// captured the golden literal.
const DETERMINISTIC_ENV = { ...process.env };
for (const key of [
  "CLAUDE_CODE_SESSION_ID", "CODEX_SESSION_ID", "CODEX_THREAD_ID",
  "GEMINI_SESSION_ID", "COPILOT_SESSION_ID", "GITHUB_ACTIONS", "GITHUB_RUN_ID",
  "OLLAMA_HOST", "ROS_TELEMETRY_PROVIDER", "ROS_TELEMETRY_RUNTIME"
]) delete DETERMINISTIC_ENV[key];

// Golden masters below were captured once from production's own Node
// implementation (tools/ros_telemetry.mjs's recordTelemetryMetric, called
// with the exact defaults `tools/ros_cli.mjs`'s CLI dispatch itself applies
// before invoking it) with the exact same call sequence as each test, then
// frozen here. Node is retained in this repository only as the web server's
// internal dependency (DF-ROS-2026-A033) and is no longer executed as a live
// oracle by this test suite. measurementId values are deterministic content
// digests, so they are still expected to match F#'s real output
// byte-for-byte.
const GOLDEN = {
  test1Metric: {
    measurementId: "MEAS-1b020374973b8c09f0500d46",
    id: "tokens.input",
    value: 42,
    unit: "tokens",
    currency: null,
    quality: "observed",
    confidence: null,
    scope: "execution",
    aggregation: "sum",
    dimensions: {},
    pricing: null,
    source: { type: "agent-report", name: "ros-telemetry-cli", mechanism: "explicit-metric-record" },
    collectedAt: "2026-01-01T00:00:00.000Z",
    schemaVersion: "1.0.0"
  },
  test7MeasurementId: "MEAS-6cf5ce5166c3e0786d967b26",
  test8HistoryStatus: "unknown",
  test9CostMetric: {
    measurementId: "MEAS-041a30e3db0284e7b85a0f96",
    id: "cost.input",
    value: 0.05,
    unit: "usd-cents",
    currency: "USD",
    quality: "estimated",
    confidence: 0.9,
    scope: "execution",
    aggregation: "sum",
    dimensions: {},
    pricing: null,
    source: { type: "agent-report", name: "ros-telemetry-cli", mechanism: "explicit-metric-record" },
    collectedAt: "2026-01-01T00:00:00.000Z",
    schemaVersion: "1.0.0"
  },
  test9TokensMetric: {
    measurementId: "MEAS-01fb61fdd2d6ca8a756d8cb0",
    id: "tokens.output",
    value: 1,
    unit: "tokens",
    currency: null,
    quality: "observed",
    confidence: "high",
    scope: "execution",
    aggregation: "sum",
    dimensions: {},
    pricing: null,
    source: { type: "agent-report", name: "ros-telemetry-cli", mechanism: "explicit-metric-record" },
    collectedAt: "2026-01-01T00:00:00.000Z",
    schemaVersion: "1.0.0"
  }
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-record-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Record Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", "record", ...args], { cwd: repositoryRoot, encoding: "utf8", env: DETERMINISTIC_ENV });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function readExecution(root, executionId) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "telemetry", "executions", `${executionId}.json`), "utf8"));
}

function readExecutions(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8")));
}

function writeFixtureExecution(root, executionId, workItemId, status, startedAt, capabilities = []) {
  const directory = path.join(root, ".ros", "telemetry", "executions");
  fs.mkdirSync(directory, { recursive: true });
  const record = { schemaVersion: "1.0.0", executionId, workItemId, status, startedAt, events: [], metrics: [], capabilities };
  fs.writeFileSync(path.join(directory, `${executionId}.json`), JSON.stringify(record, null, 2));
}

test("F# telemetry record with no target resolves the single active work item's execution, matching production's real effect", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharpRoot = fixture(t, "no-target-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });

  const fsharpResult = runFsharp(fsharpRoot, ["--metric", "tokens.input", "--value", "42", "--collected-at", "2026-01-01T00:00:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpMetric = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(GOLDEN.test1Metric, fsharpMetric);
  assert.equal(fsharpMetric.measurementId.startsWith("MEAS-"), true);
});

test("F# telemetry record with a work-item target matches only the currently-active execution, ignoring a finalized sibling", (t) => {
  const fsharpRoot = fixture(t, "workitem-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-OLD", "WI-A", "finalized", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-NEW", "WI-A", "active", "2026-01-01T00:05:00.000Z");

  const fsharpResult = runFsharp(fsharpRoot, ["WI-A", "--metric", "tokens.input", "--value", "1", "--collected-at", "2026-01-01T00:00:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpNew = readExecution(fsharpRoot, "EXE-NEW");
  assert.equal(fsharpNew.executionId, "EXE-NEW");
  assert.equal(fsharpNew.metrics.length, 1);

  const fsharpOld = readExecution(fsharpRoot, "EXE-OLD");
  assert.equal(fsharpOld.metrics.length, 0);
});

test("F# telemetry record with an EXE-prefixed target resolves by exact execution id", (t) => {
  const fsharpRoot = fixture(t, "exe-target-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");
  writeFixtureExecution(fsharpRoot, "EXE-2", "WI-B", "active", "2026-01-01T00:00:01.000Z");

  const fsharpResult = runFsharp(fsharpRoot, ["EXE-1", "--metric", "tokens.input", "--value", "5", "--collected-at", "2026-01-01T00:00:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpTargeted = readExecution(fsharpRoot, "EXE-1");
  assert.equal(fsharpTargeted.metrics.length, 1);

  const fsharpOther = readExecution(fsharpRoot, "EXE-2");
  assert.equal(fsharpOther.metrics.length, 0);
});

test("F# telemetry record with no target and zero or multiple active-or-blocked work items rejects with production's exact ambiguity message", (t) => {
  const fsharpRootZero = fixture(t, "ambiguous-zero-fsharp");

  const fsharpResultZero = runFsharp(fsharpRootZero, ["--metric", "tokens.input", "--value", "1"]);

  assert.equal(fsharpResultZero.status, 1);
  assert.match(fsharpResultZero.stderr, /telemetry target is ambiguous; provide a work-item or execution ID/);

  const fsharpRootMany = fixture(t, "ambiguous-many-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRootMany, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRootMany, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });

  const fsharpResultMany = runFsharp(fsharpRootMany, ["--metric", "tokens.input", "--value", "1"]);

  assert.equal(fsharpResultMany.status, 1);
  assert.match(fsharpResultMany.stderr, /telemetry target is ambiguous; provide a work-item or execution ID/);
});

test("F# telemetry record rejects a target whose only matching execution is not active, matching production's exact 'was not found or is already finalized' message", (t) => {
  const fsharpRoot = fixture(t, "already-finalized-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "finalized", "2026-01-01T00:00:00.000Z");

  const fsharpResult = runFsharp(fsharpRoot, ["WI-A", "--metric", "tokens.input", "--value", "1"]);

  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /telemetry execution 'WI-A' was not found or is already finalized/);
});

test("F# telemetry record rejects an unregistered metric id and a non-finite value with production's exact messages", (t) => {
  const fsharpRoot = fixture(t, "invalid-input-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const fsharpUnknownResult = runFsharp(fsharpRoot, ["EXE-1", "--metric", "bogus.metric", "--value", "1"]);

  assert.equal(fsharpUnknownResult.status, 1);
  assert.match(fsharpUnknownResult.stderr, /unknown normalized metric 'bogus\.metric'; preserve it in raw telemetry until it is registered/);

  const fsharpValueResult = runFsharp(fsharpRoot, ["EXE-1", "--metric", "tokens.input", "--value", "not-a-number"]);

  assert.equal(fsharpValueResult.status, 1);
  assert.match(fsharpValueResult.stderr, /metric 'tokens\.input' requires a finite numeric value/);
});

test("F# telemetry record deduplicates an identical repeated call by its content-addressed measurementId", (t) => {
  const fsharpRoot = fixture(t, "dedup-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  runFsharp(fsharpRoot, ["EXE-1", "--metric", "tokens.input", "--value", "1", "--collected-at", "2026-01-01T00:00:00.000Z"]);
  runFsharp(fsharpRoot, ["EXE-1", "--metric", "tokens.input", "--value", "1", "--collected-at", "2026-01-01T00:00:00.000Z"]);

  const fsharpSecond = readExecution(fsharpRoot, "EXE-1");
  assert.equal(fsharpSecond.metrics.length, 1);
  assert.equal(GOLDEN.test7MeasurementId, fsharpSecond.metrics[0].measurementId);
});

test("F# telemetry record upserts a real registry-seeded capability from unobserved into supported-observed, matching production's real effect", (t) => {
  const fsharpRoot = fixture(t, "capability-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });

  const fsharpResult = runFsharp(fsharpRoot, ["WI-A", "--metric", "tokens.input", "--value", "10", "--collected-at", "2026-01-01T00:00:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpExecution = readExecutions(fsharpRoot)[0];
  const fsharpCapability = fsharpExecution.capabilities.find((c) => c.metricId === "tokens.input");

  assert.equal(fsharpCapability.status, "supported-observed");
  assert.equal(fsharpCapability.reason, "normalized measurement recorded");
  assert.equal(fsharpCapability.history.length, 1);
  assert.equal(GOLDEN.test8HistoryStatus, fsharpCapability.history[0].status);
});

test("F# telemetry record honors explicit --unit/--currency overrides and both numeric and text --confidence, matching production", (t) => {
  const fsharpRoot = fixture(t, "overrides-fsharp");

  writeFixtureExecution(fsharpRoot, "EXE-1", "WI-A", "active", "2026-01-01T00:00:00.000Z");

  const fsharpResult = runFsharp(fsharpRoot, [
    "EXE-1", "--metric", "cost.input", "--value", "0.05", "--unit", "usd-cents", "--currency", "USD",
    "--quality", "estimated", "--confidence", "0.9", "--collected-at", "2026-01-01T00:00:00.000Z"
  ]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpMetric = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(GOLDEN.test9CostMetric, fsharpMetric);

  const fsharpTextResult = runFsharp(fsharpRoot, [
    "EXE-1", "--metric", "tokens.output", "--value", "1", "--confidence", "high", "--collected-at", "2026-01-01T00:00:00.000Z"
  ]);
  assert.equal(fsharpTextResult.status, 0, fsharpTextResult.stderr);

  assert.deepEqual(GOLDEN.test9TokensMetric, JSON.parse(fsharpTextResult.stdout));
});
