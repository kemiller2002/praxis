// praxis.remote 1.3 durable checkpoints and continuation (PRAXIS-CONT-07,
// DF-ROS-2026-A042): a cloud agent with no local Praxis runtime discovers the
// capability, checkpoints its own pushed work, and a second agent continues
// it, all through typed requests executed by the same command
// implementation as the local CLI, bound by expectedSha and journalled by
// requestId. The GitHub adapter's persistence step (commit exactly the
// reported Praxis state, push without force) is emulated between requests.
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

const AGENT_ONE = { kind: "agent", id: "example/cloud-agent-one", provider: "example", runtime: "cloud-agent", sessionId: "cloud-session-1" };
const AGENT_TWO = { kind: "agent", id: "other/cloud-agent-two", provider: "other", runtime: "cloud-agent", sessionId: "cloud-session-2" };

function git(root, ...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8", env: { ...process.env, GIT_TERMINAL_PROMPT: "0" } }).trim();
}

function praxis(root, args) {
  const result = spawnSync("dotnet", [cli, "--root", root, ...args], { cwd: root, encoding: "utf8", env: deterministicIdentityEnv() });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

// An executor checkout (what actions/checkout gives the runner) with a bare
// "GitHub" remote, and a separate clone standing in for the cloud agent's
// only way to change code: pushing commits through the Git host.
function fixture(t) {
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), "praxis-remote-checkpoint-"));
  t.after(() => fs.rmSync(parent, { recursive: true, force: true }));
  const bare = path.join(parent, "github.git");
  const executor = path.join(parent, "runner");
  fs.mkdirSync(bare);
  git(bare, "init", "-q", "--bare", "-b", "main");
  fs.mkdirSync(executor);
  initializeProject({ target: executor, project: "Remote Checkpoint" });
  const config = JSON.parse(fs.readFileSync(path.join(executor, "ros.json"), "utf8"));
  config.remote = { capabilities: ["read", "mutate", "complete"] };
  config.workProtocol.continuity = { requireDurableCheckpoint: true };
  fs.writeFileSync(path.join(executor, "ros.json"), `${JSON.stringify(config, null, 2)}\n`);
  git(executor, "init", "-q", "-b", "main");
  git(executor, "config", "user.email", "runner@example.invalid");
  git(executor, "config", "user.name", "Runner");
  assert.equal(praxis(executor, ["add", "Remote item", "--id", "WI-0100"]).status, 0);
  assert.equal(praxis(executor, ["work", "backlog-transition", "--action", "ready", "--id", "WI-0100", "--occurred-at", new Date().toISOString()]).status, 0);
  git(executor, "add", "-A");
  git(executor, "commit", "-qm", "baseline");
  git(executor, "remote", "add", "origin", bare);
  git(executor, "push", "-q", "-u", "origin", "main");
  const agentClone = path.join(parent, "agent-clone");
  git(parent, "clone", "-q", bare, agentClone);
  git(agentClone, "config", "user.email", "agent@example.invalid");
  git(agentClone, "config", "user.name", "Cloud Agent");
  return { executor, agentClone, bare };
}

function request(executor, operation, args, { actor, requestId, execution, protocolVersion = "1.3", expectedSha } = {}) {
  return {
    protocol: "praxis.remote",
    protocolVersion,
    requestId,
    operation,
    repository: { ref: "refs/heads/main", expectedSha: expectedSha ?? git(executor, "rev-parse", "HEAD") },
    actor,
    ...(execution ? { execution: { id: execution } } : {}),
    arguments: args
  };
}

function remote(executor, body) {
  const file = path.join(os.tmpdir(), `praxis-remote-checkpoint-${process.pid}-${Math.random().toString(16).slice(2)}.json`);
  fs.writeFileSync(file, JSON.stringify(body));
  try {
    const result = praxis(executor, ["remote", "execute", "--request", file, "--grant", "read", "--grant", "mutate", "--grant", "complete"]);
    return { ...result, response: JSON.parse(result.stdout) };
  } finally {
    fs.rmSync(file, { force: true });
  }
}

// The adapter: commit exactly the reported Praxis-owned paths, push without force.
function persist(executor, response) {
  const paths = response.persistence?.paths ?? [];
  assert.ok(paths.length > 0, "a succeeded mutation reports what to persist");
  for (const relative of paths) assert.ok(relative.startsWith(".ros/"), `adapter would refuse ${relative}`);
  git(executor, "add", "--", ...paths.filter((relative) => fs.existsSync(path.join(executor, relative))));
  git(executor, "commit", "-qm", `praxis: remote ${response.operation} (${response.requestId})`);
  git(executor, "push", "-q", "origin", "HEAD:main");
}

// The cloud agent changes code only through the Git host.
function agentPushes(agentClone, executor, file, content) {
  git(agentClone, "pull", "-q", "--ff-only");
  fs.mkdirSync(path.dirname(path.join(agentClone, file)), { recursive: true });
  fs.writeFileSync(path.join(agentClone, file), content);
  git(agentClone, "add", "-A");
  git(agentClone, "commit", "-qm", `agent: ${file}`);
  git(agentClone, "push", "-q", "origin", "HEAD:main");
  // The next run checks out the new head.
  git(executor, "pull", "-q", "--ff-only");
  return git(executor, "rev-parse", "HEAD");
}

function succeeded(result) {
  assert.equal(result.response.outcome, "succeeded", JSON.stringify(result.response.failure ?? result.response, null, 2));
  return result.response;
}

test("describe publishes work.checkpoint and work.continue as protocol 1.3 mutations", (t) => {
  const { executor } = fixture(t);
  const described = succeeded(remote(executor, request(executor, "praxis.describe", {}, { requestId: "req-describe-0001", protocolVersion: "1.0" }))).result;
  assert.deepEqual(described.protocolVersions, ["1.0", "1.1", "1.2", "1.3"]);
  const byName = Object.fromEntries(described.operations.map((operation) => [operation.operation, operation]));
  assert.deepEqual(byName["work.checkpoint"].requiredArguments, ["workItemId", "summary", "nextAction"]);
  assert.deepEqual(byName["work.checkpoint"].optionalArguments, ["stepId"]);
  assert.equal(byName["work.checkpoint"].capability, "mutate");
  assert.equal(byName["work.checkpoint"].requiresExecution, true);
  assert.equal(byName["work.checkpoint"].introducedIn, "1.3");
  assert.equal(byName["work.continue"].requiresExecution, false);
  assert.deepEqual(byName["work.block"].optionalArguments, ["unrecoverableReason"]);
});

test("a 1.2 request cannot use work.checkpoint, and a checkpoint must name the requester's own execution", (t) => {
  const { executor } = fixture(t);
  const old = remote(executor, request(executor, "work.checkpoint", { workItemId: "WI-0100", summary: "s", nextAction: "n" }, { requestId: "req-old-checkpoint-1", actor: AGENT_ONE, protocolVersion: "1.2" }));
  assert.equal(old.response.failure.code, "unsupported-operation");
  const unnamed = remote(executor, request(executor, "work.checkpoint", { workItemId: "WI-0100", summary: "s", nextAction: "n" }, { requestId: "req-unnamed-checkpoint-1", actor: AGENT_ONE }));
  assert.equal(unnamed.response.failure.code, "invalid-request");
  assert.ok(unnamed.response.failure.problems.some((problem) => problem.field === "execution.id"));
});

test("cloud agents without a runtime checkpoint, hand off, continue and complete through praxis.remote", (t) => {
  const { executor, agentClone, bare } = fixture(t);

  // Agent one starts the work under its own execution.
  const started = succeeded(remote(executor, request(executor, "work.start", { workItemIds: ["WI-0100"], type: "feature" }, { requestId: "req-start-0001", actor: AGENT_ONE })));
  persist(executor, started);
  const context = JSON.parse(fs.readFileSync(path.join(executor, ".ros", "context", "current.json"), "utf8"));
  const [executionOne] = context.workItems.find((item) => item.id === "WI-0100").telemetryExecutionIds;

  // It pushes code through the host, then asks Praxis to verify and record it.
  const codeSha = agentPushes(agentClone, executor, "src/feature.txt", "part one\n");
  const checkpointRequest = request(
    executor,
    "work.checkpoint",
    { workItemId: "WI-0100", summary: "Part one implemented", nextAction: "Implement part two" },
    { requestId: "req-checkpoint-0001", actor: AGENT_ONE, execution: executionOne }
  );
  const checkpointed = succeeded(remote(executor, checkpointRequest));
  assert.equal(checkpointed.result.status, "recorded");
  assert.equal(checkpointed.result.checkpoint.commit, codeSha);
  assert.equal(checkpointed.result.checkpoint.remoteCommit, codeSha);
  assert.equal(checkpointed.result.checkpoint.executionId, executionOne);
  persist(executor, checkpointed);

  // Retrying the same intent replays; nothing is recorded twice.
  const replay = remote(executor, { ...checkpointRequest, repository: { ...checkpointRequest.repository, expectedSha: git(executor, "rev-parse", "HEAD") } });
  assert.equal(replay.response.replayed, true);
  const checkpointEvents = () =>
    fs.readFileSync(path.join(executor, ".ros", "events", "events.jsonl"), "utf8").split("\n").filter((line) => line.includes("\"work.checkpointed\"")).length;
  assert.equal(checkpointEvents(), 1);

  // A request formed against an old commit is refused, never applied elsewhere.
  const stale = remote(
    executor,
    request(executor, "work.checkpoint", { workItemId: "WI-0100", summary: "again", nextAction: "n" }, { requestId: "req-checkpoint-stale-1", actor: AGENT_ONE, execution: executionOne, expectedSha: codeSha })
  );
  assert.equal(stale.response.failure.code, "stale-ref");
  assert.equal(stale.response.failure.retry, "after-refresh");

  // Agent one disappears. Agent two reads the durable state remotely.
  const read = succeeded(remote(executor, request(executor, "work.context", { workItemId: "WI-0100" }, { requestId: "req-context-0001", actor: AGENT_TWO })));
  const continuity = read.result.continuity[0];
  assert.equal(continuity.checkpoint.commit, codeSha);
  assert.equal(continuity.checkpoint.nextAction, "Implement part two");
  assert.equal(continuity.freshness, "current");

  // Agent two continues under a new execution of its own.
  const continued = succeeded(remote(executor, request(executor, "work.continue", { workItemId: "WI-0100" }, { requestId: "req-continue-0001", actor: AGENT_TWO })));
  const executionTwo = continued.result.executionId;
  assert.notEqual(executionTwo, executionOne);
  assert.equal(continued.result.predecessor.executionId, executionOne);
  assert.equal(continued.result.predecessor.disposition, "interrupted");
  persist(executor, continued);

  // Agent two finishes the work and records the final checkpoint.
  const finalSha = agentPushes(agentClone, executor, "src/feature.txt", "part one\npart two\n");
  const finalCheckpoint = succeeded(
    remote(
      executor,
      request(executor, "work.checkpoint", { workItemId: "WI-0100", summary: "Part two implemented", nextAction: "Run final completion transition" }, { requestId: "req-checkpoint-0002", actor: AGENT_TWO, execution: executionTwo })
    )
  );
  assert.equal(finalCheckpoint.result.checkpoint.commit, finalSha);
  assert.equal(finalCheckpoint.result.checkpoint.executionId, executionTwo);
  persist(executor, finalCheckpoint);

  const completed = succeeded(
    remote(
      executor,
      request(executor, "work.complete", { workItemIds: ["WI-0100"], evidence: [{ type: "implementation", path: "src/feature.txt" }, { type: "tests", path: "README.md" }] }, { requestId: "req-complete-0001", actor: AGENT_TWO })
    )
  );
  persist(executor, completed);

  const finalContext = JSON.parse(fs.readFileSync(path.join(executor, ".ros", "context", "current.json"), "utf8"));
  const item = finalContext.workItems.find((candidate) => candidate.id === "WI-0100");
  assert.equal(item.semanticState, "complete");
  assert.equal(item.latestCheckpoint.commit, finalSha);
  // The final checkpoint is what the remote branch carries (Praxis state commits aside).
  git(bare, "merge-base", "--is-ancestor", finalSha, "main");
  assert.equal(praxis(executor, ["validate"]).status, 0);
});

test("completion may not finalize another agent's active execution, named or not; the successor continues first (GH-113)", (t) => {
  const { executor, agentClone } = fixture(t);
  const started = succeeded(remote(executor, request(executor, "work.start", { workItemIds: ["WI-0100"], type: "feature" }, { requestId: "req-start-0001", actor: AGENT_ONE })));
  persist(executor, started);
  const context = JSON.parse(fs.readFileSync(path.join(executor, ".ros", "context", "current.json"), "utf8"));
  const [executionOne] = context.workItems.find((item) => item.id === "WI-0100").telemetryExecutionIds;
  agentPushes(agentClone, executor, "src/feature.txt", "done\n");
  persist(
    executor,
    succeeded(remote(executor, request(executor, "work.checkpoint", { workItemId: "WI-0100", summary: "Done", nextAction: "Complete" }, { requestId: "req-checkpoint-0001", actor: AGENT_ONE, execution: executionOne })))
  );
  const evidence = [{ type: "implementation", path: "src/feature.txt" }, { type: "tests", path: "README.md" }];

  // Leaving the execution out does not let agent two finalize agent one's execution.
  const unnamed = remote(executor, request(executor, "work.complete", { workItemIds: ["WI-0100"], evidence }, { requestId: "req-complete-unnamed-1", actor: AGENT_TWO })).response;
  assert.equal(unnamed.outcome, "rejected");
  assert.equal(unnamed.failure.code, "domain-rejected");
  assert.equal(unnamed.failure.retry, "never");
  assert.match(unnamed.failure.message, new RegExp(`active execution '${executionOne}', which belongs to another actor or run; take the work over with work.continue`));
  assert.deepEqual(unnamed.failure.problems.map((problem) => problem.field), ["arguments.workItemIds"]);
  // Naming it is refused as before.
  const named = remote(executor, request(executor, "work.complete", { workItemIds: ["WI-0100"], evidence }, { requestId: "req-complete-named-1", actor: AGENT_TWO, execution: executionOne })).response;
  assert.equal(named.failure.code, "domain-rejected");
  assert.match(named.failure.message, /belongs to another actor or run; continue in your own execution/);
  assert.equal(git(executor, "status", "--porcelain"), "", "a refused completion persists nothing");
  const unchanged = JSON.parse(fs.readFileSync(path.join(executor, ".ros", "context", "current.json"), "utf8"));
  assert.equal(unchanged.workItems.find((item) => item.id === "WI-0100").semanticState, "active");

  // The same agent in another session is another run, and is refused too.
  const otherRun = remote(
    executor,
    request(executor, "work.complete", { workItemIds: ["WI-0100"], evidence }, { requestId: "req-complete-rerun-1", actor: { ...AGENT_ONE, sessionId: "cloud-session-9" } })
  ).response;
  assert.equal(otherRun.failure.code, "domain-rejected");

  // Taking the work over first makes the completion agent two's own.
  const continued = succeeded(remote(executor, request(executor, "work.continue", { workItemId: "WI-0100" }, { requestId: "req-continue-0001", actor: AGENT_TWO })));
  const executionTwo = continued.result.executionId;
  assert.equal(continued.result.predecessor.executionId, executionOne);
  persist(executor, continued);
  persist(
    executor,
    succeeded(remote(executor, request(executor, "work.checkpoint", { workItemId: "WI-0100", summary: "Taken over", nextAction: "Complete" }, { requestId: "req-checkpoint-0002", actor: AGENT_TWO, execution: executionTwo })))
  );
  // The handed-over predecessor no longer blocks completion, but the successor's live execution does, for anyone else.
  const agentThree = { kind: "agent", id: "third/cloud-agent-three", provider: "third", runtime: "cloud-agent", sessionId: "cloud-session-3" };
  const intruder = remote(executor, request(executor, "work.complete", { workItemIds: ["WI-0100"], evidence }, { requestId: "req-complete-third-1", actor: agentThree })).response;
  assert.equal(intruder.failure.code, "domain-rejected");
  assert.match(intruder.failure.message, new RegExp(`active execution '${executionTwo}'`));
  assert.equal(git(executor, "status", "--porcelain"), "");
  const completed = succeeded(remote(executor, request(executor, "work.complete", { workItemIds: ["WI-0100"], evidence }, { requestId: "req-complete-0001", actor: AGENT_TWO })));
  persist(executor, completed);
  const item = JSON.parse(fs.readFileSync(path.join(executor, ".ros", "context", "current.json"), "utf8")).workItems.find((candidate) => candidate.id === "WI-0100");
  assert.equal(item.semanticState, "complete");
  assert.equal(praxis(executor, ["validate"]).status, 0);
});

test("the owner of the only active execution still completes without naming it", (t) => {
  const { executor, agentClone } = fixture(t);
  const started = succeeded(remote(executor, request(executor, "work.start", { workItemIds: ["WI-0100"], type: "feature" }, { requestId: "req-start-0001", actor: AGENT_ONE })));
  persist(executor, started);
  const context = JSON.parse(fs.readFileSync(path.join(executor, ".ros", "context", "current.json"), "utf8"));
  const [executionOne] = context.workItems.find((item) => item.id === "WI-0100").telemetryExecutionIds;
  agentPushes(agentClone, executor, "src/feature.txt", "done\n");
  persist(
    executor,
    succeeded(remote(executor, request(executor, "work.checkpoint", { workItemId: "WI-0100", summary: "Done", nextAction: "Complete" }, { requestId: "req-checkpoint-0001", actor: AGENT_ONE, execution: executionOne })))
  );
  const completed = succeeded(
    remote(executor, request(executor, "work.complete", { workItemIds: ["WI-0100"], evidence: [{ type: "implementation", path: "src/feature.txt" }, { type: "tests", path: "README.md" }] }, { requestId: "req-complete-0001", actor: AGENT_ONE }))
  );
  persist(executor, completed);
  const record = JSON.parse(fs.readFileSync(path.join(executor, ".ros", "telemetry", "executions", `${executionOne}.json`), "utf8"));
  assert.equal(record.status, "finalized");
});
