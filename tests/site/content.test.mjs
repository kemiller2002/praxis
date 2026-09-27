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
