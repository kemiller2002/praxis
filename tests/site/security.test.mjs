// PRAXIS-SITE-20: security and privacy properties of the published page.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { checkSite } from "../../site-tools/check.mjs";

const html = readFileSync(new URL("../../site/index.html", import.meta.url), "utf8");
const csp = html.match(/<meta http-equiv="Content-Security-Policy" content="([^"]+)">/)?.[1];
const directives = Object.fromEntries((csp ?? "").split(";").map((part) => part.trim().split(/\s+/)).map(([name, ...values]) => [name, values]));

test("a strict content security policy is declared", () => {
  assert.ok(csp, "CSP meta present");
  assert.deepEqual(directives["default-src"], ["'none'"]);
  assert.deepEqual(directives["base-uri"], ["'none'"]);
  assert.deepEqual(directives["form-action"], ["'none'"]);
  assert.ok(!csp.includes("'unsafe-inline'") && !csp.includes("'unsafe-eval'") && !csp.includes("*"), csp);
  assert.deepEqual(directives["style-src"], ["'self'", "https://fonts.googleapis.com"]);
  assert.deepEqual(directives["font-src"], ["https://fonts.gstatic.com"]);
});

test("every inline handler is allowed by hash, and only by hash", () => {
  const handlers = [...html.matchAll(/\son[a-z]+="([^"]*)"/g)].map(([, code]) => code);
  assert.deepEqual(handlers, ["this.media='all'"]);
  handlers.forEach((code) => {
    const hash = `'sha256-${createHash("sha256").update(code).digest("base64")}'`;
    assert.ok(directives["script-src"].includes(hash), `hash for ${code}`);
  });
  assert.ok(!/<script(?![^>]*\bsrc=)[^>]*>/.test(html), "no inline script blocks");
});

test("external links are https to known hosts and never open new windows", () => {
  const hosts = new Set(["github.com", "raw.githubusercontent.com", "fonts.googleapis.com", "fonts.gstatic.com"]);
  [...html.matchAll(/\b(?:href|src)="(https?:[^"]+)"/g)].forEach(([, url]) => {
    const parsed = new URL(url.replace(/&amp;/g, "&"));
    assert.equal(parsed.protocol, "https:", url);
    assert.ok(hosts.has(parsed.host), parsed.host);
  });
  assert.ok(!/target="_blank"/.test(html));
  assert.match(html, /<meta name="referrer" content="strict-origin-when-cross-origin">/);
});

test("nothing private crosses the public boundary", () => {
  assert.deepEqual(checkSite(), []);
  ["127.0.0.1", "localhost", "4310", "4320", "/api/", "sessionId", "session_"].forEach((marker) => assert.ok(!html.includes(marker), marker));
});
