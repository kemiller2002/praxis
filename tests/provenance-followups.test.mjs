import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

// End-to-end coverage, through the real F# CLI, of three provenance
// follow-ups on the DF-ROS-2026-A036 model: collaboration aggregates in
// `provenance audit`, `producedBy` on `ordo handoff`, and the installer's
// automation actor.

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// Only what each test declares identifies the process.
const BASE_ENV = Object.fromEntries(
  Object.entries(process.env).filter(([key]) =>
    !/^(CLAUDE_CODE_SESSION_ID|CODEX_SESSION_ID|CODEX_THREAD_ID|GEMINI_SESSION_ID|COPILOT_SESSION_ID|GITHUB_ACTIONS|GITHUB_RUN_ID|OLLAMA_HOST|ROS_TELEMETRY_.*|ROS_ACTOR.*|ROS_EXECUTION_ID)$/.test(key)
  )
);
const CLAUDE = { CLAUDE_CODE_SESSION_ID: "claude-session-1" };

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-provenance-followups-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  initializeProject({ target: root, project: "Provenance Followups" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["-c", "user.email=t@example.invalid", "-c", "user.name=T", "add", "-A"], { cwd: root });
  execFileSync("git", ["-c", "user.email=t@example.invalid", "-c", "user.name=T", "commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function ros(root, args, env = {}) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, ...args], { cwd: repositoryRoot, encoding: "utf8", env: { ...BASE_ENV, ...env } });
  assert.equal(result.status, 0, `${args.join(" ")}\n${result.stdout}\n${result.stderr}`);
  return result.stdout;
}

const now = () => new Date().toISOString();

test("the installer's bookkeeping event carries the automation actor", (t) => {
  const root = fixture(t);
  const [event] = fs.readFileSync(path.join(root, ".ros", "events", "events.jsonl"), "utf8").split("\n").filter(Boolean).map((line) => JSON.parse(line));
  assert.deepEqual(event.actor, { kind: "automation", id: "ros-bootstrap", runtime: "ros-bootstrap" });
  const audit = JSON.parse(spawnSync("dotnet", [fsharpCli, "--root", root, "provenance", "audit", "--json"], { cwd: repositoryRoot, encoding: "utf8", env: BASE_ENV }).stdout);
  assert.equal(audit.summary.eventsWithActor, audit.summary.events);
});

test("ordo handoff names the producing agent and its bound execution", (t) => {
  const root = fixture(t);
  ros(root, ["add", "Handoff work", "--id", "WI-H"], CLAUDE);
  ros(root, ["work", "backlog-transition", "--action", "ready", "--id", "WI-H", "--occurred-at", now()], CLAUDE);
  ros(root, ["work", "start", "--id", "WI-H", "--occurred-at", now()], CLAUDE);
  const [execution] = fs.readdirSync(path.join(root, ".ros", "telemetry", "executions")).map((name) => name.replace(/\.json$/, ""));

  const handoff = JSON.parse(ros(root, ["ordo", "handoff", "--revision", "abc123", "--source", "praxis"], CLAUDE));
  assert.equal(handoff.producedBy.actor.kind, "agent");
  assert.equal(handoff.producedBy.actor.id, "anthropic/claude-code");
  assert.equal(handoff.producedBy.execution, execution);

  // Another session of the same agent is a different run: no execution is inherited.
  const other = JSON.parse(ros(root, ["ordo", "handoff", "--revision", "abc123", "--source", "praxis"], { CLAUDE_CODE_SESSION_ID: "claude-session-2" }));
  assert.equal(other.producedBy.execution, null);
});

test("provenance audit reports collaboration aggregates", (t) => {
  const root = fixture(t);
  const audit = JSON.parse(ros(root, ["provenance", "audit", "--json"]));
  assert.deepEqual(audit.collaboration, {
    agentToAgentRevisions: [],
    humanCorrectionsOfAgentWork: [],
    humanApprovedAgentWork: [],
    hotspots: []
  });
});
