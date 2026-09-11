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

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-telemetry-lifecycle-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 100 }));
  initializeProject({ target: root, project: "Work Telemetry Lifecycle Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function readExecutions(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8")));
}

function runFsharp(root, command, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", command, ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function metricValue(record, id) {
  const metric = record.metrics.find((entry) => entry.id === id);
  return metric ? metric.value : undefined;
}

function lifecycleEvents(record) {
  return record.events.filter((event) => event.type.startsWith("work."));
}

test("F# work block/work resume record real telemetry lifecycle events, and work complete computes a real nonzero blocked duration, matching production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharpRoot = fixture(t, "fsharp");

  runFsharp(fsharpRoot, "start", ["--id", "WI-CYCLE", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"]);

  const fsharpBlock = runFsharp(fsharpRoot, "block", ["--id", "WI-CYCLE", "--reason", "waiting on review", "--occurred-at", "2026-09-10T18:05:00.000Z"]);
  assert.equal(fsharpBlock.status, 0, fsharpBlock.stderr);

  const fsharpResume = runFsharp(fsharpRoot, "resume", ["--id", "WI-CYCLE", "--occurred-at", "2026-09-10T18:10:00.000Z"]);
  assert.equal(fsharpResume.status, 0, fsharpResume.stderr);

  fs.writeFileSync(path.join(fsharpRoot, "IMPLEMENTATION-NOTES.md"), "Implemented.\n");
  fs.writeFileSync(path.join(fsharpRoot, "TESTS-NOTES.md"), "Tested.\n");

  const fsharpComplete = runFsharp(fsharpRoot, "complete", [
    "--id", "WI-CYCLE", "--occurred-at", "2026-09-10T18:15:00.000Z",
    "--evidence", "implementation=IMPLEMENTATION-NOTES.md", "--evidence", "tests=TESTS-NOTES.md"
  ]);
  assert.equal(fsharpComplete.status, 0, fsharpComplete.stderr);

  const fsharpExecution = readExecutions(fsharpRoot)[0];
  assert.equal(fsharpExecution.status, "finalized");

  const fsharpLifecycle = lifecycleEvents(fsharpExecution);
  assert.equal(fsharpLifecycle.length, 2);

  const fsharpBlocked = fsharpLifecycle.find((event) => event.type === "work.blocked");
  assert.ok(fsharpBlocked);
  assert.equal(fsharpBlocked.reason, "waiting on review");
  assert.deepEqual(fsharpBlocked.source, { type: "ros-clock", name: "ros", mechanism: "work-lifecycle" });

  const fsharpResumed = fsharpLifecycle.find((event) => event.type === "work.resumed");
  assert.ok(fsharpResumed);
  assert.equal(fsharpResumed.reason, null);

  // agent.interruptions/agent.resumes are exact counters (never time-based),
  // so these compare directly against production's known real value rather
  // than just checking presence.
  assert.equal(metricValue(fsharpExecution, "agent.interruptions"), 1);
  assert.equal(metricValue(fsharpExecution, "agent.resumes"), 1);

  const fsharpInterruptionsCapability = fsharpExecution.capabilities.find((entry) => entry.metricId === "agent.interruptions");
  assert.equal(fsharpInterruptionsCapability.status, "derived");

  // time.blocked_ms is real-clock-derived on production's own Node side
  // (only ever positive, never an exact value) but fully deterministic on
  // the F# side, since it is computed from the synthetic --occurred-at
  // timestamps this test controls exactly: 18:10:00 - 18:05:00 = 5 minutes.
  assert.equal(metricValue(fsharpExecution, "time.blocked_ms"), 5 * 60 * 1000);
});

test("F# work block with no currently-active execution is a legitimate no-op, matching production", (t) => {
  const fsharpRoot = fixture(t, "no-active-fsharp");

  // A backlog-only item block never reaches telemetry at all -- no
  // execution exists for it yet.
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "capture", "--id", "WI-BACKLOG", "--title", "Backlog item", "--occurred-at", "2026-09-10T18:00:00.000Z"]);
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "backlog-transition", "--id", "WI-BACKLOG", "--action", "ready", "--occurred-at", "2026-09-10T18:01:00.000Z"]);

  const fsharpResult = runFsharp(fsharpRoot, "block", ["--id", "WI-BACKLOG", "--reason", "waiting", "--occurred-at", "2026-09-10T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const executionsDir = path.join(fsharpRoot, ".ros", "telemetry", "executions");
  assert.ok(!fs.existsSync(executionsDir) || fs.readdirSync(executionsDir).length === 0);
});
