// PRAXIS-SITE-19: static-first budgets and third-party boundaries.
// Rendered measurements are recorded in docs/site/performance.md.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { gzipSync } from "node:zlib";
import path from "node:path";
import { fileURLToPath } from "node:url";

const site = fileURLToPath(new URL("../../site/", import.meta.url));
const html = readFileSync(path.join(site, "index.html"), "utf8");
const gz = (file) => gzipSync(readFileSync(path.join(site, file)), { level: 9 }).length;

test("first-party transfer stays inside budget", () => {
  const budgets = { "index.html": 20_000, "assets/css/site.css": 10_000, "assets/js/claim.js": 2_000 };
  Object.entries(budgets).forEach(([file, limit]) => assert.ok(gz(file) <= limit, `${file}: ${gz(file)} gzip bytes > ${limit}`));
});

test("the page loads exactly one script, its own, deferred", () => {
  const scripts = [...html.matchAll(/<script\b[^>]*>/g)].map(([tag]) => tag);
  assert.deepEqual(scripts, ['<script src="assets/js/claim.js" defer>']);
});

test("the only third party is Google Fonts, loaded without blocking render", () => {
  const external = [...html.matchAll(/<link\b[^>]*href="(https?:\/\/[^"]+)"[^>]*>/g)].map(([tag, href]) => ({ tag, host: new URL(href.replace(/&amp;/g, "&")).host }));
  external.forEach(({ host }) => assert.ok(["fonts.googleapis.com", "fonts.gstatic.com"].includes(host), host));
  const stylesheet = external.find(({ tag }) => tag.includes('rel="stylesheet"'));
  assert.match(stylesheet.tag, /media="print" onload="this\.media='all'"/);
  // No <noscript> blocking fallback: without JavaScript the stylesheet may still be
  // fetched (browsers download print stylesheets at low priority) but is never
  // applied, so no-JS visitors render with the local fallback fonts.
  assert.ok(!/<noscript>/.test(html), "no render-blocking font fallback");
});

test("no analytics, trackers, or embedded frames", () => {
  ["googletagmanager", "google-analytics", "gtag(", "plausible", "segment.", "hotjar", "<iframe", "facebook", "doubleclick"].forEach((marker) =>
    assert.ok(!html.includes(marker), marker)
  );
});

test("no images, fonts or bundles are shipped", () => {
  const walk = (dir) => readdirSync(dir).flatMap((name) => (statSync(path.join(dir, name)).isDirectory() ? walk(path.join(dir, name)) : [name]));
  const files = walk(site);
  assert.ok(!files.some((name) => /\.(woff2?|ttf|otf|png|jpe?g|gif|webp|avif|map)$/.test(name)), files.join(", "));
  assert.ok(!files.some((name) => /bundle|chunk|vendor/.test(name)));
});
