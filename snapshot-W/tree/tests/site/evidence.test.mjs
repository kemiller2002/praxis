// The public evidence snapshot must only ever say what the Praxis records say.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { buildSnapshot, htmlProblems, lookup, snapshotProblems } from "../../site-tools/evidence.mjs";

const snapshot = JSON.parse(readFileSync(new URL("../../site/data/gh-84.json", import.meta.url), "utf8"));
const html = readFileSync(new URL("../../site/index.html", import.meta.url), "utf8");
const current = buildSnapshot();

test("the committed snapshot is consistent with the records", () => {
  assert.deepEqual(snapshotProblems(snapshot, current), []);
});

test("every data-evidence value on the page matches the snapshot", () => {
  assert.ok(html.includes("data-evidence="), "page cites evidence");
  assert.deepEqual(htmlProblems(snapshot, "index.html", html), []);
});

test("a value that disagrees with the snapshot is caught", () => {
  const execution = snapshot.executions[0];
  const forged = `<dd data-evidence="${execution.executionId}:identity.provider">openai</dd>`;
  assert.equal(htmlProblems(snapshot, "forged.html", forged).length, 1);
  assert.equal(htmlProblems(snapshot, "missing.html", '<b data-evidence="EXE-NOPE:status">active</b>').length, 1);
});

test("a snapshot that rewrites identity or runs ahead of the record is caught", () => {
  const [first, ...rest] = snapshot.executions;
  const rewritten = { ...snapshot, executions: [{ ...first, identity: { ...first.identity, runtime: "chatgpt" } }, ...rest] };
  assert.ok(snapshotProblems(rewritten, current).some((problem) => problem.includes("identity.runtime")));
  const invented = { ...snapshot, executions: [{ ...first, executionId: "EXE-20990101T000000000Z-00000000" }, ...rest] };
  assert.ok(snapshotProblems(invented, current).some((problem) => problem.includes("no such execution")));
  const active = current.executions.find((execution) => execution.status === "active");
  if (active) {
    const ahead = { ...snapshot, executions: [{ ...active, status: "finalized" }] };
    assert.ok(snapshotProblems(ahead, current).some((problem) => problem.includes("ahead of record")));
  }
});

test("unknown model stays unknown, never guessed", () => {
  snapshot.executions.forEach((execution) => assert.ok(execution.identity.model === "unknown" || typeof execution.identity.model === "string"));
  assert.equal(lookup(snapshot, `${snapshot.executions[0].executionId}:identity.model`), current.executions[0].identity.model);
});

test("the snapshot never carries private identifiers", () => {
  const raw = JSON.stringify(snapshot);
  ["sessionId", "conversationId", "runId", "dirtyPaths", "/home/", "/root/"].forEach((field) => assert.ok(!raw.includes(field), field));
  assert.ok(!/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(raw), "no UUIDs");
});

test("the ledger renderer fills an empty ledger and is idempotent", async () => {
  const { withLedger } = await import("../../site-tools/evidence.mjs");
  const empty = "<tbody>\n            <!-- ledger:start -->\n            <!-- ledger:end -->\n</tbody>";
  const once = withLedger(empty, snapshot);
  assert.equal((once.match(/<tr>/g) ?? []).length, snapshot.workItems.length);
  assert.equal(withLedger(once, snapshot), once);
});
