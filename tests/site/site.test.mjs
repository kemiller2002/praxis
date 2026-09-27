// Public site checks (docs/public-site.md, GH-84). Dependency-free.
import { test } from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { checkSite } from "../../scripts/site/check.mjs";
import { accessibilityProblems, boundaryProblems, referenceProblems, structureProblems } from "../../scripts/site/lib.mjs";
import { resolveRequest } from "../../scripts/site/serve.mjs";

const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..", "site");

test("PRAXIS-SITE-01: the committed public site passes every site check", () => {
  assert.deepEqual(checkSite(siteRoot), []);
});

test("PRAXIS-SITE-01: the preview server never serves outside site/", () => {
  assert.equal(resolveRequest(siteRoot, "/"), path.join(siteRoot, "index.html"));
  assert.equal(resolveRequest(siteRoot, "/assets/css/site.css"), path.join(siteRoot, "assets", "css", "site.css"));
  assert.equal(resolveRequest(siteRoot, "/../package.json"), path.join(siteRoot, "package.json"));
  // URL parsing already collapses dot segments; encoded slashes are the
  // remaining escape route, and the server refuses them.
  assert.ok(resolveRequest(siteRoot, "/%2e%2e/%2e%2e/etc/passwd").startsWith(siteRoot + path.sep));
  assert.equal(resolveRequest(siteRoot, "/..%2f..%2fetc%2fpasswd"), null);
});

test("PRAXIS-SITE-01: the boundary check rejects loopback hosts, operational API paths, local paths, session ids and credentials", () => {
  assert.deepEqual(boundaryProblems("x", "see https://example.com/praxis"), []);
  for (const text of [
    "fetch('http://127.0.0.1:4310/state')",
    "http://localhost:4310",
    "fetch('/api/work')",
    "path /home/user/praxis",
    '"sessionId": "abc"',
    "670d8549-5656-53b1-9d67-a781eee8c6f3",
    "token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ012345",
  ])
    assert.notDeepEqual(boundaryProblems("x", text), [], text);
});

test("PRAXIS-SITE-01: references must be relative and resolve", () => {
  const exists = (file) => file === "assets/css/site.css";
  assert.deepEqual(referenceProblems('<link href="assets/css/site.css"><a href="https://github.com/x">x</a><a href="#main">m</a>', exists), []);
  assert.equal(referenceProblems('<link href="/assets/css/site.css">', exists).length, 1);
  assert.equal(referenceProblems('<img src="missing.png" alt="">', exists).length, 1);
});

test("PRAXIS-SITE-17: structural and accessibility rules catch common defects", () => {
  assert.equal(structureProblems("<main><section></main>").length > 0, true);
  assert.deepEqual(structureProblems("<main><p>ok<br></p><img src='a' alt=''></main>"), []);
  const page = (body) =>
    `<!doctype html><html lang="en"><head><title>t</title></head><body><a href="#main">Skip</a><header><nav><a href="#main">x</a></nav></header><main id="main">${body}</main><footer></footer></body></html>`;
  assert.deepEqual(accessibilityProblems(page("<h1>A</h1><h2>B</h2>")), []);
  assert.match(accessibilityProblems(page("<h1>A</h1><h3>B</h3>")).join(), /jumps/);
  assert.match(accessibilityProblems(page("<h1>A</h1><a href='x'>click here</a>")).join(), /not meaningful/);
  assert.match(accessibilityProblems(page("<h1>A</h1><button></button>")).join(), /accessible name/);
  assert.match(accessibilityProblems(page("<h1>A</h1><a href='#nowhere'>x</a>")).join(), /no target/);
});
