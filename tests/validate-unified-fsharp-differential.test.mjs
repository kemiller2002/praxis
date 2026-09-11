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
// `validate` command with the exact same call sequence as each test, then
// frozen here. Node is retained in this repository only as the web server's
// internal dependency (DF-ROS-2026-A033) and is no longer executed as a live
// oracle by this test suite.
const GOLDEN = {
  clean: {
    textStdout: "validation passed\n",
    json: { valid: true, findings: [] }
  },
  combined: {
    json: {
      valid: false,
      findings: [
        {
          severity: "error",
          path: ".ros/work/queue.json",
          field: "status",
          message: "invalid status 'not-a-status' for 'WI-BAD'",
          repair: "Correct the named file and field, then run './ros validate' again."
        },
        {
          severity: "error",
          path: "ros.json",
          field: "telemetry.disabledReason",
          message: "disabled telemetry requires an explicit reason",
          repair: "Correct the named file and field, then run './ros validate' again."
        }
      ]
    }
  },
  stale: {
    json: {
      valid: false,
      findings: [
        {
          severity: "error",
          path: "registries/decisions.json",
          field: null,
          message: "registry is stale; run 'ros registry build'",
          repair: "Run './ros registry build'."
        }
      ]
    }
  },
  artifactAndAttribution: {
    json: {
      valid: false,
      findings: [
        {
          severity: "error",
          path: "research/decisions/DF-BROKEN--test.md",
          field: "id",
          message: "filename must start with 'not valid!!--'",
          repair: "Correct the named file and field, then run './ros validate' again."
        },
        {
          severity: "error",
          path: "research/decisions/DF-BROKEN--test.md",
          field: "id",
          message: "invalid identifier 'not valid!!'",
          repair: "Correct the named file and field, then run './ros validate' again."
        },
        {
          severity: "error",
          path: "research/decisions/DF-BROKEN--test.md",
          field: "work_items",
          message: "meaningful change has no active or completed work-item attribution",
          repair: "Run './ros work begin WORK-ID', perform the change, then complete it with configured evidence."
        }
      ]
    },
    textStatus: 1,
    textStdout: "",
    textStderr:
      "ERROR research/decisions/DF-BROKEN--test.md:id: filename must start with 'not valid!!--'\n" +
      "  REPAIR Correct the named file and field, then run './ros validate' again.\n" +
      "ERROR research/decisions/DF-BROKEN--test.md:id: invalid identifier 'not valid!!'\n" +
      "  REPAIR Correct the named file and field, then run './ros validate' again.\n" +
      "ERROR research/decisions/DF-BROKEN--test.md:work_items: meaningful change has no active or completed work-item attribution\n" +
      "  REPAIR Run './ros work begin WORK-ID', perform the change, then complete it with configured evidence.\n" +
      "validation failed with 3 error(s)\n"
  }
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-validate-unified-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Validate Unified Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  execFileSync("git", ["-C", root, "-c", "user.email=a@b.c", "-c", "user.name=a", "add", "-A"]);
  execFileSync("git", ["-C", root, "-c", "user.email=a@b.c", "-c", "user.name=a", "commit", "-q", "-m", "init"]);
  return root;
}

function fsharpValidate(root, extraArgs = []) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "validate", ...extraArgs], { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test("F# unified validate matches production for a clean bootstrap, both --json and text output", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const root = fixture(t, "clean");

  const fsharpText = fsharpValidate(root);
  assert.equal(fsharpText.status, 0);
  assert.equal(fsharpText.stdout, GOLDEN.clean.textStdout);

  const fsharpJson = fsharpValidate(root, ["--json"]);
  assert.deepEqual(JSON.parse(fsharpJson.stdout), GOLDEN.clean.json);
});

test("F# unified validate matches production combining a backlog-queue finding and a disabled-telemetry finding", (t) => {
  const root = fixture(t, "combined");

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

  const rosConfigPath = path.join(root, "ros.json");
  const rosConfig = JSON.parse(fs.readFileSync(rosConfigPath, "utf8"));
  rosConfig.telemetry = { enabled: false };
  fs.writeFileSync(rosConfigPath, JSON.stringify(rosConfig, null, 2));

  const fsharpJson = JSON.parse(fsharpValidate(root, ["--json"]).stdout);
  assert.deepEqual(fsharpJson, GOLDEN.combined.json);
  assert.equal(fsharpJson.findings.length, 2);
});

test("F# unified validate matches production for a stale registry, sorted together with other findings", (t) => {
  const root = fixture(t, "stale");

  const decisionsRegistry = path.join(root, "registries", "decisions.json");
  fs.writeFileSync(decisionsRegistry, JSON.stringify({ broken: true }));

  const fsharpJson = JSON.parse(fsharpValidate(root, ["--json"]).stdout);
  assert.deepEqual(fsharpJson, GOLDEN.stale.json);
  assert.ok(fsharpJson.findings.some((f) => f.message.includes("registry is stale")));
});

test("F# unified validate matches production combining an artifact parse finding, a work-attribution finding, and text-format rendering", (t) => {
  const root = fixture(t, "artifact-and-attribution");

  fs.writeFileSync(
    path.join(root, "research", "decisions", "DF-BROKEN--test.md"),
    "---\nid: not valid!!\ntitle: Broken\nstatus: accepted\n---\nBody.\n"
  );

  const fsharpJson = JSON.parse(fsharpValidate(root, ["--json"]).stdout);
  assert.deepEqual(fsharpJson, GOLDEN.artifactAndAttribution.json);
  assert.ok(fsharpJson.findings.length >= 3, "expected at least the artifact id/filename findings plus the work-attribution finding");

  const fsharpText = fsharpValidate(root);
  assert.equal(fsharpText.status, GOLDEN.artifactAndAttribution.textStatus);
  assert.equal(fsharpText.stdout, GOLDEN.artifactAndAttribution.textStdout);
  assert.equal(fsharpText.stderr, GOLDEN.artifactAndAttribution.textStderr);
});
