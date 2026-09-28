// GitHub Actions adapter for praxis.remote (PRAXIS-REMOTE-06,
// DF-ROS-2026-A041 section 10). The persistence script is exercised against
// real Git remotes with the built F# executor standing in for the pinned
// release; the workflow and actions are checked for the trust boundary #90
// requires (least privilege, no forks, no interpolation, no secrets, pinned
// dependencies, no Praxis domain logic in YAML).
import assert from "node:assert/strict";
import crypto from "node:crypto";
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
const persist = path.join(repositoryRoot, "scripts", "praxis-remote-persist.sh");
const read = (relative) => fs.readFileSync(path.join(repositoryRoot, relative), "utf8");

function git(root, ...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim();
}

function temporary(t, label) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), `praxis-adapter-${label}-`));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return dir;
}

/** A bare "GitHub" remote and a runner checkout of its main branch. */
function remoteAndCheckout(t) {
  const base = temporary(t, "repo");
  const origin = path.join(base, "origin.git");
  const seed = path.join(base, "seed");
  git(base, "init", "-q", "--bare", "-b", "main", origin);
  fs.mkdirSync(seed);
  initializeProject({ target: seed, project: "Adapter" });
  const config = JSON.parse(fs.readFileSync(path.join(seed, "ros.json"), "utf8"));
  config.remote = { capabilities: ["read", "mutate", "complete", "reconcile"] };
  fs.writeFileSync(path.join(seed, "ros.json"), `${JSON.stringify(config, null, 2)}\n`);
  git(seed, "init", "-q", "-b", "main");
  git(seed, "config", "user.email", "seed@example.invalid");
  git(seed, "config", "user.name", "Seed");
  git(seed, "add", "-A");
  git(seed, "commit", "-qm", "baseline");
  git(seed, "remote", "add", "origin", origin);
  git(seed, "push", "-q", "origin", "main");
  const runner = path.join(base, "runner");
  git(base, "clone", "-q", origin, runner);
  return { base, origin, seed, runner };
}

function execute(root, body, env = {}) {
  const file = path.join(os.tmpdir(), `praxis-adapter-request-${process.pid}-${Math.random().toString(16).slice(2)}.json`);
  const response = path.join(os.tmpdir(), `praxis-adapter-response-${process.pid}-${Math.random().toString(16).slice(2)}.json`);
  fs.writeFileSync(file, JSON.stringify(body));
  spawnSync("dotnet", [cli, "--root", root, "remote", "execute", "--request", file, "--grant", "read", "--grant", "mutate", "--output", response], {
    cwd: root,
    encoding: "utf8",
    env: deterministicIdentityEnv(env)
  });
  fs.rmSync(file, { force: true });
  return response;
}

function persistResponse(root, response, mode = "push") {
  const output = `${response}.adapter.json`;
  const result = spawnSync("sh", [persist, "--response", response, "--output", output, "--mode", mode], { cwd: root, encoding: "utf8" });
  return { status: result.status, stderr: result.stderr, adapter: fs.existsSync(output) ? JSON.parse(fs.readFileSync(output, "utf8")) : null };
}

function startRequest(root, requestId = "req-adapter-0001") {
  return {
    protocol: "praxis.remote",
    protocolVersion: "1.0",
    requestId,
    operation: "work.start",
    repository: { ref: "refs/heads/main", expectedSha: git(root, "rev-parse", "HEAD") },
    actor: { kind: "agent", id: "example/cloud-agent", provider: "example", runtime: "cloud-agent" },
    arguments: { workItemIds: ["WI-0100"] }
  };
}

test("persists exactly the reported Praxis state as the executor, naming the asserted requester", (t) => {
  const { origin, runner } = remoteAndCheckout(t);
  const response = execute(runner, startRequest(runner), { GITHUB_ACTIONS: "true", GITHUB_RUN_ID: "77", GITHUB_RUN_ATTEMPT: "3", GITHUB_ACTOR: "octocat", GITHUB_TRIGGERING_ACTOR: "octocat" });
  const reported = JSON.parse(fs.readFileSync(response, "utf8")).persistence.paths;

  const { status, adapter, stderr } = persistResponse(runner, response);
  assert.equal(status, 0, stderr);
  assert.equal(adapter.persisted, true);
  assert.equal(adapter.failure, null);
  assert.equal(git(origin, "rev-parse", "main"), adapter.commit);

  const committed = git(runner, "show", "--name-only", "--format=", adapter.commit).split("\n").sort();
  assert.deepEqual(committed, [...reported].sort());
  assert.equal(git(runner, "show", "-s", "--format=%an", adapter.commit), "github-actions[bot]");
  const message = git(runner, "show", "-s", "--format=%B", adapter.commit);
  assert.match(message, /^Praxis-Request-Id: req-adapter-0001$/m);
  assert.match(message, /^Praxis-Requester: agent:example\/cloud-agent \(asserted by the request\)$/m);
  assert.match(message, /^Praxis-Executor: github-actions run 77 attempt 3$/m);
  assert.equal(git(runner, "status", "--porcelain"), "");
});

test("a ref that moved before the push is a concurrency conflict and nothing reaches the remote", (t) => {
  const { origin, seed, runner } = remoteAndCheckout(t);
  const response = execute(runner, startRequest(runner));

  fs.writeFileSync(path.join(seed, "notes.md"), "someone else\n");
  git(seed, "add", "-A");
  git(seed, "commit", "-qm", "concurrent change");
  git(seed, "push", "-q", "origin", "main");
  const remoteHead = git(origin, "rev-parse", "main");

  const { status, adapter } = persistResponse(runner, response);
  assert.equal(status, 1);
  assert.equal(adapter.persisted, false);
  assert.equal(adapter.failure.code, "concurrency-conflict");
  assert.equal(adapter.failure.retry, "after-refresh");
  assert.equal(git(origin, "rev-parse", "main"), remoteHead, "the remote was not changed");
});

test("a rejected request persists nothing and makes no commit", (t) => {
  const { origin, runner } = remoteAndCheckout(t);
  const body = { ...startRequest(runner), repository: { ref: "refs/heads/main", expectedSha: "0".repeat(40) } };
  const response = execute(runner, body);
  assert.equal(JSON.parse(fs.readFileSync(response, "utf8")).failure.code, "stale-ref");
  const before = git(origin, "rev-parse", "main");

  const { status, adapter } = persistResponse(runner, response);
  assert.equal(status, 0);
  assert.equal(adapter.persisted, false);
  assert.equal(adapter.commit, null);
  assert.equal(git(origin, "rev-parse", "main"), before);
});

test("the adapter refuses to persist anything that is not reported Praxis-owned state", (t) => {
  const { runner } = remoteAndCheckout(t);
  const response = execute(runner, startRequest(runner));

  const forged = JSON.parse(fs.readFileSync(response, "utf8"));
  forged.persistence.paths.push("src/Program.fs");
  const forgedFile = `${response}.forged.json`;
  fs.writeFileSync(forgedFile, JSON.stringify(forged));
  assert.equal(persistResponse(runner, forgedFile).status, 2);

  fs.writeFileSync(path.join(runner, "README.extra.md"), "not Praxis state\n");
  git(runner, "add", "README.extra.md");
  const staged = persistResponse(runner, response);
  assert.equal(staged.status, 2);
  assert.match(staged.stderr, /unreported paths: README\.extra\.md/);
});

// ---------------------------------------------------------------------------
// Static trust-boundary checks on the workflow and actions.

const workflow = read(".github/workflows/praxis-remote.yml");
const remoteAction = read(".github/actions/praxis-remote/action.yml");
const setupAction = read(".github/actions/praxis-setup/action.yml");

function jobBlock(name) {
  const match = workflow.match(new RegExp(`\\n  ${name}:\\n([\\s\\S]*?)(?=\\n  [a-z-]+:\\n|$)`));
  assert.ok(match, `job ${name}`);
  return match[1];
}

/** Every `run:` script body, from both block and inline forms. */
function runBodies(yaml) {
  const lines = yaml.split("\n");
  const bodies = [];
  lines.forEach((line, index) => {
    const inline = line.match(/^(\s*)(?:- )?run: (?!\|)(.+)$/);
    if (inline) bodies.push(inline[2]);
    const block = line.match(/^(\s*)(?:- )?run: \|\s*$/);
    if (block) {
      const indent = block[1].length;
      const body = [];
      for (let next = index + 1; next < lines.length; next += 1) {
        const text = lines[next];
        if (text.trim() !== "" && text.search(/\S/) <= indent) break;
        body.push(text);
      }
      bodies.push(body.join("\n"));
    }
  });
  return bodies;
}

test("only dispatch and workflow_call can start remote execution; forks and pull requests cannot", () => {
  const triggers = workflow.split("\non:\n")[1].split("\n\n")[0];
  const events = [...triggers.matchAll(/^  ([a-z_]+):/gm)].map((match) => match[1]);
  assert.deepEqual(events, ["workflow_dispatch", "workflow_call"]);
  assert.doesNotMatch(workflow, /pull_request_target|pull_request:/);
});

test("credentials are least-privilege per job and default to none", () => {
  assert.match(workflow, /\npermissions: \{\}\n/);
  const permissionsOf = (block) => block.match(/\n    permissions:\n((?:      .+\n)+)/)[1];
  const readJob = jobBlock("read");
  assert.equal(permissionsOf(readJob), "      contents: read\n      attestations: read\n");
  const writeJob = jobBlock("write");
  assert.equal(permissionsOf(writeJob), "      contents: write\n      attestations: read\n");
  assert.match(jobBlock("write-pull-request"), /pull-requests: write/);
  assert.match(writeJob, /needs\.read\.outputs\.mutating == 'true'/, "writes happen only for requests Praxis classified as mutating");
});

test("request content never reaches a shell by interpolation and no secret is used", () => {
  for (const yaml of [workflow, remoteAction, setupAction]) {
    for (const body of runBodies(yaml)) {
      assert.doesNotMatch(body, /\$\{\{/, `run bodies take inputs from the environment only:\n${body}`);
    }
    assert.doesNotMatch(yaml, /secrets\./);
  }
});

test("every third-party action is pinned to a commit SHA", () => {
  for (const yaml of [workflow, remoteAction, setupAction]) {
    for (const [, reference] of yaml.matchAll(/uses:\s*(\S+)/g)) {
      if (reference.startsWith("./")) continue;
      assert.match(reference, /@[0-9a-f]{40}$/, reference);
    }
  }
});

test("the adapter holds no Praxis domain logic: it only classifies, executes, and persists", () => {
  const commands = [workflow, remoteAction]
    .flatMap(runBodies)
    .flatMap((body) => [...body.matchAll(/"\$PRAXIS" ([a-z-]+(?: [a-z-]+)?)/g)].map((match) => match[1]));
  assert.deepEqual([...new Set(commands)].sort(), ["remote classify", "remote execute"]);
  for (const yaml of [workflow, remoteAction]) {
    assert.doesNotMatch(yaml, /work (start|begin|complete|block|resume|reconcile)|--occurred-at|events\.jsonl|current\.json/);
  }
});

test("Praxis, not YAML, classifies which requests need write credentials", (t) => {
  const { runner } = remoteAndCheckout(t);
  const classify = (body) => {
    const file = path.join(temporary(t, "classify"), "request.json");
    fs.writeFileSync(file, typeof body === "string" ? body : JSON.stringify(body));
    const result = spawnSync("dotnet", [cli, "remote", "classify", "--request", file], { encoding: "utf8" });
    return { status: result.status, json: JSON.parse(result.stdout) };
  };
  const mutation = classify(startRequest(runner));
  assert.equal(mutation.status, 0);
  assert.deepEqual(mutation.json, { requestId: "req-adapter-0001", operation: "work.start", capabilities: ["mutate"], mutating: true });
  const readOnly = classify({ protocol: "praxis.remote", protocolVersion: "1.0", requestId: "req-read-0001", operation: "validate" });
  assert.equal(readOnly.json.mutating, false);
  const invalid = classify("{\"protocol\":\"praxis.remote\",\"protocolVersion\":\"9.0\"}");
  assert.equal(invalid.status, 1);
  assert.equal(invalid.json.failure.code, "unsupported-protocol");
});

test("pull-request persistence proposes the state on its own branch instead of pushing the target", (t) => {
  const { origin, runner } = remoteAndCheckout(t);
  const response = execute(runner, startRequest(runner, "req-adapter:pr-0001"));
  const stub = temporary(t, "gh");
  fs.writeFileSync(path.join(stub, "gh"), "#!/bin/sh\necho https://github.example/octo/repo/pull/7\n", { mode: 0o755 });
  const before = git(origin, "rev-parse", "main");

  const output = `${response}.adapter.json`;
  const result = spawnSync("sh", [persist, "--response", response, "--output", output, "--mode", "pull-request"], {
    cwd: runner,
    encoding: "utf8",
    env: { ...process.env, PATH: `${stub}:${process.env.PATH}` }
  });
  assert.equal(result.status, 0, `${result.stderr}${fs.existsSync(output) ? fs.readFileSync(output, "utf8") : ""}`);
  const adapter = JSON.parse(fs.readFileSync(output, "utf8"));
  assert.equal(adapter.persisted, false, "pending merge is not persisted to the target ref");
  const digest = crypto.createHash("sha256").update("req-adapter:pr-0001").digest("hex").slice(0, 24);
  assert.equal(adapter.branch, `praxis/remote/${digest}`);
  assert.equal(adapter.pullRequest, "https://github.example/octo/repo/pull/7");
  assert.equal(git(origin, "rev-parse", "main"), before);
  assert.equal(git(origin, "rev-parse", adapter.branch), adapter.commit);
});

test("the operator documentation covers every failure code and names only files that exist", () => {
  const operations = read("docs/remote-execution-operations.md");
  const codes = JSON.parse(read("schemas/praxis-remote-response.schema.json")).properties.failure.oneOf[1].properties.code.enum;
  for (const code of codes) assert.ok(operations.includes(`\`${code}\``), `failure code ${code} is documented`);
  for (const file of [
    ".github/workflows/praxis-remote.yml",
    ".github/actions/praxis-remote/",
    ".github/actions/praxis-setup/",
    "scripts/praxis-bootstrap.sh",
    "scripts/praxis-remote-persist.sh"
  ]) {
    assert.ok(operations.includes(file), `${file} is named`);
    assert.ok(fs.existsSync(path.join(repositoryRoot, file)), `${file} exists`);
  }
  for (const heading of ["Installation", "Permissions", "Version pinning and upgrades", "Invocation and results", "Concurrency and idempotency", "Security model", "Failure and retry", "Reconciliation", "Troubleshooting"]) {
    assert.match(operations, new RegExp(`^## ${heading}$`, "m"), heading);
  }
});
