import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

// End-to-end coverage of agent identity and provenance (DF-ROS-2026-A036,
// RQ-ROS-2026-A001..A008) through the real F# CLI in a bootstrapped
// repository: identity established once per execution, inherited by every
// record, accumulated across agents and humans, validated, and preserved
// across export boundaries.

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// Every identity-bearing variable is cleared so each test declares exactly
// the environment it means (the suite itself may run inside an agent or CI).
const BASE_ENV = { ...process.env };
for (const key of Object.keys(BASE_ENV)) {
  if (/^(CLAUDE_CODE_SESSION_ID|CODEX_SESSION_ID|CODEX_THREAD_ID|GEMINI_SESSION_ID|COPILOT_SESSION_ID|GITHUB_ACTIONS|GITHUB_RUN_ID|OLLAMA_HOST|ROS_TELEMETRY_.*|ROS_ACTOR.*|ROS_EXECUTION_ID)$/.test(key)) {
    delete BASE_ENV[key];
  }
}

const CODEX = { CODEX_SESSION_ID: "codex-session-1", ROS_ACTOR: "openai-codex" };
const CLAUDE = { CLAUDE_CODE_SESSION_ID: "claude-session-1" };
const GEMINI = { GEMINI_SESSION_ID: "gemini-session-1" };
const ALICE = { ROS_ACTOR_KIND: "human", ROS_ACTOR: "alice" };
const LOCAL_AGENT = { ROS_ACTOR_KIND: "agent", ROS_ACTOR: "local-researcher", ROS_TELEMETRY_PROVIDER: "local", ROS_TELEMETRY_RUNTIME: "llama-runner" };

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-provenance-${label}-`));
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort.
    }
  });
  initializeProject({ target: root, project: "Provenance Test" });
  git(root, ["init", "-q"]);
  git(root, ["config", "user.email", "test@example.invalid"]);
  git(root, ["config", "user.name", "ROS Test"]);
  commitAll(root, "baseline");
  return root;
}

function git(root, args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8" });
}

function commitAll(root, message) {
  git(root, ["add", "-A"]);
  git(root, ["commit", "-qm", message, "--allow-empty"]);
}

function ros(root, args, env = {}) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, ...args], {
    cwd: repositoryRoot,
    encoding: "utf8",
    env: { ...BASE_ENV, ...env }
  });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function rosOk(root, args, env = {}) {
  const result = ros(root, args, env);
  assert.equal(result.status, 0, `${args.join(" ")}\n${result.stdout}\n${result.stderr}`);
  return result;
}

const now = () => new Date().toISOString();

function readEvents(root) {
  const file = path.join(root, ".ros", "events", "events.jsonl");
  return fs.existsSync(file) ? fs.readFileSync(file, "utf8").split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line)) : [];
}

function readExecutions(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.existsSync(dir) ? fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8"))) : [];
}

function queueItem(root, id) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "work", "queue.json"), "utf8")).items.find((item) => item.id === id);
}

function setPolicy(root, provenance) {
  const file = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(file, "utf8"));
  config.provenance = provenance;
  fs.writeFileSync(file, `${JSON.stringify(config, null, 2)}\n`);
}

function writeRequirement(root, id, extra = "") {
  const file = path.join(root, "requirements", `${id}--example.md`);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `---\nid: ${id}\ntitle: Example ${id}\nstatus: proposed\ncreated: 2026-09-25\n${extra}---\n\n# ${id}\n\nThe system MUST do the thing.\n`);
  return path.relative(root, file).replaceAll("\\", "/");
}

function show(root, target) {
  return JSON.parse(rosOk(root, ["provenance", "show", target, "--json"]).stdout);
}

function startWork(root, id, env) {
  rosOk(root, ["add", `Work ${id}`, "--id", id], env);
  rosOk(root, ["work", "backlog-transition", "--action", "ready", "--id", id, "--occurred-at", now()], env);
  rosOk(root, ["work", "start", "--id", id, "--occurred-at", now()], env);
}

test("identity: an agent establishes identity once from its runtime environment; unknowns are explicit", (t) => {
  const root = fixture(t, "identity");

  const claude = JSON.parse(rosOk(root, ["identity", "--json"], CLAUDE).stdout);
  assert.deepEqual(claude.actor, {
    kind: "agent",
    id: "claude-code",
    provider: "anthropic",
    model: "unknown",
    runtime: "claude-code",
    executionId: "unknown",
    sessionId: "claude-session-1",
    assurance: "self-reported"
  });
  assert.equal(claude.executionBinding, "unbound");
  assert.match(claude.assurance, /not authentication/);

  const local = JSON.parse(rosOk(root, ["identity", "--json"], { ...LOCAL_AGENT, ROS_TELEMETRY_MODEL: "llama-4" }).stdout);
  assert.equal(local.actor.kind, "agent");
  assert.equal(local.actor.id, "local-researcher");
  assert.equal(local.actor.provider, "local");
  assert.equal(local.actor.model, "llama-4");

  const human = JSON.parse(rosOk(root, ["identity", "--json"], ALICE).stdout);
  assert.deepEqual(human.actor, { kind: "human", id: "alice", assurance: "self-reported" });

  const nobody = JSON.parse(rosOk(root, ["identity", "--json"]).stdout);
  assert.deepEqual(nobody.actor, { kind: "unknown", id: "unknown", assurance: "self-reported" });
});

test("execution identity: work begin binds the run; every event and the execution record carry it; two runs never share it", (t) => {
  const root = fixture(t, "execution");

  startWork(root, "WI-A", CODEX);
  const identityA = JSON.parse(rosOk(root, ["identity", "--json"], CODEX).stdout);
  assert.equal(identityA.executionBinding, "matched-active-execution");

  const [executionA] = readExecutions(root).filter((record) => record.workItemId === "WI-A");
  assert.equal(executionA.actor.kind, "agent");
  assert.equal(executionA.actor.id, "openai-codex");
  assert.equal(executionA.actor.provider, "openai");
  assert.equal(executionA.actor.executionId, executionA.executionId);
  assert.equal(identityA.actor.executionId, executionA.executionId);

  const started = readEvents(root).find((event) => event.type === "work.started" && event.workItem === "WI-A");
  assert.equal(started.actor.executionId, executionA.executionId);
  assert.deepEqual(started.telemetryExecutions, [executionA.executionId]);

  // A second run of the same agent (a new session) is a different execution.
  startWork(root, "WI-B", { ...CODEX, CODEX_SESSION_ID: "codex-session-2" });
  const [executionB] = readExecutions(root).filter((record) => record.workItemId === "WI-B");
  assert.notEqual(executionB.executionId, executionA.executionId);
  assert.equal(executionB.actor.id, executionA.actor.id);

  const completed = rosOk(root, ["work", "complete", "--id", "WI-B", "--occurred-at", now(), "--evidence", "implementation=README.md", "--evidence", "tests=README.md"], { ...CODEX, CODEX_SESSION_ID: "codex-session-2" });
  assert.equal(completed.status, 0);
  const completion = readEvents(root).find((event) => event.type === "work.completed" && event.workItem === "WI-B");
  assert.equal(completion.actor.executionId, executionB.executionId);

  // Session 1's run is untouched and still distinguishable.
  const startedB = readEvents(root).find((event) => event.type === "work.started" && event.workItem === "WI-B");
  assert.notEqual(startedB.actor.executionId, started.actor.executionId);
});

test("execution identity: a different agent acting on another's work is attributed to itself, never to the other's execution", (t) => {
  const root = fixture(t, "handover");
  startWork(root, "WI-H", CODEX);
  const [codexExecution] = readExecutions(root);

  rosOk(root, ["work", "block", "--id", "WI-H", "--occurred-at", now(), "--reason", "handing over"], CLAUDE);
  const blocked = readEvents(root).find((event) => event.type === "work.blocked");
  assert.equal(blocked.actor.id, "claude-code");
  assert.notEqual(blocked.actor.executionId, codexExecution.executionId);
  assert.equal(blocked.actor.executionId, "unknown");
});

test("backlog provenance: creator and later human/agent contributors accumulate; the creator is never the last modifier", (t) => {
  const root = fixture(t, "backlog");

  rosOk(root, ["add", "Draft requirement set", "--id", "WI-Q"], GEMINI);
  rosOk(root, ["work", "update", "--id", "WI-Q", "--occurred-at", now(), "--priority", "high"], ALICE);
  rosOk(root, ["work", "update", "--id", "WI-Q", "--occurred-at", now(), "--title", "Draft requirement set v2"], CODEX);

  const contributions = queueItem(root, "WI-Q").provenance.contributions;
  assert.deepEqual(contributions.map((c) => [c.operation, c.actor.kind, c.actor.id]), [
    ["created", "agent", "gemini-cli"],
    ["modified", "human", "alice"],
    ["modified", "agent", "openai-codex"]
  ]);

  const shown = show(root, "WI-Q");
  assert.equal(shown.creator.id, "gemini-cli");
  assert.deepEqual(shown.involvement, ["agent-created", "agent-modified", "human-modified", "human-corrected-agent-work", "agent-to-agent-revision"]);
});

test("requirement provenance: creation, agent-to-agent revision, human approval, and lineage from another agent's requirement", (t) => {
  const root = fixture(t, "requirements");
  setPolicy(root, { enforce: true, requiredSince: "2026-09-01T00:00:00Z" });
  startWork(root, "WI-R", CODEX);

  const base = writeRequirement(root, "RQ-TEST-2026-A001");
  const created = rosOk(root, ["provenance", "record", base, "--reason", "initial requirement"], CODEX);
  assert.match(created.stdout, /recorded created/);
  commitAll(root, "add requirement");

  rosOk(root, ["provenance", "record", base, "--reason", "clarified acceptance criteria", "--evidence", "EV-TEST-2026-A001"], CLAUDE);
  rosOk(root, ["provenance", "record", base, "--operation", "approved"], ALICE);

  const derived = writeRequirement(root, "RQ-TEST-2026-A002", "derived_from:\n  - RQ-TEST-2026-A001\n");
  rosOk(root, ["provenance", "record", derived], CLAUDE);

  const first = show(root, "RQ-TEST-2026-A001");
  assert.equal(first.creator.id, "openai-codex");
  assert.deepEqual(first.provenance.contributions.map((c) => [c.operation, c.actor.id]), [
    ["created", "openai-codex"],
    ["modified", "claude-code"],
    ["approved", "alice"]
  ]);
  assert.deepEqual(first.provenance.contributions[1].evidence, ["EV-TEST-2026-A001"]);
  // The creating agent's contribution inherits the work item of its bound execution.
  assert.equal(first.provenance.contributions[0].workItem, "WI-R");
  assert.deepEqual(first.involvement, ["agent-created", "agent-modified", "human-approved", "agent-to-agent-revision"]);

  // Lineage and authorship are separate: B authored A002, derived from A's A001.
  const second = show(root, "RQ-TEST-2026-A002");
  assert.equal(second.creator.id, "claude-code");
  assert.deepEqual(second.derivedFrom, ["RQ-TEST-2026-A001"]);

  rosOk(root, ["registry", "build"]);
  const registry = JSON.parse(fs.readFileSync(path.join(root, "registries", "requirements.json"), "utf8"));
  assert.equal(registry.find((entry) => entry.id === "RQ-TEST-2026-A001").provenance.contributions.length, 3);

  const validation = ros(root, ["validate"]);
  assert.equal(validation.status, 0, validation.stderr);

  const summary = JSON.parse(rosOk(root, ["provenance", "summary", "--json"]).stdout);
  assert.deepEqual(summary.agentToAgentRevisions, ["RQ-TEST-2026-A001"]);
  assert.deepEqual(summary.humanApprovedAgentWork, ["RQ-TEST-2026-A001"]);
});

test("legacy records: committed artifacts without provenance stay readable, are never assigned a creator, and modifications default to 'modified'", (t) => {
  const root = fixture(t, "legacy");
  const legacy = writeRequirement(root, "RQ-TEST-2026-A010", "author_agent: someone-long-ago\n");
  fs.writeFileSync(path.join(root, legacy), fs.readFileSync(path.join(root, legacy), "utf8").replace("created: 2026-09-25", "created: 2026-01-01"));
  commitAll(root, "legacy requirement");
  rosOk(root, ["registry", "build"]);
  commitAll(root, "registries");

  setPolicy(root, { enforce: true, requiredSince: "2026-09-01T00:00:00Z" });
  const before = JSON.parse(ros(root, ["provenance", "validate", "--json"]).stdout);
  const legacyFinding = before.findings.find((finding) => finding.path === legacy);
  assert.equal(legacyFinding.severity, "info");
  assert.equal(legacyFinding.code, "legacy-unattributed");
  assert.match(legacyFinding.message, /someone-long-ago/);

  rosOk(root, ["provenance", "record", legacy, "--reason", "typo"], CODEX);
  const shown = show(root, "RQ-TEST-2026-A010");
  assert.equal(shown.creator, null);
  assert.deepEqual(shown.involvement, ["creator-unknown", "agent-modified"]);
});

test("validation: a new requirement without provenance fails validate; malformed or rewritten provenance is an error", (t) => {
  const root = fixture(t, "validation");
  setPolicy(root, { enforce: true, requiredSince: "2026-09-01T00:00:00Z" });
  startWork(root, "WI-V", CODEX);

  const bare = writeRequirement(root, "RQ-TEST-2026-A020");
  rosOk(root, ["registry", "build"]);
  const missing = ros(root, ["validate"]);
  assert.equal(missing.status, 1);
  assert.match(missing.stderr, /missing-provenance/);

  rosOk(root, ["provenance", "record", bare], CODEX);
  rosOk(root, ["registry", "build"]);
  assert.equal(ros(root, ["validate"]).status, 0);
  commitAll(root, "attributed requirement");

  // Rewriting the committed creator is detected against the committed version.
  const file = path.join(root, bare);
  fs.writeFileSync(file, fs.readFileSync(file, "utf8").replace("id: \"openai-codex\"", "id: \"someone-else\""));
  rosOk(root, ["registry", "build"]);
  const rewritten = JSON.parse(ros(root, ["provenance", "validate", "--json"]).stdout);
  assert.ok(rewritten.findings.some((finding) => finding.code === "provenance-rewritten" && finding.severity === "error"));
  git(root, ["checkout", "--", bare]);

  const malformed = writeRequirement(root, "RQ-TEST-2026-A021", "provenance:\n  contributions:\n    - operation: created\n      at: \"2026-09-25\"\n      actor:\n        kind: robot\n        id: x\n");
  rosOk(root, ["registry", "build"]);
  const invalid = ros(root, ["validate", "--json"]);
  assert.equal(invalid.status, 1);
  assert.ok(JSON.parse(invalid.stdout).findings.some((finding) => finding.path === malformed && /invalid-kind/.test(finding.message)));

  const refused = ros(root, ["provenance", "record", malformed], CODEX);
  assert.equal(refused.status, 1);
  assert.match(refused.stderr, /malformed/);
});

test("validation: events recorded after the cutoff without an actor are errors; earlier events are legacy", (t) => {
  const root = fixture(t, "events");
  const eventsFile = path.join(root, ".ros", "events", "events.jsonl");
  const legacyEvent = { schemaVersion: "1.0.0", type: "work.started", workItem: "OLD-1", occurredAt: "2026-01-01T00:00:00.000Z", evidence: [], paths: [], eventId: "legacy000000000000000001" };
  const strayEvent = { ...legacyEvent, workItem: "NEW-1", occurredAt: "2026-09-30T00:00:00.000Z", eventId: "stray0000000000000000001" };
  fs.appendFileSync(eventsFile, `${JSON.stringify(legacyEvent)}\n${JSON.stringify(strayEvent)}\n`);
  setPolicy(root, { enforce: true, requiredSince: "2026-09-01T00:00:00Z" });

  const report = JSON.parse(ros(root, ["provenance", "validate", "--json"]).stdout);
  const byWork = (id) => report.findings.filter((finding) => finding.message.includes(` ${id} `));
  assert.deepEqual(byWork("OLD-1").map((finding) => finding.severity), ["info"]);
  assert.deepEqual(byWork("NEW-1").map((finding) => [finding.severity, finding.code]), [["error", "missing-provenance"]]);
});

test("integration: published events and Ordo handoffs keep the originating actor and execution", (t) => {
  const root = fixture(t, "integration");
  startWork(root, "WI-I", CODEX);
  const [execution] = readExecutions(root);

  rosOk(root, ["adapter", "publish", "--target", ".ros/mock-project-store/events.jsonl"]);
  const published = fs.readFileSync(path.join(root, ".ros", "mock-project-store", "events.jsonl"), "utf8").split("\n").filter(Boolean).map((line) => JSON.parse(line));
  const startedEvent = published.find((event) => event.workItem === "WI-I");
  assert.equal(startedEvent.actor.id, "openai-codex");
  assert.equal(startedEvent.actor.executionId, execution.executionId);

  const handoff = JSON.parse(rosOk(root, ["ordo", "handoff", "--revision", "abc123", "--source", "praxis"], CODEX).stdout);
  assert.equal(handoff.producedBy.id, "openai-codex");
  assert.equal(handoff.producedBy.executionId, execution.executionId);
});
