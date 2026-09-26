// Contract tests for the Praxis provenance interchange block (praxis.provenance/1)
// and the cross-system end-to-end provenance scenario (RQ-ROS-2026-A013..A017,
// DF-ROS-2026-A037). The same fixtures are asserted by the F# implementation
// (tests/Ros.Tests/ProvenanceInterchangeTests.fs), so both sides of the shared
// contract reach the same verdicts.
import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import {
  classify, appendContribution, addLineage, preservationViolations, originator, modifiers,
  withRole, emptyBlock, actorFromEnvelopeV1, keyFromEnvelopeV1, foreignExecutionKey, foreignSystem,
  IDENTITY_ENVIRONMENT_VARIABLES, identityEnvironment, parseTimestamp,
} from "../lib/provenance-interchange.mjs";

const fixture = (name) => JSON.parse(readFileSync(new URL(`./fixtures/provenance-interchange/${name}`, import.meta.url), "utf8"));
const cases = fixture("cases.json").cases;
const chain = fixture("echelon-chain.json");

for (const item of cases) {
  test(`conformance: ${item.name} is ${item.expect}`, () => {
    const result = classify(item.block);
    assert.equal(result.verdict, item.expect, JSON.stringify(result.problems));
    assert.equal(result.warnings.length, item.warnings);
  });
}

test("classification never mutates its input", () => {
  for (const item of cases) {
    const before = JSON.stringify(item.block);
    classify(item.block);
    assert.equal(JSON.stringify(item.block), before);
  }
});

const A = { kind: "agent", id: "openai/codex", provider: "openai", model: "gpt-5-codex", runtime: "codex" };
const B = { kind: "agent", id: "anthropic/claude-code", provider: "anthropic", model: "unknown", runtime: "claude-code" };
const H = { kind: "human", id: "kevin" };
const t = (minute) => `2026-09-26T08:${String(minute).padStart(2, "0")}:00.000Z`;

test("appending never overwrites the originator or another contributor", () => {
  const created = appendContribution(emptyBlock(), "EXE-1", { operations: ["created"], at: t(0), actor: A });
  assert.ok(created.ok);
  const modified = appendContribution(created.block, "EXE-2", { operations: ["modified"], at: t(5), actor: B });
  assert.ok(modified.ok);
  assert.deepEqual(preservationViolations(created.block, modified.block), []);
  assert.equal(originator(modified.block).actor.id, "openai/codex");
  assert.deepEqual(modifiers(modified.block).map((item) => item.actor.id), ["anthropic/claude-code"]);
});

test("a second or late creator is refused", () => {
  const created = appendContribution(emptyBlock(), "EXE-1", { operations: ["created"], at: t(0), actor: A }).block;
  assert.equal(appendContribution(created, "EXE-2", { operations: ["created"], at: t(5), actor: B }).ok, false);
  const modifiedOnly = appendContribution(emptyBlock(), "EXE-1", { operations: ["modified"], at: t(0), actor: A }).block;
  assert.equal(appendContribution(modifiedOnly, "EXE-2", { operations: ["created"], at: t(5), actor: B }).ok, false);
});

test("an execution can never be re-attributed to another actor", () => {
  const created = appendContribution(emptyBlock(), "EXE-1", { operations: ["created"], at: t(0), actor: A }).block;
  const forged = appendContribution(created, "EXE-1", { operations: ["modified"], at: t(5), actor: B });
  assert.equal(forged.ok, false);
  assert.match(forged.error, /refusing to re-attribute/);
});

test("the same execution merges idempotently and advances last", () => {
  const created = appendContribution(emptyBlock(), "EXE-1", { operations: ["created"], at: t(0), actor: A }).block;
  const again = appendContribution(created, "EXE-1", { operations: ["created"], at: t(0), actor: A });
  assert.equal(again.changed, false);
  const later = appendContribution(created, "EXE-1", { operations: ["modified"], at: t(9), actor: A });
  assert.deepEqual(later.block.contributions["EXE-1"].operations, ["created", "modified"]);
  assert.equal(later.block.contributions["EXE-1"].last, t(9));
});

test("two executions of the same agent stay distinct", () => {
  const one = appendContribution(emptyBlock(), "EXE-1", { operations: ["created"], at: t(0), actor: A }).block;
  const two = appendContribution(one, "EXE-3", { operations: ["modified"], at: t(9), actor: A }).block;
  assert.deepEqual(Object.keys(two.contributions), ["EXE-1", "EXE-3"]);
});

test("unknown fields survive appends (room for attestation)", () => {
  const block = { ...emptyBlock(), "x-extra": { keep: true }, contributions: { "EXE-1": { operations: ["created"], at: t(0), actor: A, attestation: { sig: "abc" } } } };
  const next = appendContribution(block, "CTB-20260926-5f2e19aa", { operations: ["approved"], at: t(9), actor: H }).block;
  assert.deepEqual(next["x-extra"], { keep: true });
  assert.deepEqual(next.contributions["EXE-1"].attestation, { sig: "abc" });
  assert.deepEqual(preservationViolations(block, next), []);
});

test("unsupported and malformed blocks are never appended to", () => {
  const unsupported = cases.find((item) => item.name === "unsupported-major").block;
  assert.equal(appendContribution(unsupported, "EXE-1", { operations: ["created"], at: t(0), actor: A }).ok, false);
  const malformed = cases.find((item) => item.name === "two-creators").block;
  assert.equal(appendContribution(malformed, "EXE-9", { operations: ["modified"], at: t(59), actor: A }).ok, false);
});

test("preservation detects removal, overwrite, replaced history, and lost lineage", () => {
  const base = addLineage(appendContribution(appendContribution(emptyBlock(), "EXE-1", { operations: ["created"], at: t(0), actor: A }).block, "EXE-2", { operations: ["modified"], at: t(5), actor: B }).block, ["RQ-APP-2026-A001"]);
  assert.match(preservationViolations(base, { schema: base.schema }).join(), /provenance was removed/);
  const overwritten = JSON.parse(JSON.stringify(base));
  overwritten.contributions["EXE-1"].actor = B;
  assert.match(preservationViolations(base, overwritten).join(), /actor was overwritten/);
  const replaced = { ...base, contributions: { "EXE-9": { operations: ["created"], at: t(0), actor: B } } };
  assert.match(preservationViolations(base, replaced).join(), /removed/);
  const noLineage = { ...base, derivedFrom: [] };
  assert.match(preservationViolations(base, noLineage).join(), /lineage RQ-APP-2026-A001 was removed/);
  const unsupported = cases.find((item) => item.name === "unsupported-major").block;
  assert.match(preservationViolations(unsupported, { ...unsupported, whatever: false }).join(), /carried verbatim/);
});

test("lineage is recorded separately and never adds an author", () => {
  const created = appendContribution(emptyBlock(), "EXE-2", { operations: ["created"], at: t(0), actor: B }).block;
  const derived = addLineage(created, ["RQ-APP-2026-A001", "ordo:resolution/r-17"]);
  assert.deepEqual(Object.keys(derived.contributions), ["EXE-2"]);
  assert.deepEqual(derived.derivedFrom, ["RQ-APP-2026-A001", "ordo:resolution/r-17"]);
});

test("serialization round-trips byte-for-byte", () => {
  for (const item of cases.filter((entry) => entry.expect === "supported")) {
    const text = JSON.stringify(item.block);
    assert.equal(JSON.stringify(JSON.parse(text)), text);
    assert.equal(classify(JSON.parse(text)).verdict, "supported");
  }
});

test("foreign execution keys name their system and reject what they cannot carry", () => {
  assert.equal(foreignExecutionKey("dokimos", "run-7"), "EXT-dokimos.run-7");
  assert.equal(foreignSystem("EXT-ros-worker.3f2a.attempt-1"), "ros-worker");
  assert.throws(() => foreignExecutionKey("Dokimos", "run 7"));
});

test("echelon envelope v1 actors map to Praxis actors without invention", () => {
  const known = (value) => ({ state: "known", value });
  assert.deepEqual(
    actorFromEnvelopeV1({ kind: "agent", provider: known("openai"), identity: known("openai/codex") }),
    { kind: "agent", id: "openai/codex", provider: "openai", model: "unknown", runtime: "unknown" });
  assert.deepEqual(
    actorFromEnvelopeV1({ kind: "system", provider: { state: "unknown" }, identity: known("echelon/vigila") }),
    { kind: "automation", id: "echelon/vigila", provider: "unknown", model: "unknown", runtime: "unknown" });
  assert.deepEqual(
    actorFromEnvelopeV1({ kind: "human", provider: { state: "not-applicable" }, identity: known("kevin") }),
    { kind: "human", id: "kevin" });
  assert.equal(keyFromEnvelopeV1({ operationId: "op 1", actor: { runId: { state: "unknown" } } }), "EXT-op.op_201");
  assert.equal(keyFromEnvelopeV1({ operationId: "op-1", actor: { runId: known("gh/99") } }), "EXT-run.gh_2f99");
  // Injective (review finding): distinct ids never share a key.
  const keys = ["a/b", "a:b", "a_b", "a-b", "a_2fb"].map((operationId) => keyFromEnvelopeV1({ operationId, actor: {} }));
  assert.equal(new Set(keys).size, keys.length);
});

// ---- contract revision 1.1: adversarial-review regressions -------------------

test("an append never returns a block that classify rejects: credentials in the new contribution", () => {
  const created = appendContribution(emptyBlock(), "EXE-1", { operations: ["created"], at: t(0), actor: A }).block;
  const leaked = appendContribution(created, "CTB-20260926-5f2e19aa", { operations: ["reviewed"], at: t(5), actor: { kind: "human", id: "ghp_0123456789abcdefghijABCDEFGHIJ0123" } });
  assert.equal(leaked.ok, false);
  const reason = appendContribution(created, "EXE-2", { operations: ["modified"], at: t(5), actor: B, reason: "Bearer abcdefghijklmnopqrstuvwxyz012345" });
  assert.equal(reason.ok, false);
});

test("an append never returns a block that classify rejects: created merged into a later entry", () => {
  const history = appendContribution(appendContribution(emptyBlock(), "EXE-1", { operations: ["modified"], at: t(0), actor: A }).block,
    "EXE-2", { operations: ["modified"], at: t(5), actor: B }).block;
  assert.equal(appendContribution(history, "EXE-2", { operations: ["created"], at: t(6), actor: B }).ok, false);
});

test("an append never returns a block that classify rejects: a contribution dated before the creation", () => {
  const created = appendContribution(emptyBlock(), "EXE-1", { operations: ["created"], at: t(30), actor: A }).block;
  const backdated = appendContribution(created, "EXE-2", { operations: ["modified"], at: t(5), actor: B });
  assert.equal(backdated.ok, false);
  assert.match(backdated.error, /precedes the recorded creation/);
});

test("an unknown actor cannot extend an entry a known actor holds", () => {
  const created = appendContribution(emptyBlock(), "EXT-run.7", { operations: ["created"], at: t(0), actor: B }).block;
  const unknownAgent = { kind: "agent", id: "unknown", provider: "unknown", model: "unknown", runtime: "unknown" };
  assert.equal(appendContribution(created, "EXT-run.7", { operations: ["transformed"], at: t(5), actor: unknownAgent }).ok, false);
});

test("a same-key merge keeps the incoming unknown fields and its latest time", () => {
  const created = appendContribution(emptyBlock(), "CTB-1", { operations: ["created"], at: t(0), actor: H, evidence: ["e1"] }).block;
  const merged = appendContribution(created, "CTB-1", { operations: ["modified"], at: t(1), last: t(9), actor: H, "x-ticket": "T-9" });
  assert.ok(merged.ok);
  assert.equal(merged.block.contributions["CTB-1"]["x-ticket"], "T-9");
  assert.equal(merged.block.contributions["CTB-1"].last, t(9));
  assert.deepEqual(merged.block.contributions["CTB-1"].evidence, ["e1"]);
});

test("timestamps are calendar-valid and ordered at millisecond precision", () => {
  assert.equal(parseTimestamp("2026-02-30T00:00:00Z"), undefined);
  assert.equal(parseTimestamp("2026-09-26T24:00:00Z"), undefined);
  assert.equal(parseTimestamp("0000-01-01T00:00:00Z"), undefined);
  assert.equal(parseTimestamp("2026-09-26T08:00:00.0009Z"), parseTimestamp("2026-09-26T08:00:00.0001Z"));
  assert.equal(parseTimestamp("9999-12-31T23:59:59.999999999Z"), Date.UTC(9999, 11, 31, 23, 59, 59, 999));
});

test("the identity environment list is exactly what Praxis identity discovery reads", () => {
  const pinned = fixture("identity-environment.json").variables;
  assert.deepEqual([...IDENTITY_ENVIRONMENT_VARIABLES], pinned);
  const source = readFileSync(new URL("../src/Ros.Infrastructure/Work/FileTelemetryExecutionRepository.fs", import.meta.url), "utf8");
  const discovery = source.slice(source.indexOf("environmentIdentityInputs"), source.indexOf("resolveIdentity"));
  const read = new Set([...discovery.matchAll(/"([A-Z][A-Z0-9_]+)"/g)].map((match) => match[1]));
  for (const name of read) assert.ok(pinned.includes(name), `${name} is read by identity discovery but not pinned`);
  for (const name of pinned.filter((name) => name !== "ROS_EXECUTION_ID")) assert.ok(read.has(name), `${name} is pinned but not read`);
});

test("a child environment for another actor carries none of the launcher's identity", () => {
  const inherited = { PATH: "/bin", HOME: "/h", CLAUDE_CODE_SESSION_ID: "operator", GITHUB_RUN_ID: "77", ROS_ACTOR: "hub", ROS_TELEMETRY_SESSION_ID: "s" };
  const child = identityEnvironment(inherited, { ROS_ACTOR_KIND: "agent", ROS_TELEMETRY_PROVIDER: "openai", ROS_TELEMETRY_RUNTIME: "codex", ROS_TELEMETRY_MODEL: undefined });
  assert.deepEqual(child, { PATH: "/bin", HOME: "/h", ROS_ACTOR_KIND: "agent", ROS_TELEMETRY_PROVIDER: "openai", ROS_TELEMETRY_RUNTIME: "codex" });
});

// ---- cross-system end-to-end scenario --------------------------------------

const replay = () =>
  chain.steps.reduce((records, step) => {
    const current = records[step.record] ?? emptyBlock();
    if (step.lineage) return { ...records, [step.record]: addLineage(current, step.lineage) };
    const result = appendContribution(current, step.append.key, step.append.contribution);
    assert.ok(result.ok, `${step.record} ${step.append.key}: ${result.error}`);
    assert.deepEqual(preservationViolations(current, result.block), [], `${step.record} lost provenance at ${step.append.key}`);
    return { ...records, [step.record]: result.block };
  }, {});

test("end-to-end: every record keeps its own originator", () => {
  const records = replay();
  for (const [record, expected] of Object.entries(chain.expect.originators)) {
    const origin = originator(records[record]);
    assert.equal(origin.key, expected.key, record);
    assert.equal(origin.actor.id, expected.actorId, record);
  }
});

test("end-to-end: discoverer, generator, handler, validator, and reviewer stay distinct", () => {
  const records = replay();
  for (const [record, roles] of Object.entries(chain.expect.roles)) {
    for (const [role, keys] of Object.entries(roles)) {
      assert.deepEqual(withRole(records[record], role).map((item) => item.key), keys, `${record} ${role}`);
    }
  }
  const finding = records["aegis:finding/SF-0001"];
  const discoverer = withRole(finding, "discovered")[0].actor.id;
  const remediator = withRole(finding, "remediated")[0].actor.id;
  const validator = withRole(finding, "validated")[0].actor.id;
  assert.equal(new Set([discoverer, remediator, validator]).size, 3);
});

test("end-to-end: the measurement is not attributed to the code's author", () => {
  const records = replay();
  const author = originator(records["git:commit/5e1f0c2"]).actor.id;
  const measurer = withRole(records["dokimos:observation/OBS-2026-0001"], "measured")[0].actor.id;
  assert.notEqual(author, measurer);
});

test("end-to-end: lineage reconstructs the chain without merging authors", () => {
  const records = replay();
  const walk = (reference, seen = new Set()) =>
    seen.has(reference) || !records[reference] ? seen
      : (records[reference].derivedFrom ?? []).reduce((acc, next) => walk(next, acc), new Set([...seen, reference]));
  const reached = walk(chain.expect.lineageFrom);
  for (const reference of chain.expect.lineageReaches) assert.ok(reached.has(reference), reference);
  const originators = [...reached].map((reference) => originator(records[reference]).actor.id);
  assert.equal(originators.length, chain.expect.chainOriginatorCount);
  assert.ok(new Set(originators).size > 1, "no single actor authored the whole chain");
  for (const reference of reached) assert.equal(classify(records[reference]).verdict, "supported");
});

test("end-to-end: executions of one agent remain separate across records", () => {
  const records = replay();
  const { actorId, keys } = chain.expect.distinctExecutionsOfOneAgent;
  const found = new Set(Object.values(records).flatMap((block) =>
    Object.entries(block.contributions).filter(([, entry]) => entry.actor.id === actorId).map(([key]) => key)));
  assert.deepEqual([...found].sort(), [...keys].sort());
});

test("end-to-end: the final records answer metrics questions", () => {
  const records = replay();
  const facts = Object.entries(records).flatMap(([record, block]) =>
    Object.entries(block.contributions).flatMap(([key, entry]) => entry.operations.map((operation) => ({ record, key, operation, actor: entry.actor.id }))));
  const by = (operation) => facts.filter((fact) => fact.operation === operation).map((fact) => fact.actor);
  assert.deepEqual(by("discovered"), ["google/gemini-cli"]);
  assert.deepEqual(by("resolved"), ["openai/codex"]);
  assert.deepEqual(by("remediated"), ["openai/codex"]);
  assert.ok(facts.every((fact) => /^(EXE|EXT|CTB)-/.test(fact.key)), "every fact joins to an execution or contribution key");
});

test("end-to-end: the replayed records survive serialization", () => {
  const records = replay();
  const text = JSON.stringify(records);
  const parsed = JSON.parse(text);
  for (const [record, block] of Object.entries(records)) assert.deepEqual(preservationViolations(block, parsed[record]), []);
  assert.equal(createHash("sha256").update(JSON.stringify(parsed)).digest("hex"), createHash("sha256").update(text).digest("hex"));
});
