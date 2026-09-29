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

test("praxis.describe tells an agent what it may do here, from Praxis's own catalog and state", (t) => {
  const root = fixture(t, "describe", { remoteCapabilities: ["read", "mutate"] });
  const { response } = remote(root, request(root, "praxis.describe", {}), { grants: ["read", "mutate", "complete"] });
  assert.equal(response.outcome, "succeeded");
  const described = response.result;
  assert.equal(described.schema, "praxis.describe");
  assert.equal(described.available, true);
  assert.deepEqual(described.protocolVersions, ["1.0", "1.1", "1.2"]);
  assert.equal(described.contract, "docs/remote-agent-contract.md");
  assert.deepEqual(described.repository.capabilities, ["read", "mutate"]);
  assert.deepEqual(described.grants, ["read", "mutate"], "the transport grant is narrowed by the repository");
  assert.equal(described.repository.sha, git(root, "rev-parse", "HEAD"));

  const start = described.operations.find((operation) => operation.operation === "work.start");
  assert.deepEqual(start, {
    operation: "work.start",
    capability: "mutate",
    mutating: true,
    requiresExpectedSha: true,
    requiredArguments: ["workItemIds"],
    optionalArguments: ["type", "classifications"]
  });
  assert.ok(described.readyWork.some((item) => item.id === "WI-0100"), "ready work is discoverable");
  assert.deepEqual(described.transports, [], "a repository without the adapter says so");

  const local = praxis(root, ["remote", "describe"]);
  assert.equal(local.status, 0);
  assert.deepEqual(JSON.parse(local.stdout).operations, described.operations, "local and remote discovery agree");
});

test("AGENTS.md routes agents without a runtime to the contract without embedding scripts", () => {
  const agents = fs.readFileSync(path.join(repositoryRoot, "AGENTS.md"), "utf8");
  const section = agents.split("## No local runtime? Use remote execution")[1]?.split("\n## ")[0];
  assert.ok(section, "AGENTS.md has the remote-execution routing section");
  assert.match(section, /docs\/remote-agent-contract\.md/);
  assert.doesNotMatch(section, /```|gh workflow run|curl /, "no scripts in AGENTS.md");
  for (const document of ["docs/remote-agent-contract.md", "docs/remote-protocol.md"]) {
    assert.ok(fs.existsSync(path.join(repositoryRoot, document)), document);
    const manifest = JSON.parse(fs.readFileSync(path.join(repositoryRoot, "starter", "greenfield", "manifest.json"), "utf8"));
    assert.ok(JSON.stringify(manifest).includes(`"${document}"`), `${document} ships with the scaffold`);
  }
});

test("a successor agent continues in its own execution, linked to its predecessor, never impersonating it", (t) => {
  const root = fixture(t, "successor");
  const commit = (message) => { git(root, "add", "-A"); git(root, "commit", "-qm", message); };
  const agentA = { kind: "agent", id: "example/agent-a", provider: "example", runtime: "cloud-a", sessionId: "session-a" };
  const agentB = { kind: "agent", id: "other/agent-b", provider: "other", runtime: "cloud-b", sessionId: "session-b" };

  assert.equal(remote(root, request(root, "work.start", { workItemIds: ["WI-0100"] }, { requestId: "req-a-start-001", actor: agentA })).response.outcome, "succeeded");
  commit("praxis: A starts");
  assert.equal(remote(root, request(root, "work.block", { workItemIds: ["WI-0100"], reason: "handoff to another agent" }, { requestId: "req-a-block-001", actor: agentA })).response.outcome, "succeeded");
  commit("praxis: A blocks");
  const [predecessor] = executions(root);

  const resumed = remote(root, request(root, "work.resume", { workItemIds: ["WI-0100"] }, { requestId: "req-b-resume-01", actor: agentB })).response;
  assert.equal(resumed.outcome, "succeeded");
  commit("praxis: B resumes");

  const all = executions(root);
  assert.equal(all.length, 2, "the successor has its own execution");
  const successor = all.find((execution) => execution.executionId !== predecessor.executionId);
  assert.equal(successor.identity.agentId, "other/agent-b");
  assert.equal(successor.identity.parentExecutionId, predecessor.executionId, "continuation names its predecessor");
  const predecessorNow = all.find((execution) => execution.executionId === predecessor.executionId);
  assert.equal(predecessorNow.identity.agentId, "example/agent-a", "the predecessor's identity is untouched");
  assert.equal(readEvents(root).at(-1).actor.id, "other/agent-b");

  // B may not record into A's execution, remotely or by naming it.
  const intrusion = remote(root, request(root, "telemetry.record", { metric: "tokens.input", value: 10 }, {
    requestId: "req-b-telemetry-1", actor: agentB, execution: { id: predecessor.executionId }
  })).response;
  assert.equal(intrusion.failure.code, "domain-rejected");
  assert.match(intrusion.failure.message, /belongs to another actor or run/);
  assert.equal(status(root), "");

  const own = remote(root, request(root, "telemetry.record", { metric: "tokens.input", value: 10, unit: "tokens" }, {
    requestId: "req-b-telemetry-2", actor: agentB, execution: { id: successor.executionId }
  })).response;
  assert.equal(own.outcome, "succeeded", JSON.stringify(own.failure));
  const recorded = executions(root).find((execution) => execution.executionId === successor.executionId);
  const measurement = recorded.metrics.find((metric) => metric.id === "tokens.input");
  assert.equal(measurement.value, 10);
  assert.equal(measurement.source.type, "agent-report", "remotely supplied telemetry is labelled as reported, not observed");
});

test("a successor takes over from a predecessor that left without blocking: resume is refused until the successor blocks for the handoff", (t) => {
  const root = fixture(t, "takeover");
  const commit = (message) => { git(root, "add", "-A"); git(root, "commit", "-qm", message); };
  const agentA = { kind: "agent", id: "example/agent-a", provider: "example", runtime: "cloud-a", sessionId: "session-a" };
  const agentB = { kind: "agent", id: "example/agent-a", provider: "example", runtime: "cloud-a", sessionId: "session-b" };

  assert.equal(remote(root, request(root, "work.start", { workItemIds: ["WI-0100"] }, { requestId: "req-a-start-001", actor: agentA })).response.outcome, "succeeded");
  commit("praxis: A starts, then its session ends");
  const [predecessor] = executions(root);

  const refused = remote(root, request(root, "work.resume", { workItemIds: ["WI-0100"] }, { requestId: "req-b-resume-01", actor: agentB })).response;
  assert.equal(refused.failure.code, "domain-rejected", "resume is legal only from blocked");
  assert.equal(status(root), "", "a refused transition persists nothing");

  const blocked = remote(root, request(root, "work.block", { workItemIds: ["WI-0100"], reason: "predecessor session-a ended without a handoff; taking over" }, { requestId: "req-b-block-01", actor: agentB })).response;
  assert.equal(blocked.outcome, "succeeded", JSON.stringify(blocked.failure));
  commit("praxis: B blocks for the handoff");
  assert.equal(readEvents(root).at(-1).actor.id, agentB.id);
  assert.equal(readEvents(root).at(-1).type, "work.blocked");

  const resumed = remote(root, request(root, "work.resume", { workItemIds: ["WI-0100"] }, { requestId: "req-b-resume-02", actor: agentB })).response;
  assert.equal(resumed.outcome, "succeeded", JSON.stringify(resumed.failure));
  commit("praxis: B resumes");

  // Same agent ID in another session is still another run: its own execution, linked, never the predecessor's.
  const successor = executions(root).find((execution) => execution.executionId !== predecessor.executionId);
  assert.ok(successor, "the successor has its own execution");
  assert.equal(successor.identity.sessionId, "session-b");
  assert.equal(successor.identity.parentExecutionId, predecessor.executionId);
  assert.equal(executions(root).find((execution) => execution.executionId === predecessor.executionId).identity.sessionId, "session-a");
});

test("remote reconciliation (#80) attributes already-committed work without touching it and keeps three identities apart", (t) => {
  const root = fixture(t, "reconcile");
  fs.mkdirSync(path.join(root, "src"), { recursive: true });
  fs.writeFileSync(path.join(root, "src", "feature.txt"), "committed before any work item was active\n");
  git(root, "add", "src/feature.txt");
  execFileSync("git", ["-c", "user.name=Original Author", "-c", "user.email=author@example.invalid", "commit", "-qm", "feature"], { cwd: root });
  const featureCommit = git(root, "rev-parse", "HEAD");
  const before = fs.readFileSync(path.join(root, "src", "feature.txt"), "utf8");

  const { response } = remote(root, request(root, "work.reconcile", {
    workItemId: "WI-0100", reason: "committed before the work item was begun", commits: [featureCommit]
  }));
  assert.equal(response.outcome, "succeeded", JSON.stringify(response.failure));
  assert.equal(fs.readFileSync(path.join(root, "src", "feature.txt"), "utf8"), before, "the reconciled file is not touched");
  assert.ok(response.persistence.paths.every((relative) => relative.startsWith(".ros/")));

  const event = readEvents(root).find((candidate) => candidate.type === "work.attribution.reconciled");
  assert.equal(event.workItem, "WI-0100");
  assert.equal(event.attribution, "post-hoc");
  assert.equal(event.actor.id, AGENT.id, "the reconciliation actor is the requester");
  assert.equal(event.gitEvidence.commits[0].author.name, "Original Author", "the change author is preserved");
  assert.ok(event.paths.includes("src/feature.txt"));

  git(root, "add", "-A");
  git(root, "commit", "-qm", "praxis: reconcile");
  const duplicate = remote(root, request(root, "work.reconcile", {
    workItemId: "WI-0100", reason: "committed before the work item was begun", commits: [featureCommit]
  }, { requestId: "req-reconcile-again" })).response;
  assert.equal(duplicate.outcome, "succeeded", "a duplicate reconciliation is an idempotent no-op");
  assert.equal(readEvents(root).filter((candidate) => candidate.type === "work.attribution.reconciled").length, 1);
});

test("an agent records steps and step-scoped usage remotely; usage keeps its evidence quality", (t) => {
  const root = fixture(t, "steps");
  const commit = (message) => { git(root, "add", "-A"); git(root, "commit", "-qm", message); };
  const v11 = (operation, args, extra) => ({ ...request(root, operation, args, extra), protocolVersion: "1.1" });
  const agent = { ...AGENT, sessionId: "agent-session" };

  const started = remote(root, v11("work.start", { workItemIds: ["WI-0100"] }, { requestId: "req-steps-start-1", actor: agent })).response;
  assert.equal(started.outcome, "succeeded");
  commit("start");
  const executionId = started.result.workItems.find((item) => item.id === "WI-0100").telemetryExecutionIds[0];
  const own = { actor: agent, execution: { id: executionId } };

  assert.equal(remote(root, v11("step.start", { stepId: "implement", name: "Implement parser" }, { requestId: "req-step-start-1", ...own })).response.outcome, "succeeded");
  commit("step");
  const usage = remote(root, v11("telemetry.record", { metric: "tokens.input", value: 1200, unit: "tokens", step: "implement", sourceType: "runtime-api" }, { requestId: "req-step-usage-1", ...own })).response;
  assert.equal(usage.outcome, "succeeded", JSON.stringify(usage.failure));
  commit("usage");
  const unknownStep = remote(root, v11("telemetry.record", { metric: "tokens.input", value: 1, step: "never-started" }, { requestId: "req-step-usage-2", ...own })).response;
  assert.equal(unknownStep.failure.code, "domain-rejected");
  assert.equal(remote(root, v11("step.complete", { stepId: "implement" }, { requestId: "req-step-done-01", ...own })).response.outcome, "succeeded");
  commit("step done");

  const [execution] = executions(root);
  assert.deepEqual(execution.events.filter((event) => event.type.startsWith("step.")).map((event) => [event.type, event.stepId]), [
    ["step.started", "implement"],
    ["step.completed", "implement"]
  ]);

  const report = JSON.parse(praxis(root, ["telemetry", "usage", "WI-0100", "--by", "step"]).stdout);
  const group = report.groups.find((candidate) => candidate.key === "implement" && candidate.metric === "tokens.input");
  assert.equal(group.total, 1200);
  assert.deepEqual(group.evidenceQuality, { "provider-reported": 1 });

  const intruder = { kind: "agent", id: "other/agent", provider: "other", runtime: "x" };
  const refused = remote(root, v11("step.start", { stepId: "sneak" }, { requestId: "req-step-sneak-1", actor: intruder, execution: { id: executionId } })).response;
  assert.equal(refused.failure.code, "domain-rejected");
});

test("an ordered batch runs each constituent with its own identity and outcome, and a partial batch is unambiguous", (t) => {
  const root = fixture(t, "batch");
  assert.equal(praxis(root, ["add", "Second item", "--id", "WI-0200"]).status, 0);
  git(root, "add", "-A");
  git(root, "commit", "-qm", "second item captured, not ready");

  const batch = (requestId, requests) => ({ ...request(root, "batch", { requests }, { requestId }), protocolVersion: "1.2" });
  const partial = remote(root, batch("req-batch-0001", [
    { requestId: "req-batch-part-1", operation: "work.start", arguments: { workItemIds: ["WI-0100"] } },
    { requestId: "req-batch-part-2", operation: "work.start", arguments: { workItemIds: ["WI-0200"] } },
    { requestId: "req-batch-part-3", operation: "work.block", arguments: { workItemIds: ["WI-0100"], reason: "never runs" } }
  ]));
  const { response } = partial;
  assert.equal(partial.status, 1);
  assert.equal(response.outcome, "rejected");
  assert.equal(response.failure.code, "domain-rejected");
  assert.match(response.failure.message, /constituent 'req-batch-part-2'/);
  assert.equal(response.result.completed, 1);
  assert.equal(response.result.stoppedAt, "req-batch-part-2");
  assert.deepEqual(response.result.notRun, ["req-batch-part-3"]);
  assert.deepEqual(response.result.responses.map((part) => [part.requestId, part.outcome]), [
    ["req-batch-part-1", "succeeded"],
    ["req-batch-part-2", "rejected"]
  ]);

  // The accepted constituent is kept and reported for persistence; the
  // refused one left nothing behind.
  assert.ok(response.persistence.paths.includes(".ros/remote/requests/req-batch-part-1.json"));
  assert.ok(response.persistence.paths.includes(".ros/remote/requests/req-batch-0001.json"));
  assert.ok(!response.persistence.paths.includes(".ros/remote/requests/req-batch-part-2.json"));
  const context = readJson(root, ".ros/context/current.json");
  assert.equal(context.workItems.find((item) => item.id === "WI-0100").state, "active");
  assert.equal(context.workItems.find((item) => item.id === "WI-0200"), undefined);
  git(root, "add", "-A");
  git(root, "commit", "-qm", "praxis: partial batch");

  // Retrying the same batch replays its recorded outcome.
  const replay = remote(root, batch("req-batch-0001", partial.response.result.responses.length ? [
    { requestId: "req-batch-part-1", operation: "work.start", arguments: { workItemIds: ["WI-0100"] } },
    { requestId: "req-batch-part-2", operation: "work.start", arguments: { workItemIds: ["WI-0200"] } },
    { requestId: "req-batch-part-3", operation: "work.block", arguments: { workItemIds: ["WI-0100"], reason: "never runs" } }
  ] : [])).response;
  assert.equal(replay.replayed, true);
  assert.equal(status(root), "");

  // A new batch may reuse an already-recorded constituent: it replays.
  const next = remote(root, batch("req-batch-0002", [
    { requestId: "req-batch-part-1", operation: "work.start", arguments: { workItemIds: ["WI-0100"] } },
    { requestId: "req-batch-part-4", operation: "work.block", arguments: { workItemIds: ["WI-0100"], reason: "handoff" } }
  ])).response;
  assert.equal(next.outcome, "succeeded", JSON.stringify(next.failure));
  assert.equal(next.result.responses[0].replayed, true);
  assert.equal(next.result.responses[1].outcome, "succeeded");
  assert.equal(readJson(root, ".ros/context/current.json").workItems.find((item) => item.id === "WI-0100").state, "blocked");
  assert.equal(readEvents(root).filter((event) => event.workItem === "WI-0100" && event.type === "work.started").length, 1, "the replayed start did not run twice");
});

test("the request journal is Praxis bookkeeping and never an unattributed change", (t) => {
  const root = fixture(t, "journal-ignored");
  const config = JSON.parse(fs.readFileSync(path.join(root, "ros.json"), "utf8"));
  assert.ok(config.workProtocol.ignoredPaths.includes(".ros/remote/**"), "the scaffold ignores the journal for attribution");

  // A journal entry committed by an adapter, with no work item active.
  fs.mkdirSync(path.join(root, ".ros", "remote", "requests"), { recursive: true });
  fs.writeFileSync(path.join(root, ".ros", "remote", "requests", "req-landed-0001.json"), "{}\n");
  const result = praxis(root, ["validate", "--json"]);
  const findings = JSON.parse(result.stdout).findings.filter((finding) => String(finding.path).startsWith(".ros/remote"));
  assert.deepEqual(findings, [], JSON.stringify(findings));
});
