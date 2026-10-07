import assert from "node:assert/strict";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { deterministicIdentityEnv } from "./deterministic-identity-env.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");
const nodeTelemetry = path.join(repositoryRoot, "tools", "ros_telemetry.mjs");

// Actor resolution (RQ-ROS-2026-A001) exists twice: Ros.Domain.Provenance
// .ActorResolution behind the F# CLI, and tools/ros_telemetry.mjs's
// resolveActor behind the Node internal library that still writes work
// events and backlog items for the web server (DF-ROS-2026-A033). Both must
// resolve the identical actor, key order included, because the actor is
// hashed into every event ID.
function fsharpActor(env, args = []) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", repositoryRoot, "provenance", "identity", "--json", ...args], {
    cwd: repositoryRoot, encoding: "utf8", env
  });
  assert.equal(result.status, 0, result.stderr);
  return JSON.stringify(JSON.parse(result.stdout).actor);
}

function nodeActor(env, options = {}) {
  const script = `import(${JSON.stringify(nodeTelemetry)}).then((m) => process.stdout.write(JSON.stringify(m.resolveActor(${JSON.stringify(options)}))))`;
  const result = spawnSync(process.execPath, ["-e", script], { cwd: repositoryRoot, encoding: "utf8", env });
  assert.equal(result.status, 0, result.stderr);
  return result.stdout;
}

const CASES = [
  { name: "nothing known", env: {} },
  { name: "Claude Code runtime", env: { CLAUDE_CODE_SESSION_ID: "s-1" } },
  { name: "Codex runtime with model", env: { CODEX_SESSION_ID: "c-1", ROS_TELEMETRY_MODEL: "gpt-5-codex" } },
  { name: "Gemini CLI runtime", env: { GEMINI_SESSION_ID: "g-1" } },
  { name: "GitHub Actions automation", env: { GITHUB_ACTIONS: "true", GITHUB_RUN_ID: "9" } },
  { name: "local model server implies no kind", env: { OLLAMA_HOST: "http://localhost:11434" } },
  { name: "declared human via environment", env: { ROS_ACTOR_KIND: "human", ROS_ACTOR: "kevin" } },
  { name: "future provider declared via environment", env: { ROS_ACTOR_KIND: "agent", ROS_TELEMETRY_PROVIDER: "future-ai", ROS_TELEMETRY_RUNTIME: "future-cli" } },
  { name: "an empty provider variable counts as unset", env: { ROS_TELEMETRY_PROVIDER: "", CLAUDE_CODE_SESSION_ID: "s-1" } },
  { name: "an explicit unknown provider suppresses runtime detection", env: { ROS_TELEMETRY_PROVIDER: "unknown", CLAUDE_CODE_SESSION_ID: "s-1" } },
  { name: "an explicit unknown runtime suppresses runtime detection", env: { ROS_TELEMETRY_RUNTIME: "unknown", CODEX_SESSION_ID: "c-1" } },
  { name: "an explicit unknown provider suppresses CI detection", env: { ROS_TELEMETRY_PROVIDER: "unknown", GITHUB_ACTIONS: "true" } },
  { name: "a whitespace provider is not a known provider", env: { ROS_TELEMETRY_PROVIDER: " ", ROS_TELEMETRY_RUNTIME: "x" } },
  { name: "explicit flags override the runtime", env: { CLAUDE_CODE_SESSION_ID: "s-1" }, args: ["--actor-kind", "agent", "--agent", "reviewer-bot", "--model", "m-2"], options: { actorKind: "agent", agentId: "reviewer-bot", model: "m-2" } }
];

for (const entry of CASES) {
  test(`F# and Node resolve the same actor: ${entry.name}`, () => {
    assert.ok(fsharpCli, "build:fsharp must produce the CLI before this test runs");
    const env = deterministicIdentityEnv(entry.env);
    assert.equal(fsharpActor(env, entry.args ?? []), nodeActor(env, entry.options ?? {}));
  });
}

test("add records the same structured creator in both implementations", () => {
  const env = deterministicIdentityEnv();
  const fsharp = spawnSync("dotnet", [fsharpCli, "--root", repositoryRoot, "provenance", "identity", "--json", "--actor-kind", "human", "--actor", "kevin"], { cwd: repositoryRoot, encoding: "utf8", env });
  assert.equal(fsharp.status, 0, fsharp.stderr);
  const script = `import(${JSON.stringify(nodeTelemetry)}).then((m) => process.stdout.write(JSON.stringify(m.resolveActor({ agentId: "kevin", actorKind: "human" }))))`;
  const node = spawnSync(process.execPath, ["-e", script], { cwd: repositoryRoot, encoding: "utf8", env });
  assert.equal(JSON.stringify(JSON.parse(fsharp.stdout).actor), node.stdout);
  assert.equal(node.stdout, JSON.stringify({ kind: "human", id: "kevin" }));
});

test("both implementations reject an invalid explicit actor kind", () => {
  const env = deterministicIdentityEnv({ ROS_ACTOR_KIND: "robot" });
  const fsharp = spawnSync("dotnet", [fsharpCli, "--root", repositoryRoot, "provenance", "identity", "--json"], { cwd: repositoryRoot, encoding: "utf8", env });
  assert.equal(fsharp.status, 2);
  assert.match(fsharp.stderr, /unknown actor kind 'robot'/);
  const script = `import(${JSON.stringify(nodeTelemetry)}).then((m) => m.resolveActor({}))`;
  const node = spawnSync(process.execPath, ["-e", script], { cwd: repositoryRoot, encoding: "utf8", env });
  assert.notEqual(node.status, 0);
  assert.match(node.stderr, /unknown actor kind 'robot'/);
});
