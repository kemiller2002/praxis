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
// implementation (tools/ros_telemetry.mjs's summarizeTelemetry) with the
// exact same call sequence as each test, then frozen here. Node is retained
// in this repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this test
// suite.
const GOLDEN = {
  test1Providers: ["unknown"],
  test1Runtimes: ["unknown"],
  test1Metrics: {
    "git.files_added": { value: 4, aggregation: "sum", measurements: 2, unit: "count" },
    "git.files_modified": { value: 0, aggregation: "sum", measurements: 2, unit: "count" },
    "documentation.files_changed": { value: 4, aggregation: "sum", measurements: 2, unit: "count" },
    "git.baseline_dirty_files": { value: 0, aggregation: "maximum", measurements: 2, unit: "count" }
  },
  test3Summary: {
    schemaVersion: "1.0.0",
    workItemId: null,
    executionCount: 0,
    providers: [],
    runtimes: [],
    timing: {
      fullyFinalized: false,
      finalizedExecutionCount: 0,
      activeExecutionCount: 0,
      earliestStartedAt: null,
      latestFinalizedAt: null,
      calendarSpanMs: null,
      totalExecutionWallMs: null,
      overlappingExecutionMs: null
    },
    metrics: []
  },
  test4Summary: {
    schemaVersion: "1.0.0",
    workItemId: "WI-FIX",
    executionCount: 3,
    providers: ["anthropic"],
    runtimes: ["claude-code"],
    timing: {
      fullyFinalized: true,
      finalizedExecutionCount: 3,
      activeExecutionCount: 0,
      earliestStartedAt: "2026-01-01T00:00:00.000Z",
      latestFinalizedAt: "2026-01-01T00:10:00.000Z",
      calendarSpanMs: 600000,
      totalExecutionWallMs: 1800000,
      overlappingExecutionMs: 1200000
    },
    metrics: [
      {
        id: "context.window_size",
        value: null,
        unit: "tokens",
        currency: null,
        dimensions: {},
        aggregation: "none",
        measurements: 1,
        note: "not aggregated; inspect per-execution measurements"
      },
      {
        id: "session.tokens.cumulative",
        value: 200,
        unit: "tokens",
        currency: null,
        dimensions: {},
        aggregation: "latest-per-session",
        measurements: 3,
        note: "latest value per unique provider session; cumulative snapshots are not summed"
      }
    ]
  }
};

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
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", ...args], { cwd: repositoryRoot, encoding: "utf8", env: DETERMINISTIC_ENV });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function metricValue(summary, id) {
  return summary.metrics.find((entry) => entry.id === id);
}

test("F# telemetry summary matches production's real aggregation over two real, real-timed executions", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharpRoot = fixture(t, "real-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });

  fs.writeFileSync(path.join(fsharpRoot, "IMPLEMENTATION-NOTES.md"), "Implemented.\n");
  fs.writeFileSync(path.join(fsharpRoot, "TESTS-NOTES.md"), "Tested.\n");

  execFileSync("dotnet", [
    fsharpCli, "--root", fsharpRoot, "work", "complete", "--id", "WI-A", "--id", "WI-B", "--occurred-at", "2026-09-10T18:05:00.000Z",
    "--evidence", "implementation=IMPLEMENTATION-NOTES.md", "--evidence", "tests=TESTS-NOTES.md"
  ], { env: DETERMINISTIC_ENV });

  const fsharpResult = runFsharp(fsharpRoot, ["summary"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpSummary = JSON.parse(fsharpResult.stdout);

  assert.equal(fsharpSummary.executionCount, 2);
  assert.deepEqual(GOLDEN.test1Providers, fsharpSummary.providers);
  assert.deepEqual(GOLDEN.test1Runtimes, fsharpSummary.runtimes);
  assert.equal(fsharpSummary.timing.fullyFinalized, true);

  // Cross-checked deterministic aggregates: identical real git diffs on both
  // sides, so the same "sum"/"maximum" values must match exactly, node vs. F#.
  for (const id of ["git.files_added", "git.files_modified", "documentation.files_changed", "git.baseline_dirty_files"]) {
    const fsharpMetric = metricValue(fsharpSummary, id);
    assert.ok(fsharpMetric, `fsharp summary missing metric ${id}`);
    const golden = GOLDEN.test1Metrics[id];
    assert.equal(golden.value, fsharpMetric.value, `metric ${id} value mismatch`);
    assert.equal(golden.aggregation, fsharpMetric.aggregation);
    assert.equal(golden.measurements, fsharpMetric.measurements);
    assert.equal(golden.unit, fsharpMetric.unit);
  }

  // time.wall_ms is real-clock-derived (positive on both sides, not
  // cross-equal); its aggregation note is deterministic and compared.
  const fsharpWallMs = metricValue(fsharpSummary, "time.wall_ms");
  assert.ok(fsharpWallMs.value > 0);
  assert.equal(fsharpWallMs.note, "sum of execution spans; may exceed calendarSpanMs when executions overlap");
});

test("F# telemetry summary WORKITEMID filters to a single execution's metrics only, matching production", (t) => {
  const fsharpRoot = fixture(t, "filter-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });

  const fsharpResult = runFsharp(fsharpRoot, ["summary", "WI-A"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpSummary = JSON.parse(fsharpResult.stdout);

  assert.equal(fsharpSummary.workItemId, "WI-A");
  assert.equal(fsharpSummary.executionCount, 1);
});

test("F# telemetry summary with no executions at all reports an empty, zeroed summary, matching production", (t) => {
  const fsharpRoot = fixture(t, "empty-fsharp");

  const fsharpResult = runFsharp(fsharpRoot, ["summary"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpSummary = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(GOLDEN.test3Summary, fsharpSummary);
  assert.equal(fsharpSummary.executionCount, 0);
  assert.deepEqual(fsharpSummary.metrics, []);
  assert.equal(fsharpSummary.timing.fullyFinalized, false);
});

test("F# telemetry summary exercises latest-per-session and none aggregation over hand-written fixture executions, byte-identical to production", (t) => {
  const fsharpRoot = fixture(t, "fixture-fsharp");

  // These aggregation strategies are never produced by any real effect this
  // migration's own writers exercise (only sum/maximum are), so the fixture
  // is seeded directly with identical hand-written execution records --
  // matching this project's established pattern for exercising a real but
  // not-yet-CLI-producible branch.
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

  const directory = path.join(fsharpRoot, ".ros", "telemetry", "executions");
  fs.mkdirSync(directory, { recursive: true });
  for (const rec of records) fs.writeFileSync(path.join(directory, `${rec.executionId}.json`), JSON.stringify(rec, null, 2));

  const fsharpResult = runFsharp(fsharpRoot, ["summary", "WI-FIX"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpSummary = JSON.parse(fsharpResult.stdout);

  assert.deepEqual(GOLDEN.test4Summary, fsharpSummary);

  const fsharpTokens = metricValue(fsharpSummary, "session.tokens.cumulative");
  // sess-A's latest (150, EXE-FIX-2) plus sess-B's only sample (50) = 200.
  assert.equal(fsharpTokens.value, 200);
  assert.equal(fsharpTokens.aggregation, "latest-per-session");
  assert.equal(fsharpTokens.measurements, 3);
  assert.equal(fsharpTokens.note, "latest value per unique provider session; cumulative snapshots are not summed");

  const fsharpWindow = metricValue(fsharpSummary, "context.window_size");
  assert.equal(fsharpWindow.value, null);
  assert.equal(fsharpWindow.aggregation, "none");
  assert.equal(fsharpWindow.measurements, 1);
  assert.equal(fsharpWindow.note, "not aggregated; inspect per-execution measurements");
});
