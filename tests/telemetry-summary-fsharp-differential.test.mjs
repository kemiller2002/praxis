import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { startWork, transition } from "../tools/ros_cli.mjs";
import { summarizeTelemetry } from "../tools/ros_telemetry.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-summary-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Telemetry Summary Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function metricValue(summary, id) {
  return summary.metrics.find((entry) => entry.id === id);
}

test("F# telemetry summary matches production's real aggregation over two real, real-timed executions", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const nodeRoot = fixture(t, "real-node");
  const fsharpRoot = fixture(t, "real-fsharp");

  startWork(nodeRoot, ["WI-A"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  startWork(nodeRoot, ["WI-B"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "task"]);

  for (const root of [nodeRoot, fsharpRoot]) {
    fs.writeFileSync(path.join(root, "IMPLEMENTATION-NOTES.md"), "Implemented.\n");
    fs.writeFileSync(path.join(root, "TESTS-NOTES.md"), "Tested.\n");
  }

  const evidence = [
    { type: "implementation", path: "IMPLEMENTATION-NOTES.md" },
    { type: "tests", path: "TESTS-NOTES.md" }
  ];

  transition(nodeRoot, "complete", ["WI-A", "WI-B"], { evidence });
  execFileSync("dotnet", [
    fsharpCli, "--root", fsharpRoot, "work", "complete", "--id", "WI-A", "--id", "WI-B", "--occurred-at", "2026-09-10T18:05:00.000Z",
    "--evidence", "implementation=IMPLEMENTATION-NOTES.md", "--evidence", "tests=TESTS-NOTES.md"
  ]);

  const nodeSummary = summarizeTelemetry(nodeRoot, undefined);
  const fsharpResult = runFsharp(fsharpRoot, ["summary"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpSummary = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeSummary.executionCount, 2);
  assert.equal(fsharpSummary.executionCount, 2);
  assert.deepEqual(nodeSummary.providers, fsharpSummary.providers);
  assert.deepEqual(nodeSummary.runtimes, fsharpSummary.runtimes);
  assert.equal(nodeSummary.timing.fullyFinalized, true);
  assert.equal(fsharpSummary.timing.fullyFinalized, true);

  // Cross-checked deterministic aggregates: identical real git diffs on both
  // sides, so the same "sum"/"maximum" values must match exactly, node vs. F#.
  for (const id of ["git.files_added", "git.files_modified", "documentation.files_changed", "git.baseline_dirty_files"]) {
    const nodeMetric = metricValue(nodeSummary, id);
    const fsharpMetric = metricValue(fsharpSummary, id);
    assert.ok(nodeMetric, `node summary missing metric ${id}`);
    assert.ok(fsharpMetric, `fsharp summary missing metric ${id}`);
    assert.equal(nodeMetric.value, fsharpMetric.value, `metric ${id} value mismatch`);
    assert.equal(nodeMetric.aggregation, fsharpMetric.aggregation);
    assert.equal(nodeMetric.measurements, fsharpMetric.measurements);
    assert.equal(nodeMetric.unit, fsharpMetric.unit);
  }

  // time.wall_ms is real-clock-derived (positive on both sides, not
  // cross-equal); its aggregation note is deterministic and compared.
  const nodeWallMs = metricValue(nodeSummary, "time.wall_ms");
  const fsharpWallMs = metricValue(fsharpSummary, "time.wall_ms");
  assert.ok(nodeWallMs.value > 0);
  assert.ok(fsharpWallMs.value > 0);
  assert.equal(nodeWallMs.note, "sum of execution spans; may exceed calendarSpanMs when executions overlap");
  assert.equal(fsharpWallMs.note, nodeWallMs.note);
});

test("F# telemetry summary WORKITEMID filters to a single execution's metrics only, matching production", (t) => {
  const nodeRoot = fixture(t, "filter-node");
  const fsharpRoot = fixture(t, "filter-fsharp");

  startWork(nodeRoot, ["WI-A"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);
  startWork(nodeRoot, ["WI-B"], { type: "task" });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "task"]);

  const nodeSummary = summarizeTelemetry(nodeRoot, "WI-A");
  const fsharpResult = runFsharp(fsharpRoot, ["summary", "WI-A"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpSummary = JSON.parse(fsharpResult.stdout);

  assert.equal(nodeSummary.workItemId, "WI-A");
  assert.equal(fsharpSummary.workItemId, "WI-A");
  assert.equal(nodeSummary.executionCount, 1);
  assert.equal(fsharpSummary.executionCount, 1);
});

test("F# telemetry summary with no executions at all reports an empty, zeroed summary, matching production", (t) => {
  const nodeRoot = fixture(t, "empty-node");
  const fsharpRoot = fixture(t, "empty-fsharp");

  const nodeSummary = summarizeTelemetry(nodeRoot, undefined);
  const fsharpResult = runFsharp(fsharpRoot, ["summary"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpSummary = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(nodeSummary, fsharpSummary);
  assert.equal(nodeSummary.executionCount, 0);
  assert.deepEqual(nodeSummary.metrics, []);
  assert.equal(nodeSummary.timing.fullyFinalized, false);
});

test("F# telemetry summary exercises latest-per-session and none aggregation over hand-written fixture executions, byte-identical to production", (t) => {
  const nodeRoot = fixture(t, "fixture-node");
  const fsharpRoot = fixture(t, "fixture-fsharp");

  // These aggregation strategies are never produced by any real effect this
  // migration's own writers exercise (only sum/maximum are), so both
  // fixtures are seeded directly with identical hand-written execution
  // records -- matching this project's established pattern for exercising a
  // real but not-yet-CLI-producible branch.
  const record = (executionId, sessionId, tokenValue, tokenCollectedAt, includeWindowSize) => ({
    schemaVersion: "1.0.0",
    executionId,
    workItemId: "WI-FIX",
    status: "finalized",
    startedAt: "2026-01-01T00:00:00.000Z",
    finalizedAt: "2026-01-01T00:10:00.000Z",
    identity: { provider: "anthropic", runtime: "claude-code", sessionId },
    capabilities: [],
    metrics: [
      {
        measurementId: `MEAS-${executionId}`,
        id: "session.tokens.cumulative",
        value: tokenValue,
        unit: "tokens",
        currency: null,
        quality: "observed",
        confidence: null,
        scope: "execution",
        aggregation: "latest-per-session",
        dimensions: {},
        pricing: null,
        collectedAt: tokenCollectedAt
      },
      ...(includeWindowSize
        ? [
            {
              measurementId: `MEAS-WINDOW-${executionId}`,
              id: "context.window_size",
              value: 200000,
              unit: "tokens",
              currency: null,
              quality: "observed",
              confidence: null,
              scope: "execution",
              aggregation: "none",
              dimensions: {},
              pricing: null,
              collectedAt: tokenCollectedAt
            }
          ]
        : [])
    ],
    events: [],
    repository: { start: { available: false } },
    links: {}
  });

  const records = [
    record("EXE-FIX-1", "sess-A", 100, "2026-01-01T00:01:00.000Z", true),
    record("EXE-FIX-2", "sess-A", 150, "2026-01-01T00:02:00.000Z", false),
    record("EXE-FIX-3", "sess-B", 50, "2026-01-01T00:01:30.000Z", false)
  ];

  for (const root of [nodeRoot, fsharpRoot]) {
    const directory = path.join(root, ".ros", "telemetry", "executions");
    fs.mkdirSync(directory, { recursive: true });
    for (const rec of records) fs.writeFileSync(path.join(directory, `${rec.executionId}.json`), JSON.stringify(rec, null, 2));
  }

  const nodeSummary = summarizeTelemetry(nodeRoot, "WI-FIX");
  const fsharpResult = runFsharp(fsharpRoot, ["summary", "WI-FIX"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpSummary = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(nodeSummary, fsharpSummary);

  const nodeTokens = metricValue(nodeSummary, "session.tokens.cumulative");
  // sess-A's latest (150, EXE-FIX-2) plus sess-B's only sample (50) = 200.
  assert.equal(nodeTokens.value, 200);
  assert.equal(nodeTokens.aggregation, "latest-per-session");
  assert.equal(nodeTokens.measurements, 3);
  assert.equal(nodeTokens.note, "latest value per unique provider session; cumulative snapshots are not summed");

  const nodeWindow = metricValue(nodeSummary, "context.window_size");
  assert.equal(nodeWindow.value, null);
  assert.equal(nodeWindow.aggregation, "none");
  assert.equal(nodeWindow.measurements, 1);
  assert.equal(nodeWindow.note, "not aggregated; inspect per-execution measurements");
});
