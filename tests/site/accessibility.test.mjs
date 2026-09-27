// PRAXIS-SITE-17: accessibility rules that can be decided from the source.
// Rendered-page evidence (axe-core, keyboard traversal, text spacing, reflow)
// is recorded in docs/site/accessibility.md.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { accessibilityProblems } from "../../scripts/site/lib.mjs";

const html = readFileSync(new URL("../../site/index.html", import.meta.url), "utf8");
const css = readFileSync(new URL("../../site/assets/css/site.css", import.meta.url), "utf8");
const js = readFileSync(new URL("../../site/assets/js/claim.js", import.meta.url), "utf8");

test("the markup-level checks pass", () => {
  assert.deepEqual(accessibilityProblems(html), []);
});

test("scrollable regions can be reached and scrolled from the keyboard", () => {
  const scrollers = [...css.matchAll(/\.([a-z_-]+)\s*\{[^}]*overflow-x:\s*auto/g)].map(([, name]) => name);
  assert.ok(scrollers.length > 0);
  scrollers.forEach((name) => {
    const element = html.match(new RegExp(`<[a-z]+ class="${name}"[^>]*>`));
    assert.ok(element, name);
    assert.match(element[0], /tabindex="0"/, `${name} is focusable`);
    assert.match(element[0], /role="region"/, `${name} is a named region`);
    assert.match(element[0], /aria-labelledby="[^"]+"/, `${name} has a name`);
  });
});

test("aria-label is only used where the role allows it", () => {
  [...html.matchAll(/<(pre|div|span|p)\b[^>]*aria-label=/g)].forEach(([tag]) => assert.fail(`aria-label on a generic ${tag}`));
});

test("hover never reveals anything focus does not", () => {
  const hoverRules = [...css.matchAll(/([^{}]*:hover[^{]*)\{([^}]*)\}/g)];
  hoverRules.forEach(([, , body]) => assert.ok(!/display|visibility|opacity|height/.test(body), `hover-only change: ${body.trim()}`));
});

test("interactive controls meet the 44px target in the main flows", () => {
  [".button", ".nav-list a", ".principles__link a", ".site-footer__nav a"].forEach((selector) => {
    const rule = css.match(new RegExp(`${selector.replace(/\./g, "\\.")}\\s*\\{([^}]*)\\}`));
    assert.ok(rule, selector);
    assert.match(rule[1], /min-height:\s*(44|48)px/, selector);
  });
});

test("reduced motion removes motion rather than shortening it", () => {
  const block = css.match(/@media \(prefers-reduced-motion: reduce\) \{([\s\S]*?)\n\}/)[1];
  assert.match(block, /animation: none !important/);
  assert.match(block, /transition: none !important/);
  assert.match(block, /scroll-behavior: auto/);
});

test("the claim enhancement keeps the page operable", () => {
  assert.match(js, /setAttribute\("role", "status"\)/);
  assert.match(js, /button\.type = "button"/);
  assert.ok(!/tabindex|keydown|mouseover|mouseenter/.test(js), "no custom key handling or hover interaction");
});
