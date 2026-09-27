import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { buildSite } from "../scripts/build-pages-site.mjs";

// Builds the real documentation site to static HTML (needs `npm ci` for
// `marked`; run by .github/workflows/pages.yml) and checks that every
// internal link and anchor resolves.

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

const isExternal = (href) => /^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(href);
const unescape = (value) => value.replaceAll("&amp;", "&").replaceAll("&quot;", '"').replaceAll("&#39;", "'");

test("the documentation builds to static HTML whose every internal link and anchor resolves", async (t) => {
  const output = fs.mkdtempSync(path.join(os.tmpdir(), "praxis-pages-"));
  t.after(() => fs.rmSync(output, { recursive: true, force: true }));
  const entries = await buildSite({ root: repositoryRoot, output, repository: "o/r", ref: "abc" });

  assert.ok(entries.length > 50);
  for (const file of ["index.html", "pages.html", "assets/site.css", ".nojekyll", "docs/cli.html"]) {
    assert.ok(fs.existsSync(path.join(output, file)), file);
  }

  const pages = fs.readdirSync(output, { recursive: true }).filter((file) => file.endsWith(".html")).map((file) => path.join(output, file));
  const ids = new Map(pages.map((page) => [page, new Set([...fs.readFileSync(page, "utf8").matchAll(/ id="([^"]+)"/g)].map((match) => match[1]))]));

  const broken = pages.flatMap((page) =>
    [...fs.readFileSync(page, "utf8").matchAll(/(?:href|src)="([^"]+)"/g)]
      .map((match) => unescape(match[1]))
      .filter((href) => !isExternal(href))
      .filter((href) => {
        const [file, anchor] = href.split("#");
        const target = file ? path.resolve(path.dirname(page), decodeURI(file)) : page;
        if (!fs.existsSync(target)) return true;
        return Boolean(anchor) && target.endsWith(".html") && !ids.get(target)?.has(anchor);
      })
      .map((href) => `${path.relative(output, page)} -> ${href}`)
  );
  assert.deepEqual(broken, []);

  const cli = fs.readFileSync(path.join(output, "docs", "cli.html"), "utf8");
  assert.match(cli, /<title>CLI reference \| Praxis<\/title>/);
  assert.match(cli, /<h2 id="exit-codes">/);
  assert.match(cli, /<table>/);

  const home = fs.readFileSync(path.join(output, "index.html"), "utf8");
  assert.match(home, /href="docs\/cli\.html"/);
  assert.doesNotMatch(home, /href="[^"h][^"]*\.md[#"]/);
});
