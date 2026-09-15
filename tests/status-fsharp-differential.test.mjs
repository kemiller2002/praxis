import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const installWorkItemId = `ROS-INSTALL-${JSON.parse(fs.readFileSync(path.join(repositoryRoot, "package.json"), "utf8")).version.replaceAll(".", "-")}`;
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// Golden masters below were captured once from production's own Node `status`
// command with the exact same call sequence as each test, then frozen here.
// Node is retained in this repository only as the web server's internal
// dependency (DF-ROS-2026-A033) and is no longer executed as a live oracle
// by this test suite.
const GOLDEN = {
  test1: {
    repository: "status-differential",
    protocolVersion: "1.0.0",
    validation: "passed",
    findingCount: 0,
    workItems: [
      {
        id: installWorkItemId,
        type: "mechanical",
        state: "complete",
        semanticState: "complete",
        allowedActions: [],
        hasExecIds: false
      }
    ],
    telemetry: { executionCount: 0, activeExecutionCount: 0 },
    nextActions: ["Select an allowed work transition or begin a new work item."]
  },
  test2: {
    repository: "status-differential",
    protocolVersion: "1.0.0",
    validation: "failed",
    findingCount: 1,
    workItems: [
      {
        id: installWorkItemId,
        type: "mechanical",
        state: "complete",
        semanticState: "complete",
        allowedActions: [],
        hasExecIds: false
      },
      {
        id: "WI-READY",
        type: "task",
        state: "active",
        semanticState: "active",
        allowedActions: ["block", "complete"],
        hasExecIds: true
      },
      {
        id: "WI-ACTIVE",
        type: "task",
        state: "blocked",
        semanticState: "blocked",
        allowedActions: ["resume"],
        hasExecIds: true
      }
    ],
    telemetry: { executionCount: 2, activeExecutionCount: 2 },
    nextActions: ["Correct the named file and field, then run './ros validate' again."]
  }
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-status-${label}-`));
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
  initializeProject({ target: root, project: "Status Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  return root;
}

function fsharpStatus(root) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "status"], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function fsharpWork(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", ...args], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

// `installation` is an additive block the lifecycle interface introduced; it
// did not exist when these golden masters were captured. Stripping it here
// keeps the goldens proving exactly what they were frozen to prove -- that
// every pre-existing key is byte-for-byte unchanged -- while the dedicated
// assertions below cover the new block. Any change to a pre-existing key
// still fails these tests.
function normalize(record) {
  const { installation, ...rest } = record;
  return {
    ...rest,
    workItems: record.workItems.map(({ telemetryExecutionIds, ...itemRest }) => ({ ...itemRest, hasExecIds: telemetryExecutionIds.length > 0 }))
  };
}

const packageMetadata = JSON.parse(fs.readFileSync(path.join(repositoryRoot, "package.json"), "utf8"));

function assertCommonInstallationFields(installation) {
  assert.ok(installation, "status must carry the additive installation block");
  assert.equal(installation.schemaVersion, 1);
  assert.equal(installation.tool, "ros");
  assert.equal(installation.package, packageMetadata.name);
  // One authoritative version source: the CLI reports package.json's version.
  assert.equal(installation.cliVersion, packageMetadata.version);
  // Not verbose: the full artifact list stays out of the default document.
  assert.equal(installation.managedArtifacts, undefined);
}

// These fixtures are built by `ros-bootstrap init`, which records its state
// only in the legacy .ros/installation.json. That is exactly what an existing
// consumer's repository looks like today, so status must report it as a real
// installation that is one migration behind -- not as missing or broken.
function assertLegacyInstallationBlock(installation) {
  assertCommonInstallationFields(installation);
  assert.equal(installation.state, "upgrade-required");
  assert.equal(installation.installedVersion, "legacy");
  assert.equal(installation.upgradeAvailable, packageMetadata.version);
  assert.equal(installation.configurationVersion, null);
  assert.equal(installation.managedArtifactCount, null);
  assert.equal(installation.verified, true, "a legacy install is a warning, never an error");
}

function assertCurrentInstallationBlock(installation) {
  assertCommonInstallationFields(installation);
  assert.equal(installation.state, "installed");
  assert.equal(installation.installedVersion, packageMetadata.version);
  assert.equal(installation.configurationVersion, 1);
  assert.equal(installation.profile, "greenfield");
  assert.equal(installation.verified, true);
  assert.equal(installation.upgradeAvailable, null);
  assert.ok(installation.managedArtifactCount > 0);
}

test("F# status matches production for a clean bootstrap with no findings", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const root = fixture(t, "clean");

  const fsharpResult = fsharpStatus(root);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpParsed = JSON.parse(fsharpResult.stdout);
  assert.deepEqual(normalize(fsharpParsed), GOLDEN.test1);
  assert.equal(fsharpParsed.validation, "passed");
  assert.deepEqual(fsharpParsed.nextActions, ["Select an allowed work transition or begin a new work item."]);
  assertLegacyInstallationBlock(fsharpParsed.installation);
});

test("status --json is identical to status, and --verbose adds the managed artifact list", (t) => {
  const root = fixture(t, "verbose");

  const plain = fsharpStatus(root);
  assert.equal(plain.status, 0, plain.stderr);

  const explicit = spawnSync("dotnet", [fsharpCli, "--root", root, "status", "--json"], {
    cwd: repositoryRoot,
    encoding: "utf8"
  });
  assert.equal(explicit.status, 0, explicit.stderr);
  assert.equal(explicit.stdout, plain.stdout, "--json must not change the long-standing default output");

  // Adopt the .echelon manifest so there is a managed artifact list to show.
  const upgraded = spawnSync("dotnet", [fsharpCli, "--root", root, "upgrade"], {
    cwd: repositoryRoot,
    encoding: "utf8"
  });
  assert.equal(upgraded.status, 0, upgraded.stderr);

  const verbose = spawnSync("dotnet", [fsharpCli, "--root", root, "status", "--verbose"], {
    cwd: repositoryRoot,
    encoding: "utf8"
  });
  assert.equal(verbose.status, 0, verbose.stderr);

  const verboseParsed = JSON.parse(verbose.stdout);
  assertCurrentInstallationBlock(JSON.parse(fsharpStatus(root).stdout).installation);
  assert.ok(Array.isArray(verboseParsed.installation.managedArtifacts));
  assert.equal(verboseParsed.installation.managedArtifacts.length, verboseParsed.installation.managedArtifactCount);
  for (const artifact of verboseParsed.installation.managedArtifacts) {
    assert.ok(["tool-owned", "generated", "user-owned", "shared"].includes(artifact.ownership), artifact.ownership);
    assert.match(artifact.sha256, /^[0-9a-f]{64}$/);
  }
});

test("F# status matches production with real active/blocked work items, telemetry counts, and a real finding", (t) => {
  const root = fixture(t, "populated");

  // Real (not backdated) timestamps: a new execution's own startedAt is
  // always production's real wall clock, so a synthetic --occurred-at
  // earlier than "now" on a later transition would spuriously fail
  // validate's chronological-order check.
  const at = () => new Date().toISOString();
  assert.equal(fsharpWork(root, ["capture", "--id", "WI-READY", "--title", "Ready item", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["backlog-transition", "--id", "WI-READY", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["start", "--id", "WI-READY", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["capture", "--id", "WI-ACTIVE", "--title", "Active item", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["backlog-transition", "--id", "WI-ACTIVE", "--action", "ready", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["start", "--id", "WI-ACTIVE", "--occurred-at", at()]).status, 0);
  assert.equal(fsharpWork(root, ["block", "--id", "WI-ACTIVE", "--reason", "waiting", "--occurred-at", at()]).status, 0);

  const queuePath = path.join(root, ".ros", "work", "queue.json");
  const queue = JSON.parse(fs.readFileSync(queuePath, "utf8"));
  queue.items.push({
    id: "WI-BAD",
    title: "bad",
    status: "not-a-status",
    tags: [],
    priority: "medium",
    attachments: [],
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
    createdBy: "unknown",
    source: "manual"
  });
  fs.writeFileSync(queuePath, JSON.stringify(queue, null, 2));

  const fsharpResult = fsharpStatus(root);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpParsed = JSON.parse(fsharpResult.stdout);
  assert.deepEqual(normalize(fsharpParsed), GOLDEN.test2);
  assert.equal(fsharpParsed.validation, "failed");
  assert.equal(fsharpParsed.findingCount, 1);
  assert.equal(fsharpParsed.telemetry.executionCount, 2);
  assert.equal(fsharpParsed.telemetry.activeExecutionCount, 2);
  assert.equal(fsharpParsed.workItems.length, 3);
  assertLegacyInstallationBlock(fsharpParsed.installation);
});
