// PRAXIS-SITE-18: layout rules that keep the page reflowing from 320px up.
// Rendered evidence at 320/375/768/1280/1920 is in docs/site/responsive.md.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

const css = readFileSync(new URL("../../site/assets/css/site.css", import.meta.url), "utf8");
const html = readFileSync(new URL("../../site/index.html", import.meta.url), "utf8");

test("no fixed width can force a 320px viewport to scroll", () => {
  const widths = [...css.matchAll(/(?<![-\w(])(?:width|min-width):\s*(\d+(?:\.\d+)?)(px|rem)/g)].map(([whole, n, unit]) => ({ whole, px: unit === "rem" ? Number(n) * 16 : Number(n) }));
  const inRules = widths.filter(({ px }) => px > 320);
  // The only wide minimum is the ledger table, which scrolls inside its own region.
  assert.deepEqual(inRules.map(({ whole }) => whole), ["min-width: 34rem"]);
  assert.match(css, /\.ledger \{\s*overflow-x: auto;/);
});

test("the viewport is declared and the layout has intentional breakpoints", () => {
  assert.match(html, /<meta name="viewport" content="width=device-width, initial-scale=1">/);
  const breakpoints = new Set([...css.matchAll(/@media \((?:min|max)-width: ([\d.]+rem)\)/g)].map(([, value]) => value));
  ["30rem", "48rem", "60rem", "72rem"].forEach((value) => assert.ok(breakpoints.has(value) || breakpoints.has("47.99rem"), value));
});

test("navigation needs no script and wraps instead of hiding", () => {
  assert.match(css, /\.nav-list \{[^}]*flex-wrap: wrap;/);
  assert.ok(!/\.nav-list[^{]*\{[^}]*display: none/.test(css));
});

test("long identifiers wrap instead of overflowing", () => {
  ["\\.record__rows dd", "\\.claim__a", "\\.terminal", "\\.record__steps li > span:first-child"].forEach((selector) =>
    assert.match(css, new RegExp(`${selector} \\{[^}]*overflow-wrap: anywhere;`), selector)
  );
});
