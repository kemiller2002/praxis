import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";
import test from "node:test";

import {
  captureChangeHealth,
  lineBuckets,
  loadChangeHealthPolicy,
  parseChangeHunks,
  showChangeHotspots
} from "../tools/ros_change_health.mjs";

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "praxis-change-health-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.mkdirSync(path.join(root, ".ros", "telemetry"), { recursive: true });
  fs.writeFileSync(path.join(root, "ros.json"), JSON.stringify({
    telemetry: { changeHealthPolicy: "telemetry/change-health.json" },
    workProtocol: { ignoredPaths: [".ros/**"] }
  }, null, 2));
  fs.mkdirSync(path.join(root, "telemetry"), { recursive: true });
  fs.writeFileSync(path.join(root, "telemetry", "change-health.json"), JSON.stringify({
    schemaVersion: "1.0.0",
    enabled: true,
    historyPath: ".ros/telemetry/change-history.json",
    historyWindow: 20,
    maxHistoryUpdates: 200,
    lineBucketSize: 25,
    failOnSeverity: null,
    thresholds: {
      filesChanged: { warning: 25, error: 60 },
      linesChanged: { warning: 800, error: 2000 },
      largestFileChurn: { warning: 300, error: 800 },
      largestChangedFileLines: { warning: 800, error: 1500 },
      hunksChanged: { warning: 30, error: 80 },
      maxHunksPerFile: { warning: 10, error: 25 },
      repeatFileTouches: { warning: 5, error: 10 },
      repeatRegionTouches: { warning: 3, error: 6 }
    }
  }, null, 2));

  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "Praxis Test"], { cwd: root });
  fs.mkdirSync(path.join(root, "src"), { recursive: true });
  fs.writeFileSync(path.join(root, "src", "App.fs"), "module App\n\nlet value = 1\n");
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  const baseline = execFileSync("git", ["rev-parse", "HEAD"], { cwd: root, encoding: "utf8" }).trim();
  return { root, baseline };
}

test("line buckets and zero-context hunks retain ranges without source text", () => {
  assert.deepEqual(lineBuckets(25, 20, 10, 20, 10), [0, 1]);
  const parsed = parseChangeHunks(
    "diff --git a/src/App.fs b/src/App.fs\n--- a/src/App.fs\n+++ b/src/App.fs\n@@ -3 +3 @@\n-old\n+new\n",
    25
  );
  assert.equal(parsed.size, 1);
  assert.deepEqual(parsed.get("src/App.fs"), [{
    oldStart: 3, oldLines: 1, newStart: 3, newLines: 1, buckets: [0]
  }]);
  assert.equal(JSON.stringify(parsed.get("src/App.fs")).includes("old\n"), false);
});

test("policy loader validates repository thresholds", (t) => {
  const { root } = fixture(t);
  const policy = loadChangeHealthPolicy(root);
  assert.equal(policy.historyWindow, 20);
  assert.equal(policy.thresholds.linesChanged.warning, 800);

  const file = path.join(root, "telemetry", "change-health.json");
  const bad = JSON.parse(fs.readFileSync(file, "utf8"));
  bad.thresholds.linesChanged = { warning: 300, error: 200 };
  fs.writeFileSync(file, JSON.stringify(bad, null, 2));
  assert.throws(() => loadChangeHealthPolicy(root), /warning cannot exceed error/);
});

test("two updates to the same file region accumulate file and hunk hotspot history", (t) => {
  const { root, baseline } = fixture(t);
  fs.writeFileSync(path.join(root, "src", "App.fs"), "module App\n\nlet value = 2\n");

  const summary = {
    available: true,
    mechanism: "git-diff-from-clean-execution-baseline",
    startCommit: baseline,
    endCommit: baseline,
    commits: 0,
    linesAdded: 1,
    linesDeleted: 1,
    documentationFilesChanged: 0,
    paths: [{
      status: "M",
      path: "src/App.fs",
      from: null,
      untracked: false,
      lineStats: { added: 1, deleted: 1 }
    }]
  };
  const options = {
    ignored: (file) => file.startsWith(".ros/"),
    isTestFile: () => false,
    isDocumentation: () => false
  };

  const first = captureChangeHealth(
    root,
    { executionId: "EXE-1", workItemId: "WI-1" },
    summary,
    "2026-09-23T10:00:00.000Z",
    options
  );
  assert.equal(first.metrics.repeatFileTouches, 1);
  assert.equal(first.metrics.repeatRegionTouches, 1);

  const second = captureChangeHealth(
    root,
    { executionId: "EXE-2", workItemId: "WI-2" },
    summary,
    "2026-09-23T11:00:00.000Z",
    options
  );
  assert.equal(second.metrics.repeatFileTouches, 2);
  assert.equal(second.metrics.repeatRegionTouches, 2);

  const history = JSON.parse(fs.readFileSync(path.join(root, ".ros", "telemetry", "change-history.json"), "utf8"));
  assert.equal(history.updates.length, 2);
  assert.equal(history.updates[0].files[0].path, "src/App.fs");
  assert.equal(history.updates[0].files[0].hunks[0].buckets[0], 0);

  const hotspots = showChangeHotspots(root);
  assert.equal(hotspots.files[0].path, "src/App.fs");
  assert.equal(hotspots.files[0].touches, 2);
  assert.equal(hotspots.regions[0].path, "src/App.fs");
  assert.equal(hotspots.regions[0].touches, 2);
});

test("history is idempotent for a retried execution", (t) => {
  const { root, baseline } = fixture(t);
  fs.writeFileSync(path.join(root, "src", "App.fs"), "module App\n\nlet value = 3\n");
  const summary = {
    available: true,
    startCommit: baseline,
    endCommit: baseline,
    linesAdded: 1,
    linesDeleted: 1,
    documentationFilesChanged: 0,
    paths: [{ status: "M", path: "src/App.fs", from: null, untracked: false, lineStats: { added: 1, deleted: 1 } }]
  };
  const options = { ignored: () => false, isTestFile: () => false, isDocumentation: () => false };

  captureChangeHealth(root, { executionId: "EXE-1", workItemId: "WI-1" }, summary, "2026-09-23T10:00:00.000Z", options);
  captureChangeHealth(root, { executionId: "EXE-1", workItemId: "WI-1" }, summary, "2026-09-23T10:01:00.000Z", options);

  const history = JSON.parse(fs.readFileSync(path.join(root, ".ros", "telemetry", "change-history.json"), "utf8"));
  assert.equal(history.updates.length, 1);
  assert.equal(history.updates[0].executionId, "EXE-1");
});
