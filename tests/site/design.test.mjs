// PRAXIS-SITE-02: the Echelon Foundry tokens and the text/background pairs
// the stylesheet actually uses must meet WCAG 2.2 AA contrast.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { contrast } from "../../site-tools/lib.mjs";

const css = readFileSync(new URL("../../site/assets/css/site.css", import.meta.url), "utf8");
const html = readFileSync(new URL("../../site/index.html", import.meta.url), "utf8");

const tokens = Object.fromEntries(
  [...css.matchAll(/--([a-z-]+):\s*(#[0-9a-f]{6});/g)].map(([, name, value]) => [name, value])
);

const core = {
  parchment: "#f2efe7",
  charcoal: "#202421",
  "forged-iron": "#3a403c",
  "oxide-bronze": "#905831",
  verdigris: "#47756b",
  carbon: "#171a18",
  graphite: "#686d68",
  stone: "#e3e0d7",
};

test("core Echelon Foundry tokens are defined exactly", () => {
  Object.entries(core).forEach(([name, value]) => assert.equal(tokens[name], value, name));
});

// [foreground, background, minimum ratio]: 4.5 for body text, 3 for
// non-text indicators such as focus outlines.
const pairs = [
  ["charcoal", "parchment", 4.5],
  ["charcoal", "stone", 4.5],
  ["forged-iron", "parchment", 4.5],
  ["oxide-bronze", "parchment", 4.5],
  ["verdigris", "parchment", 4.5],
  ["graphite", "parchment", 4.5],
  ["parchment", "carbon", 4.5],
  ["stone", "carbon", 4.5],
  ["parchment", "charcoal", 4.5],
  ["parchment", "forged-iron", 4.5],
  ["carbon", "parchment", 4.5],
  ["bronze-on-dark", "carbon", 4.5],
  ["verdigris-on-dark", "carbon", 4.5],
  ["graphite-on-dark", "carbon", 4.5],
  ["oxide-bronze", "parchment", 3],
  ["oxide-bronze", "stone", 3],
  ["bronze-on-dark", "carbon", 3],
];

pairs.forEach(([fg, bg, min]) =>
  test(`${fg} on ${bg} meets ${min}:1`, () => {
    const ratio = contrast(tokens[fg], tokens[bg]);
    assert.ok(ratio >= min, `${fg} on ${bg} is ${ratio.toFixed(2)}:1`);
  })
);

test("geometry stays square: no border-radius other than zero", () => {
  const radii = [...css.matchAll(/border-radius:\s*([^;]+);/g)].map(([, v]) => v.trim());
  assert.deepEqual(radii.filter((v) => v !== "0"), []);
});

test("no forbidden visual effects", () => {
  ["linear-gradient", "radial-gradient", "backdrop-filter", "box-shadow"].forEach((effect) =>
    assert.ok(!css.includes(effect), effect)
  );
});

test("every font family has a local fallback and no font binaries are referenced", () => {
  ["--font-display", "--font-body", "--font-mono"].forEach((name) => {
    const stack = css.match(new RegExp(`${name}:([^;]+);`))[1];
    assert.match(stack, /(serif|sans-serif|monospace)\s*$/, name);
  });
  assert.ok(!/\.(woff2?|ttf|otf)\b/.test(css), "font binaries referenced from CSS");
  assert.match(html, /fonts\.googleapis\.com\/css2\?[^"]*display=swap/);
});

test("reduced motion is respected", () => {
  assert.match(css, /@media \(prefers-reduced-motion: reduce\)/);
});

test("focus is always visible", () => {
  assert.match(css, /:focus-visible\s*\{\s*outline:/);
});

test("small labels on stone use a colour that meets AA there", () => {
  assert.match(css, /\.section--stone \.kicker \{\s*color: var\(--forged-iron\);/);
  assert.ok(contrast(tokens["forged-iron"], tokens.stone) >= 4.5);
});
