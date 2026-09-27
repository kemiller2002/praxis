// PRAXIS-SITE-25: regression tests for claims the adversarial audit corrected.
// Each test pins the honest wording against the code that makes it true.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const read = (file) => readFileSync(new URL(`../../${file}`, import.meta.url), "utf8");
const html = read("site/index.html");
const text = html.replace(/<[^>]+>/g, " ").replace(/&rsquo;/g, "'").replace(/\s+/g, " ");

test("attribution enforcement is described with its leniency while work is open", () => {
  assert.match(read("src/Ros.Domain/Work/Attribution.fs"), /not request\.HasActiveOrBlockedWork/);
  assert.match(text, /when any work item is active or blocked, unattributed changes are not reported/);
  assert.ok(!/Never quietly absorbed|not absorbed into the nearest/.test(text));
});

test("handoff copy matches resume semantics: rejoin if open, new child only after finalize", () => {
  assert.match(text, /resuming rejoins any execution still open/);
  assert.match(text, /Once an execution is finalized, resuming opens a new one/);
  assert.ok(!/When work changes hands, the next actor opens a new execution/.test(text));
});

test("the GH-84 story discloses that the same session resumed", () => {
  assert.match(text, /the same Claude Code session resumed the work/);
  assert.match(text, /Both executions carry the same runtime session identifier/);
});

test("token and cost totals are reported from the total metrics, not their components", () => {
  const evidence = read("site-tools/evidence.mjs");
  assert.match(evidence, /metricStatus\(record, "tokens\.", "tokens\.total"\)/);
  assert.match(evidence, /metricStatus\(record, "cost\.", "cost\.execution_total"\)/);
});

test("test evidence is described as a named file, not proof that tests passed", () => {
  assert.match(text, /completion requires naming an existing test file as evidence/);
  assert.ok(!/A work item without it stays open/.test(text));
});

test("event attribution is not claimed for legacy events", () => {
  assert.ok(!/Each event carries the actor/.test(text));
  assert.match(text, /Events written by current versions carry the actor/);
});
