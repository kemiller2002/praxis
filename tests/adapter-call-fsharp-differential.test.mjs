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
// implementation (tools/ros_cli.mjs's callFileAdapter) with the exact same
// call sequence as each test, then frozen here. Node is retained in this
// repository only as the web server's internal dependency (DF-ROS-2026-A033)
// and is no longer executed as a live oracle by this test suite.
const GOLDEN = {
  test1: {
    readStdout: JSON.stringify(
      {
        schemaVersion: "1.0.0",
        protocolVersion: "1.0.0",
        requestId: "req-get-1",
        operation: "getWorkItem",
        outcome: "success",
        data: { workItem: { id: "FEAT-900", state: "ready", type: "feature" } }
      },
      null,
      2
    ),
    transitionStdout: JSON.stringify(
      {
        schemaVersion: "1.0.0",
        protocolVersion: "1.0.0",
        requestId: "req-transition-1",
        operation: "transitionWorkItem",
        outcome: "success",
        data: { workItem: { id: "FEAT-900", state: "active", type: "feature", updatedBy: "agent:test" } }
      },
      null,
      2
    ),
    store: {
      schemaVersion: "1.0.0",
      protocolVersion: "1.0.0",
      repositories: ["protocol-consumer"],
      workItems: { "FEAT-900": { id: "FEAT-900", state: "active", type: "feature", updatedBy: "agent:test" } },
      events: [],
      requests: {
        "req-get-1": {
          schemaVersion: "1.0.0",
          protocolVersion: "1.0.0",
          requestId: "req-get-1",
          operation: "getWorkItem",
          outcome: "success",
          data: { workItem: { id: "FEAT-900", state: "ready", type: "feature" } }
        },
        "req-transition-1": {
          schemaVersion: "1.0.0",
          protocolVersion: "1.0.0",
          requestId: "req-transition-1",
          operation: "transitionWorkItem",
          outcome: "success",
          data: { workItem: { id: "FEAT-900", state: "active", type: "feature", updatedBy: "agent:test" } }
        }
      }
    }
  },
  test2: {
    firstStdout: JSON.stringify(
      {
        schemaVersion: "1.0.0",
        protocolVersion: "1.0.0",
        requestId: "req-same",
        operation: "transitionWorkItem",
        outcome: "success",
        data: { workItem: { id: "FEAT-900", state: "active", type: "feature", updatedBy: "agent:test" } }
      },
      null,
      2
    ),
    conflictStdout: JSON.stringify(
      {
        schemaVersion: "1.0.0",
        protocolVersion: "1.0.0",
        requestId: "req-conflict",
        operation: "transitionWorkItem",
        outcome: "failure",
        error: { code: "state_conflict", message: "expected 'ready', found 'active'" }
      },
      null,
      2
    ),
    store: {
      schemaVersion: "1.0.0",
      protocolVersion: "1.0.0",
      repositories: ["protocol-consumer"],
      workItems: { "FEAT-900": { id: "FEAT-900", state: "active", type: "feature", updatedBy: "agent:test" } },
      events: [],
      requests: {
        "req-same": {
          schemaVersion: "1.0.0",
          protocolVersion: "1.0.0",
          requestId: "req-same",
          operation: "transitionWorkItem",
          outcome: "success",
          data: { workItem: { id: "FEAT-900", state: "active", type: "feature", updatedBy: "agent:test" } }
        },
        "req-conflict": {
          schemaVersion: "1.0.0",
          protocolVersion: "1.0.0",
          requestId: "req-conflict",
          operation: "transitionWorkItem",
          outcome: "failure",
          error: { code: "state_conflict", message: "expected 'ready', found 'active'" }
        }
      }
    }
  },
  test3: {
    results: [
      {
        status: 1,
        stdout: JSON.stringify(
          {
            schemaVersion: "1.0.0",
            protocolVersion: "1.0.0",
            requestId: "req-forbidden",
            operation: "transitionWorkItem",
            outcome: "failure",
            error: { code: "forbidden", message: "principal lacks work:transition scope" }
          },
          null,
          2
        )
      },
      {
        status: 1,
        stdout: JSON.stringify(
          {
            schemaVersion: "1.0.0",
            protocolVersion: "1.0.0",
            requestId: "req-repo",
            operation: "transitionWorkItem",
            outcome: "failure",
            error: { code: "repository_unknown", message: "repository 'other' is not authorized" }
          },
          null,
          2
        )
      },
      {
        status: 1,
        stdout: JSON.stringify(
          {
            schemaVersion: "1.0.0",
            protocolVersion: "2.0.0",
            requestId: "req-version",
            operation: "transitionWorkItem",
            outcome: "failure",
            error: { code: "protocol_mismatch", message: "adapter supports protocol 1.0.0" }
          },
          null,
          2
        )
      },
      {
        status: 2,
        stdout: JSON.stringify(
          {
            schemaVersion: "1.0.0",
            protocolVersion: "1.0.0",
            requestId: "req-unknown",
            operation: "transitionWorkItem",
            outcome: "unknown",
            error: { code: "remote_outcome_unknown", message: "the remote effect could not be confirmed" }
          },
          null,
          2
        )
      }
    ],
    store: {
      schemaVersion: "1.0.0",
      protocolVersion: "1.0.0",
      repositories: ["protocol-consumer"],
      workItems: { "FEAT-900": { id: "FEAT-900", state: "ready", type: "feature" } },
      events: [],
      requests: {
        "req-forbidden": {
          schemaVersion: "1.0.0",
          protocolVersion: "1.0.0",
          requestId: "req-forbidden",
          operation: "transitionWorkItem",
          outcome: "failure",
          error: { code: "forbidden", message: "principal lacks work:transition scope" }
        },
        "req-repo": {
          schemaVersion: "1.0.0",
          protocolVersion: "1.0.0",
          requestId: "req-repo",
          operation: "transitionWorkItem",
          outcome: "failure",
          error: { code: "repository_unknown", message: "repository 'other' is not authorized" }
        },
        "req-unknown": {
          schemaVersion: "1.0.0",
          protocolVersion: "1.0.0",
          requestId: "req-unknown",
          operation: "transitionWorkItem",
          outcome: "unknown",
          error: { code: "remote_outcome_unknown", message: "the remote effect could not be confirmed" }
        }
      }
    }
  },
  test4: {
    store: {
      schemaVersion: "1.0.0",
      protocolVersion: "1.0.0",
      repositories: ["protocol-consumer"],
      workItems: { "FEAT-900": { id: "FEAT-900", state: "ready", type: "feature" } },
      events: [{ eventId: "evt-1", type: "work.completed" }],
      requests: {
        "req-event-1": {
          schemaVersion: "1.0.0",
          protocolVersion: "1.0.0",
          requestId: "req-event-1",
          operation: "publishRepositoryEvent",
          outcome: "success",
          data: { eventId: "evt-1" }
        },
        "req-event-2": {
          schemaVersion: "1.0.0",
          protocolVersion: "1.0.0",
          requestId: "req-event-2",
          operation: "publishRepositoryEvent",
          outcome: "success",
          data: { eventId: "evt-1" }
        }
      }
    }
  }
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-adapter-call-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Adapter Call Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function writeStore(root, store) {
  fs.writeFileSync(path.join(root, "adapter-store.json"), JSON.stringify(store, null, 2));
}

function baseStore() {
  return {
    schemaVersion: "1.0.0",
    protocolVersion: "1.0.0",
    repositories: ["protocol-consumer"],
    workItems: { "FEAT-900": { id: "FEAT-900", state: "ready", type: "feature" } },
    events: [],
    requests: {}
  };
}

function writeRequest(root, request) {
  fs.writeFileSync(path.join(root, "adapter-request.json"), JSON.stringify(request));
}

function readStore(root) {
  return JSON.parse(fs.readFileSync(path.join(root, "adapter-store.json"), "utf8"));
}

function fsharpCall(root) {
  const result = spawnSync(
    "dotnet",
    [fsharpCli, "--root", root, "adapter", "call", "--store", "adapter-store.json", "--request", "adapter-request.json"],
    { cwd: repositoryRoot, encoding: "utf8" }
  );
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

test("F# adapter call reads and transitions a work item without vendor assumptions, byte-identical to production", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharp = fixture(t, "read-transition-fsharp");
  writeStore(fsharp, baseStore());

  const readRequest = {
    protocolVersion: "1.0.0",
    requestId: "req-get-1",
    operation: "getWorkItem",
    repository: "protocol-consumer",
    principal: "agent:test",
    scopes: ["work:read"],
    workItem: "FEAT-900"
  };
  writeRequest(fsharp, readRequest);
  const fsharpRead = fsharpCall(fsharp);
  assert.equal(fsharpRead.status, 0, fsharpRead.stderr);
  assert.equal(fsharpRead.stdout.trim(), GOLDEN.test1.readStdout);

  const transitionRequest = {
    protocolVersion: "1.0.0",
    requestId: "req-transition-1",
    operation: "transitionWorkItem",
    repository: "protocol-consumer",
    principal: "agent:test",
    scopes: ["work:transition"],
    workItem: "FEAT-900",
    expectedState: "ready",
    targetState: "active"
  };
  writeRequest(fsharp, transitionRequest);
  const fsharpTransition = fsharpCall(fsharp);
  assert.equal(fsharpTransition.status, 0, fsharpTransition.stderr);
  assert.equal(fsharpTransition.stdout.trim(), GOLDEN.test1.transitionStdout);
  assert.deepEqual(readStore(fsharp), GOLDEN.test1.store);
});

test("F# adapter call retries are idempotent and state conflicts are explicit, matching production", (t) => {
  const fsharp = fixture(t, "idempotent-fsharp");
  writeStore(fsharp, baseStore());

  const request = {
    protocolVersion: "1.0.0",
    requestId: "req-same",
    operation: "transitionWorkItem",
    repository: "protocol-consumer",
    principal: "agent:test",
    scopes: ["work:transition"],
    workItem: "FEAT-900",
    expectedState: "ready",
    targetState: "active"
  };
  writeRequest(fsharp, request);
  const fsharpFirst = fsharpCall(fsharp);
  const fsharpRetry = fsharpCall(fsharp);
  assert.equal(fsharpRetry.stdout.trim(), fsharpFirst.stdout.trim());
  assert.equal(fsharpFirst.stdout.trim(), GOLDEN.test2.firstStdout);

  const conflictRequest = { ...request, requestId: "req-conflict", targetState: "complete" };
  writeRequest(fsharp, conflictRequest);
  const fsharpConflict = fsharpCall(fsharp);
  assert.equal(fsharpConflict.status, 1);
  assert.equal(fsharpConflict.stdout.trim(), GOLDEN.test2.conflictStdout);
  assert.deepEqual(readStore(fsharp), GOLDEN.test2.store);
});

test("F# adapter call enforces repository, authorization, protocol, and unknown outcomes, matching production", (t) => {
  const fsharp = fixture(t, "enforce-fsharp");
  writeStore(fsharp, baseStore());

  const base = {
    protocolVersion: "1.0.0",
    operation: "transitionWorkItem",
    repository: "protocol-consumer",
    principal: "agent:test",
    scopes: [],
    workItem: "FEAT-900",
    targetState: "active"
  };

  const scenarios = [
    { ...base, requestId: "req-forbidden" },
    { ...base, requestId: "req-repo", repository: "other" },
    { ...base, requestId: "req-version", protocolVersion: "2.0.0" },
    { ...base, requestId: "req-unknown", scopes: ["work:transition"], simulateOutcome: "unknown" }
  ];

  scenarios.forEach((scenario, index) => {
    writeRequest(fsharp, scenario);
    const fsharpResult = fsharpCall(fsharp);
    const golden = GOLDEN.test3.results[index];
    assert.equal(fsharpResult.status, golden.status, JSON.stringify(scenario));
    assert.equal(fsharpResult.stdout.trim(), golden.stdout, JSON.stringify(scenario));
  });

  const fsharpStore = readStore(fsharp);
  assert.equal(fsharpStore.workItems["FEAT-900"].state, "ready");
  assert.deepEqual(fsharpStore, GOLDEN.test3.store);
});

test("F# adapter call deduplicates published event IDs, matching production", (t) => {
  const fsharp = fixture(t, "publish-fsharp");
  writeStore(fsharp, baseStore());

  const base = {
    protocolVersion: "1.0.0",
    operation: "publishRepositoryEvent",
    repository: "protocol-consumer",
    principal: "ci:test",
    scopes: ["event:publish"],
    event: { eventId: "evt-1", type: "work.completed" }
  };

  writeRequest(fsharp, { ...base, requestId: "req-event-1" });
  assert.equal(fsharpCall(fsharp).status, 0);

  writeRequest(fsharp, { ...base, requestId: "req-event-2" });
  assert.equal(fsharpCall(fsharp).status, 0);

  const fsharpStore = readStore(fsharp);
  assert.equal(fsharpStore.events.length, 1);
  assert.deepEqual(fsharpStore, GOLDEN.test4.store);
});

test("F# adapter call rejects a missing required field and a missing request file with production's exact messages", (t) => {
  const fsharp = fixture(t, "missing-fsharp");
  writeStore(fsharp, baseStore());

  writeRequest(fsharp, { requestId: "r1", operation: "getWorkItem", repository: "x", principal: "p" });
  const fsharpMissing = fsharpCall(fsharp);
  assert.equal(fsharpMissing.status, 1);
  assert.equal(fsharpMissing.stderr.trim(), "ERROR adapter request is missing 'protocolVersion'");

  const fsharpNotFound = spawnSync(
    "dotnet",
    [fsharpCli, "--root", fsharp, "adapter", "call", "--store", "adapter-store.json", "--request", "nope.json"],
    { cwd: repositoryRoot, encoding: "utf8" }
  );
  assert.equal(fsharpNotFound.status, 1);
  assert.equal(fsharpNotFound.stderr.trim(), "ERROR adapter request not found: nope.json");
});
