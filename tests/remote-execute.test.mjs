// `praxis remote execute` (PRAXIS-REMOTE-03, DF-ROS-2026-A041): the
// transport-independent boundary that runs one praxis.remote request with
// the same command implementation as the local CLI. These tests exercise
// the built F# binary against real Git repositories and pin the invariants
// #90 requires: local/remote equivalence, idempotent replay after a lost
// result, fail-closed duplicates, stale and dirty repository state,
// capability opt-in, hostile input, secret isolation, and "nothing kept"
// after a refused or interrupted mutation.
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { deterministicIdentityEnv } from "./deterministic-identity-env.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const cli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

const AGENT = { kind: "agent", id: "example/cloud-agent", provider: "example", runtime: "cloud-agent" };
const AGENT_ENV = {
  ROS_ACTOR_KIND: AGENT.kind,
  ROS_ACTOR: AGENT.id,
  ROS_TELEMETRY_PROVIDER: AGENT.provider,
  ROS_TELEMETRY_RUNTIME: AGENT.runtime
};
const PLANTED_TOKEN = "ghp_" + "Q".repeat(36);

function git(root, ...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim();
}

function praxis(root, args, env = {}) {
  const result = spawnSync("dotnet", [cli, "--root", root, ...args], {
    cwd: root,
    encoding: "utf8",
    env: deterministicIdentityEnv(env)
  });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function fixture(t, label, { remoteCapabilities = ["read", "mutate", "complete", "reconcile"] } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `praxis-remote-${label}-`));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Remote Execute" });
  if (remoteCapabilities) {
    const config = JSON.parse(fs.readFileSync(path.join(root, "ros.json"), "utf8"));
    config.remote = { capabilities: remoteCapabilities };
    fs.writeFileSync(path.join(root, "ros.json"), `${JSON.stringify(config, null, 2)}\n`);
  }
  git(root, "init", "-q", "-b", "main");
  git(root, "config", "user.email", "test@example.invalid");
  git(root, "config", "user.name", "Praxis Test");
  assert.equal(praxis(root, ["add", "Remote item", "--id", "WI-0100"]).status, 0);
  assert.equal(praxis(root, ["work", "backlog-transition", "--action", "ready", "--id", "WI-0100", "--occurred-at", new Date().toISOString()]).status, 0);
  git(root, "add", "-A");
  git(root, "commit", "-qm", "baseline");
  return root;
}

function request(root, operation, args, extra = {}) {
  return {
    protocol: "praxis.remote",
    protocolVersion: "1.0",
    requestId: `req-${operation.replace(".", "-")}-0001`,
    operation,
    repository: { ref: "refs/heads/main", expectedSha: git(root, "rev-parse", "HEAD") },
    actor: AGENT,
    arguments: args,
    ...extra
  };
}

function remote(root, body, { grants = ["read", "mutate", "complete", "reconcile"], env = {}, extraArgs = [] } = {}) {
  const file = path.join(os.tmpdir(), `praxis-remote-request-${process.pid}-${Math.random().toString(16).slice(2)}.json`);
  fs.writeFileSync(file, typeof body === "string" ? body : JSON.stringify(body));
  try {
    const result = praxis(root, ["remote", "execute", "--request", file, ...grants.flatMap((grant) => ["--grant", grant]), ...extraArgs], env);
    return { ...result, response: JSON.parse(result.stdout) };
  } finally {
    fs.rmSync(file, { force: true });
  }
}

function status(root) {
  return git(root, "status", "--porcelain", "--untracked-files=all");
}

function readJson(root, relative) {
  return JSON.parse(fs.readFileSync(path.join(root, relative), "utf8"));
}

function readEvents(root) {
  return fs.readFileSync(path.join(root, ".ros", "events", "events.jsonl"), "utf8").split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line));
}

function executions(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.existsSync(dir) ? fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8"))) : [];
}

const VOLATILE = new Set([
  "executionId", "startedAt", "discoveredAt", "lastAssessedAt", "recordedAt", "collectedAt", "measurementId",
  "commit", "branch", "dirtyPaths", "dirty", "commits", "occurredAt", "eventId", "updatedAt", "completedAt",
  "telemetryExecutionIds", "telemetryExecutions", "repository"
]);

function strip(value) {
  if (Array.isArray(value)) return value.map(strip);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).filter(([key]) => !VOLATILE.has(key)).map(([key, child]) => [key, strip(child)]));
  }
  return value;
}

test("remote work.start produces the same work state as the local CLI, plus only the remote provenance fields", (t) => {
  const local = fixture(t, "parity-local");
  const viaRemote = fixture(t, "parity-remote");

  const localResult = praxis(local, ["work", "start", "--id", "WI-0100", "--occurred-at", new Date().toISOString()], AGENT_ENV);
  assert.equal(localResult.status, 0, localResult.stderr);

  const { status: exit, response } = remote(viaRemote, request(viaRemote, "work.start", { workItemIds: ["WI-0100"] }));
  assert.equal(exit, 0, JSON.stringify(response.failure));
  assert.equal(response.outcome, "succeeded");

  assert.deepEqual(strip(readJson(viaRemote, ".ros/context/current.json")), strip(readJson(local, ".ros/context/current.json")));
  assert.deepEqual(strip(readEvents(viaRemote)), strip(readEvents(local)));

  const [remoteExecution] = executions(viaRemote);
  const [localExecution] = executions(local);
  const { executor, ...remoteRest } = remoteExecution;
  const { assurance, ...remoteIdentity } = remoteRest.identity;
  assert.equal(assurance, "asserted-by-request");
  assert.equal(executor.assurance, "observed-by-executor");
  assert.equal(executor.kind, "local");
  assert.equal(localExecution.executor, undefined);
  assert.deepEqual(strip({ ...remoteRest, identity: remoteIdentity }), strip(localExecution));
});

test("remote validate returns exactly the local validation document", (t) => {
  const root = fixture(t, "validate");
  const local = praxis(root, ["validate", "--json"]);
  const { response } = remote(root, request(root, "validate", {}));
  assert.equal(response.outcome, local.status === 0 ? "succeeded" : "failed");
  assert.deepEqual(response.result, JSON.parse(local.stdout));
});

test("a retry after a lost result replays the recorded outcome even though its own commit moved the ref", (t) => {
  const root = fixture(t, "replay");
  const body = request(root, "work.start", { workItemIds: ["WI-0100"] });
  const first = remote(root, body).response;
  assert.equal(first.outcome, "succeeded");
  assert.ok(first.persistence.paths.includes(".ros/remote/requests/req-work-start-0001.json"));

  // The adapter persisted the state, then the result was lost in transit.
  git(root, "add", "-A");
  git(root, "commit", "-qm", "praxis: work.start");
  const eventsAfterFirst = readEvents(root).length;

  const retry = remote(root, body);
  assert.equal(retry.status, 0);
  assert.equal(retry.response.replayed, true);
  assert.deepEqual({ ...retry.response, replayed: false }, first);
  assert.equal(status(root), "", "a replay writes nothing");
  assert.equal(readEvents(root).length, eventsAfterFirst);
  assert.equal(executions(root).length, 1, "no duplicate execution");

  // The same holds when the caller refreshed its expected SHA first.
  const refreshed = remote(root, { ...body, repository: { ...body.repository, expectedSha: git(root, "rev-parse", "HEAD") } });
  assert.equal(refreshed.response.replayed, true);
});

test("request.status recovers whether a request was recorded without guessing", (t) => {
  const root = fixture(t, "status");
  const before = remote(root, request(root, "request.status", { requestId: "req-work-start-0001" })).response;
  assert.equal(before.result.recorded, false);

  remote(root, request(root, "work.start", { workItemIds: ["WI-0100"] }));
  const after = remote(root, request(root, "request.status", { requestId: "req-work-start-0001" })).response;
  assert.equal(after.result.recorded, true);
  assert.equal(after.result.response.outcome, "succeeded");
});

test("the same request ID with a different payload fails closed and writes nothing", (t) => {
  const root = fixture(t, "conflict");
  remote(root, request(root, "work.start", { workItemIds: ["WI-0100"] }));
  git(root, "add", "-A");
  git(root, "commit", "-qm", "praxis: work.start");

  const conflicting = remote(root, request(root, "work.start", { workItemIds: ["WI-0200"] }));
  assert.equal(conflicting.status, 1);
  assert.equal(conflicting.response.failure.code, "idempotency-conflict");
  assert.equal(conflicting.response.failure.retry, "never");
  assert.equal(status(root), "");
});

test("a stale request, and a dirty working tree, are refused rather than applied to another state", (t) => {
  const root = fixture(t, "stale");
  const body = request(root, "work.start", { workItemIds: ["WI-0100"] });
  fs.writeFileSync(path.join(root, "notes.txt"), "moved on\n");
  git(root, "add", "-A");
  git(root, "commit", "-qm", "someone else moved the branch");

  const stale = remote(root, body).response;
  assert.equal(stale.failure.code, "stale-ref");
  assert.equal(stale.failure.retry, "after-refresh");
  assert.equal(status(root), "");

  fs.writeFileSync(path.join(root, "notes.txt"), "uncommitted\n");
  const dirty = remote(root, request(root, "work.start", { workItemIds: ["WI-0100"] })).response;
  assert.equal(dirty.failure.code, "stale-ref");
  assert.equal(status(root), "M notes.txt", "only the pre-existing change remains");
});

test("remote mutation is opt-in per repository and read never implies write", (t) => {
  const optedOut = fixture(t, "opt-out", { remoteCapabilities: null });
  const refused = remote(optedOut, request(optedOut, "work.start", { workItemIds: ["WI-0100"] }));
  assert.equal(refused.response.failure.code, "unauthorized");
  assert.equal(status(optedOut), "");
  assert.equal(remote(optedOut, request(optedOut, "validate", {})).response.failure?.code ?? null, null);

  const optedIn = fixture(t, "read-grant");
  const readOnly = remote(optedIn, request(optedIn, "work.start", { workItemIds: ["WI-0100"] }), { grants: ["read"] });
  assert.equal(readOnly.response.failure.code, "unauthorized");
  const noComplete = remote(optedIn, request(optedIn, "work.complete", { workItemIds: ["WI-0100"] }), { grants: ["read", "mutate"] });
  assert.equal(noComplete.response.failure.code, "unauthorized");
});

test("hostile request values are refused before any command runs", (t) => {
  const root = fixture(t, "hostile");
  const attempts = [
    request(root, "work.start", { workItemIds: ["--root=/tmp"] }),
    request(root, "work.start", { workItemIds: ["WI-0100"], type: "--help" }),
    request(root, "work.block", { workItemIds: ["WI-0100"], reason: "-x" }),
    { ...request(root, "work.start", { workItemIds: ["WI-0100"] }), command: "rm -rf /" },
    { ...request(root, "work.start", { workItemIds: ["WI-0100"] }), operation: "shell" }
  ];
  for (const body of attempts) {
    const { status: exit, response } = remote(root, body);
    assert.equal(exit, 1);
    assert.equal(response.outcome, "rejected");
    assert.ok(["invalid-request", "unsupported-operation"].includes(response.failure.code), response.failure.code);
  }
  assert.equal(status(root), "");
  assert.equal(remote(root, "{not json").response.failure.code, "invalid-request");
});

test("credentials in the executor's environment never reach recorded state", (t) => {
  const root = fixture(t, "secrets");
  const { response } = remote(root, request(root, "work.start", { workItemIds: ["WI-0100"] }), {
    env: { GITHUB_TOKEN: PLANTED_TOKEN, ANTHROPIC_API_KEY: PLANTED_TOKEN, ROS_ACTOR: "a-previous-agent" }
  });
  assert.equal(response.outcome, "succeeded");
  for (const relative of response.persistence.paths) {
    const text = fs.readFileSync(path.join(root, relative), "utf8");
    assert.ok(!text.includes(PLANTED_TOKEN), `${relative} leaked a credential`);
    assert.ok(!text.includes("a-previous-agent"), `${relative} inherited a previous actor`);
  }
  const secretInRequest = remote(root, request(root, "work.block", { workItemIds: ["WI-0100"], reason: `use ${PLANTED_TOKEN}` }, { requestId: "req-secret-0001" }));
  assert.equal(secretInRequest.response.failure.code, "secret-detected");
  assert.ok(!secretInRequest.stdout.includes(PLANTED_TOKEN));
});

test("a mutation that cannot finish leaves nothing behind and claims nothing", (t) => {
  const root = fixture(t, "timeout");
  const { response } = remote(root, request(root, "work.start", { workItemIds: ["WI-0100"] }), { extraArgs: ["--timeout-seconds", "0"] });
  assert.equal(response.failure.code, "timeout");
  assert.equal(response.failure.retry, "same-request");
  assert.notEqual(response.outcome, "succeeded");
  assert.deepEqual(response.persistence.paths, []);
  assert.equal(status(root), "", "anything the interrupted command wrote was undone");
  assert.equal(fs.existsSync(path.join(root, ".ros", "remote", "requests", "req-work-start-0001.json")), false);
});

test("a domain refusal is reported as the command's own refusal and writes nothing", (t) => {
  const root = fixture(t, "domain");
  remote(root, request(root, "work.start", { workItemIds: ["WI-0100"] }));
  git(root, "add", "-A");
  git(root, "commit", "-qm", "praxis: work.start");

  const again = remote(root, request(root, "work.start", { workItemIds: ["WI-0100"] }, { requestId: "req-start-again-0001" }));
  assert.equal(again.response.failure.code, "domain-rejected");
  assert.equal(again.response.failure.decidedBy, "praxis");
  assert.match(again.response.failure.message, /cannot begin 'WI-0100' from 'active'/);
  assert.equal(status(root), "");
});

test("an unidentified requester is recorded as unknown, never as the executor", (t) => {
  const root = fixture(t, "unknown-actor");
  const { actor, ...anonymous } = request(root, "work.start", { workItemIds: ["WI-0100"] });
  const { response } = remote(root, anonymous, { env: { GITHUB_ACTIONS: "true", GITHUB_RUN_ID: "4242", GITHUB_RUN_ATTEMPT: "1", GITHUB_ACTOR: "octocat", GITHUB_TRIGGERING_ACTOR: "octocat" } });
  assert.equal(response.outcome, "succeeded");
  assert.equal(response.executor.kind, "github-actions");
  assert.equal(response.executor.runId, "4242");
  assert.equal(response.executor.principal, "github:octocat");

  const [execution] = executions(root);
  assert.equal(execution.identity.actorKind, "unknown");
  assert.equal(execution.identity.provider, "unknown");
  assert.equal(execution.identity.runId, null);
  assert.equal(execution.executor.runId, "4242");
  const started = readEvents(root).at(-1);
  assert.equal(started.actor.kind, "unknown");
});
