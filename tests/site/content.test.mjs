// Content contracts for the public page: the copy the specification fixes
// verbatim, the narrative order, and the claims the page must never make.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const html = readFileSync(new URL("../../site/index.html", import.meta.url), "utf8");
const text = html
  .replace(/<(script|style)[\s\S]*?<\/\1>/g, " ")
  .replace(/<[^>]+>/g, " ")
  .replace(/&amp;/g, "&")
  .replace(/&#39;|&rsquo;/g, "'")
  .replace(/\s+/g, " ");
const section = (id) => {
  const start = html.indexOf(`id="${id}"`);
  assert.ok(start >= 0, `section #${id} exists`);
  const end = html.indexOf("</section>", start);
  return html.slice(start, end);
};

test("hero states the claim and the demand", () => {
  const hero = section("top");
  assert.match(hero, /Echelon<\/span> <span>Foundry/);
  assert.match(hero, /class="hero__mark">Praxis</);
  assert.match(hero, /<h1[^>]*>\s*<span class="hero__claim">You said it's done\.<\/span>\s*<span class="hero__demand">Prove it\.<\/span>\s*<\/h1>/);
  assert.ok(text.includes("Praxis connects requirements, execution, agents, changes, tests, evidence, provenance, and cost into a verifiable engineering record."));
  assert.ok(text.includes("Trust is not an engineering control. Evidence is."));
  assert.match(hero, />See the evidence</);
  assert.match(hero, /href="https:\/\/github\.com\/kemiller2002\/praxis"[^>]*>View on GitHub</);
});

test("the page never borrows from the film it was inspired by", () => {
  ["Pulp Fiction", "Jules", "Winnfield", "Samuel L", "Ezekiel", "royale with cheese", "say what again", "motherf"].forEach((phrase) =>
    assert.ok(!text.toLowerCase().includes(phrase.toLowerCase()), phrase)
  );
});

test("the claim is complete without JavaScript", () => {
  const claim = section("verify");
  assert.ok(!/\bhidden\b/.test(claim), "no content is hidden in the static page");
  assert.equal((claim.match(/class="claim__step"/g) ?? []).length, 8);
  assert.match(claim, /class="claim__verdict"/);
  assert.match(claim, /Real record/);
  assert.match(html, /<script src="assets\/js\/claim\.js" defer><\/script>/);
});

test("the claim enhancement respects reduced motion and announces progress", () => {
  const js = readFileSync(new URL("../../site/assets/js/claim.js", import.meta.url), "utf8");
  assert.match(js, /prefers-reduced-motion: reduce/);
  assert.match(js, /role", "status"/);
  assert.match(js, /button\.type = "button"/);
  assert.ok(!/fetch\(|XMLHttpRequest|localStorage|document\.cookie/.test(js), "no network or storage");
});

test("the problem precedes the proposition, which precedes the verification", () => {
  const order = ["top", "problem", "proposition", "verify"].map((id) => html.indexOf(`id="${id}"`));
  assert.deepEqual([...order].sort((a, b) => a - b), order);
  assert.ok(text.includes("Agents can write code. Praxis makes their work accountable."));
});

test("the execution chain runs from requirement to verified history in order", () => {
  const chain = section("how");
  const names = [...chain.matchAll(/<h3 class="chain__name">([^<]+)<\/h3>/g)].map((m) => m[1]);
  assert.deepEqual(names, ["Requirement", "Work item", "Execution", "Steps", "Changes", "Tests", "Evidence", "Reconciliation", "Verified history"]);
});

test("every command the chain names exists in the CLI", () => {
  const cli = readFileSync(new URL("../../src/Ros.Cli/Program.fs", import.meta.url), "utf8");
  const chain = section("how");
  const verbs = [...chain.matchAll(/<code>([^<]+)<\/code>/g)].flatMap((m) => [...m[1].matchAll(/work ([a-z-]+)/g)].map((v) => v[1]));
  assert.ok(verbs.length >= 5);
  verbs.forEach((verb) => assert.ok(cli.includes(`"${verb}"`), `work ${verb}`));
});

test("Git and Praxis are compared as complements, in an accessible table", () => {
  const git = section("git");
  assert.match(git, /Git knows what\. <span class="accent">Praxis knows why\.<\/span>/);
  assert.match(git, /<caption/);
  assert.equal((git.match(/<th scope="row">/g) ?? []).length, 6);
  assert.equal((git.match(/<th scope="col">/g) ?? []).length, 3);
  ["Git is bad", "broken", "fails to", "inadequate", "outdated", "legacy"].forEach((phrase) => assert.ok(!git.toLowerCase().includes(phrase.toLowerCase()), phrase));
});

test("agent accountability distinguishes every quality of knowledge", () => {
  const agents = section("agents");
  ["Observed", "Derived", "Declared", "Unknown", "Unavailable"].forEach((quality) => assert.match(agents, new RegExp(`>${quality}<`), quality));
  assert.match(agents, /never mistaken for a detection/);
  assert.match(agents, /does not infer a model/);
});

test("the runtimes the page says are detected are the ones the CLI detects", () => {
  const identity = readFileSync(new URL("../../src/Ros.Domain/Telemetry/Identity.fs", import.meta.url), "utf8");
  [
    ["OpenAI Codex", "whitelisted-codex-environment"],
    ["Claude Code", "whitelisted-claude-environment"],
    ["Gemini CLI", "whitelisted-gemini-environment"],
    ["GitHub Copilot", "whitelisted-copilot-environment"],
    ["GitHub Actions", "whitelisted-github-actions-environment"],
  ].forEach(([name, mechanism]) => {
    assert.ok(section("agents").includes(name), name);
    assert.ok(identity.includes(mechanism), mechanism);
  });
});

test("the GH-84 handoff is rendered from real records only", () => {
  const evidence = section("evidence");
  assert.equal((evidence.match(/data-record="real"/g) ?? []).length, 2);
  assert.ok(!/illustrative/i.test(evidence));
  assert.match(evidence, /Praxis will not transfer identity merely because the work continued\./);
  const snapshot = JSON.parse(readFileSync(new URL("../../site/data/gh-84.json", import.meta.url), "utf8"));
  const gh84 = snapshot.executions.filter((execution) => execution.workItemId === "GH-84");
  assert.equal(gh84.length, 2);
  assert.equal(gh84[1].parentExecutionId, gh84[0].executionId, "second execution is the child of the first");
  gh84.forEach((execution) => assert.ok(evidence.includes(`data-evidence="${execution.executionId}:identity.provider"`)));
});

test("unattributed changes: the message shown is the one the CLI prints", () => {
  const reconciliation = section("reconciliation");
  const protocol = readFileSync(new URL("../../docs/work-protocol.md", import.meta.url), "utf8");
  const message = "meaningful change has no active or completed work-item attribution";
  assert.ok(reconciliation.includes(message));
  assert.ok(protocol.includes(message));
  ["Attributed", "Reconciled", "Unresolved"].forEach((state) => assert.match(reconciliation, new RegExp(`>${state}<`)));
  assert.match(reconciliation, /Manufactured attribution is worse than explicitly unresolved attribution\./);
});
