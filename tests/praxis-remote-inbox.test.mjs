// Praxis remote request inbox (DF-ROS-2026-A045, RQ-ROS-2026-A023): a
// request committed to a praxis-inbox/** branch is relayed, byte for byte,
// to praxis-remote.yml on the branch it names. The relay never interprets,
// alters, or authorizes a request, and its trust boundary is dispatch's.
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const relay = path.join(repositoryRoot, "scripts", "praxis-remote-inbox.sh");
const read = (relative) => fs.readFileSync(path.join(repositoryRoot, relative), "utf8");
const workflow = read(".github/workflows/praxis-remote-inbox.yml");

function temporary(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "praxis-inbox-"));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return dir;
}

/** Runs the relay over a workspace holding `files`, for a push that touched `touched`. */
function relayPush(t, files, touched = Object.keys(files), commits = null) {
  const root = temporary(t);
  const workspace = path.join(root, "workspace");
  Object.entries(files).forEach(([relative, content]) => {
    fs.mkdirSync(path.dirname(path.join(workspace, relative)), { recursive: true });
    fs.writeFileSync(path.join(workspace, relative), content);
  });
  fs.mkdirSync(workspace, { recursive: true });
  const event = path.join(root, "event.json");
  fs.writeFileSync(event, JSON.stringify({ commits: commits ?? [{ added: touched, modified: [] }] }));
  // The stand-in dispatcher records its arguments and the exact bytes of the @file it was given.
  const log = path.join(root, "dispatches.jsonl");
  const stub = path.join(root, "dispatch.mjs");
  fs.writeFileSync(
    stub,
    `#!/usr/bin/env node
import fs from "node:fs";
const args = process.argv.slice(2);
const field = args.find((arg) => arg.startsWith("request=@"));
const bytes = field ? fs.readFileSync(field.slice("request=@".length), "utf8") : null;
fs.appendFileSync(${JSON.stringify(log)}, JSON.stringify({ args, bytes }) + "\\n");
`
  );
  fs.chmodSync(stub, 0o755);
  const summary = path.join(root, "summary.md");
  const result = spawnSync("bash", [relay, "--event", event, "--workspace", workspace, "--default-ref", "refs/heads/main"], {
    encoding: "utf8",
    env: { ...process.env, PRAXIS_INBOX_DISPATCH: stub, GITHUB_STEP_SUMMARY: summary, GITHUB_REF_NAME: "praxis-inbox/test", GITHUB_SHA: "abc123", GITHUB_ACTOR: "someone" }
  });
  const dispatches = fs.existsSync(log) ? fs.readFileSync(log, "utf8").trim().split("\n").map((line) => JSON.parse(line)) : [];
  return { ...result, dispatches, summary: fs.existsSync(summary) ? fs.readFileSync(summary, "utf8") : "" };
}

const request = (overrides = {}) => ({
  protocol: "praxis.remote",
  protocolVersion: "1.3",
  requestId: "req-inbox-test-0001",
  operation: "work.continue",
  repository: { ref: "refs/heads/proof/chatgpt-continuation", expectedSha: "3".repeat(40) },
  actor: { kind: "agent", id: "example/successor" },
  arguments: { workItemId: "WI-1" },
  ...overrides
});

test("a committed request is dispatched on the branch it names, byte for byte, under its own requestId", (t) => {
  // Unusual but valid formatting must survive untouched: the relay passes the file, not a re-serialization.
  const bytes = `${JSON.stringify(request(), null, 3)}\n\n`;
  const run = relayPush(t, { ".praxis-inbox/req-inbox-test-0001.json": bytes });
  assert.equal(run.status, 0, run.stderr || run.stdout);
  assert.equal(run.dispatches.length, 1);
  const [{ args, bytes: sent }] = run.dispatches;
  assert.deepEqual(args.slice(0, 7), ["workflow", "run", "praxis-remote.yml", "--ref", "proof/chatgpt-continuation", "-f", "request_id=req-inbox-test-0001"]);
  assert.equal(sent, bytes, "the request reached praxis-remote.yml exactly as committed");
  assert.match(run.summary, /pushed by `someone`/);
  assert.match(run.summary, /dispatched `praxis remote req-inbox-test-0001` on `proof\/chatgpt-continuation`/);
});

test("the relay never supplies or alters an actor; a request without one is relayed as it is", (t) => {
  const { actor, ...anonymous } = request();
  assert.ok(actor);
  const bytes = JSON.stringify(anonymous);
  const run = relayPush(t, { ".praxis-inbox/a.json": bytes });
  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.dispatches[0].bytes, bytes);
  assert.doesNotMatch(fs.readFileSync(relay, "utf8"), /"actor"\s*[:=]|\bactor\s*=/, "the relay has no actor logic");
});

test("a read request with no ref goes to the default branch", (t) => {
  const run = relayPush(t, { ".praxis-inbox/describe.json": JSON.stringify({ protocol: "praxis.remote", protocolVersion: "1.3", requestId: "req-describe-0001", operation: "praxis.describe" }) });
  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.dispatches[0].args[4], "main");
});

test("unroutable files are refused without dispatching, visibly, while valid ones in the same push still go", (t) => {
  const files = {
    ".praxis-inbox/good.json": JSON.stringify(request()),
    ".praxis-inbox/broken.json": "{ not json",
    ".praxis-inbox/other.json": JSON.stringify({ ...request(), protocol: "something-else" }),
    ".praxis-inbox/short-id.json": JSON.stringify(request({ requestId: "r1" })),
    ".praxis-inbox/inbox-target.json": JSON.stringify(request({ repository: { ref: "refs/heads/praxis-inbox/loop", expectedSha: "1".repeat(40) } })),
    ".praxis-inbox/traversal.json": JSON.stringify(request({ repository: { ref: "refs/heads/a/../main", expectedSha: "1".repeat(40) } })),
    ".praxis-inbox/tag.json": JSON.stringify(request({ repository: { ref: "refs/tags/v1", expectedSha: "1".repeat(40) } }))
  };
  const run = relayPush(t, files);
  assert.equal(run.status, 1, "a refused file fails the relay so the submitter sees it");
  assert.deepEqual(run.dispatches.map((dispatch) => dispatch.args[6]), ["request_id=req-inbox-test-0001"]);
  const refusals = run.stdout.split("\n").filter((line) => line.startsWith("::error"));
  assert.equal(refusals.length, 6, run.stdout);
  assert.match(run.summary, /`\.praxis-inbox\/broken\.json`: refused, not valid JSON/);
  assert.match(run.summary, /Nothing was dispatched/);
});

test("only inbox files touched by the push are relayed; others and deleted files are not", (t) => {
  const run = relayPush(
    t,
    { ".praxis-inbox/new.json": JSON.stringify(request()), ".praxis-inbox/old.json": JSON.stringify(request({ requestId: "req-old-00001" })), "README.md": "x", ".praxis-inbox/sub/nested.json": "{}" },
    null,
    [{ added: [".praxis-inbox/new.json", "README.md", ".praxis-inbox/sub/nested.json", ".praxis-inbox/gone.json", ".praxis-inbox/bad name.json"], modified: [] }]
  );
  assert.equal(run.status, 0, run.stderr || run.stdout);
  assert.deepEqual(run.dispatches.map((dispatch) => dispatch.args[6]), ["request_id=req-inbox-test-0001"]);
  assert.match(run.stdout, /skipped \.praxis-inbox\/gone\.json/);
});

test("a push with no inbox request dispatches nothing and succeeds", (t) => {
  const run = relayPush(t, { "README.md": "x" }, ["README.md"]);
  assert.equal(run.status, 0);
  assert.equal(run.dispatches.length, 0);
});

test("the inbox's trust boundary is dispatch's: push to inbox branches only, no pull_request, least privilege, no secrets", () => {
  const triggers = workflow.split("\non:\n")[1].split("\n\n")[0];
  assert.deepEqual([...triggers.matchAll(/^  ([a-z_]+):/gm)].map((match) => match[1]), ["push"]);
  assert.match(triggers, /branches: \["praxis-inbox\/\*\*"\]/);
  assert.match(triggers, /paths: \["\.praxis-inbox\/\*\.json"\]/);
  assert.doesNotMatch(triggers, /pull_request/);
  assert.doesNotMatch(workflow, /pull_request_target|workflow_run/);
  assert.match(workflow, /\npermissions: \{\}\n/);
  assert.match(workflow, /\n    permissions:\n      contents: read\n      actions: write\n/);
  assert.doesNotMatch(workflow, /secrets\./);
  for (const [, reference] of workflow.matchAll(/uses:\s*(\S+)/g)) {
    assert.match(reference, /@[0-9a-f]{40}$/, reference);
  }
  for (const [, body] of workflow.matchAll(/\n        run: (.+)\n/g)) {
    assert.doesNotMatch(body, /\$\{\{/, "run bodies take values from the environment only");
  }
});

test("the relay targets praxis-remote.yml and nothing else", () => {
  const script = fs.readFileSync(relay, "utf8");
  const targets = [...script.matchAll(/workflow run (\S+)/g)].map((match) => match[1]);
  assert.ok(targets.length > 0 && targets.every((target) => target === "praxis-remote.yml"), targets.join(", "));
  assert.match(read(".github/workflows/praxis-remote.yml"), /\n  workflow_dispatch:\n/);
});

test("the agent contract documents the inbox and when to use it", () => {
  const contract = read("docs/remote-agent-contract.md");
  assert.match(contract, /praxis-inbox\//);
  assert.match(contract, /\.praxis-inbox\/<requestId>\.json/);
  assert.match(contract, /cannot dispatch/i);
});
