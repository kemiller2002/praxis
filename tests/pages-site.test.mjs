import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  escapeHtml,
  frontMatterFields,
  htmlPath,
  layout,
  pageTitle,
  pagesIndex,
  publishedFiles,
  relativeUrl,
  rewriteLinks,
  slugger,
  splitFrontMatter
} from "../scripts/build-pages-site.mjs";

// Pure helpers of the documentation-site builder. They need no installed
// dependencies, so they run in `npm test`; rendering (which needs `marked`)
// is covered by tests/pages-render.test.mjs in the Pages workflow.

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceUrl = (file) => `https://github.com/o/r/blob/abc/${file}`;
const published = new Set(["README.md", "AGENTS.md", "docs/cli.md", "docs/00-governance/README.md"]);

test("published Markdown maps to HTML pages; the README is the home page", () => {
  assert.equal(htmlPath("README.md"), "index.html");
  assert.equal(htmlPath("docs/cli.md"), "docs/cli.html");
  assert.equal(htmlPath("docs/00-governance/README.md"), "docs/00-governance/README.html");
  assert.equal(relativeUrl("docs/cli.html", "index.html"), "../index.html");
  assert.equal(relativeUrl("index.html", "assets/site.css"), "assets/site.css");
  assert.equal(relativeUrl("docs/cli.html", "docs/cli.html"), "cli.html");
});

test("links between published pages point at the generated HTML, keeping anchors", () => {
  assert.equal(rewriteLinks("[cli](docs/cli.md#exit-codes)", "README.md", published, sourceUrl), "[cli](docs/cli.html#exit-codes)");
  assert.equal(rewriteLinks("[home](../README.md#install)", "docs/cli.md", published, sourceUrl), "[home](../index.html#install)");
  assert.equal(rewriteLinks("[agents](../AGENTS.md)", "docs/cli.md", published, sourceUrl), "[agents](../AGENTS.html)");
  assert.equal(rewriteLinks("[gov](00-governance/README.md)", "docs/cli.md", published, sourceUrl), "[gov](00-governance/README.html)");
});

test("links to unpublished repository files point at their GitHub source", () => {
  assert.equal(rewriteLinks("[wf](.github/workflows/publish.yml)", "README.md", published, sourceUrl), "[wf](https://github.com/o/r/blob/abc/.github/workflows/publish.yml)");
  assert.equal(rewriteLinks("[m](../telemetry/metrics.json#L3)", "docs/cli.md", published, sourceUrl), "[m](https://github.com/o/r/blob/abc/telemetry/metrics.json#L3)");
  assert.equal(rewriteLinks("[l](LICENSE)", "README.md", published, sourceUrl), "[l](https://github.com/o/r/blob/abc/LICENSE)");
});

test("absolute URLs, anchors, and links above the repository root are untouched", () => {
  for (const text of ["[a](https://example.com/x.md)", "[b](#section)", "[c](mailto:x@example.com)", "[d](../../outside.md)"]) {
    assert.equal(rewriteLinks(text, "docs/cli.md", published, sourceUrl), text);
  }
});

test("front matter is split from the body and its scalars read for title and metadata", () => {
  const { frontMatter, body } = splitFrontMatter('---\nid: RQ-1\ntitle: "Quoted: title"\ntags: [a, b]\nprovenance:\n  x: y\n---\n\n# Heading\n');
  assert.deepEqual(frontMatterFields(frontMatter), { id: "RQ-1", title: "Quoted: title", tags: "[a, b]" });
  assert.equal(body, "\n# Heading\n");
  assert.deepEqual(splitFrontMatter("# No front matter\n"), { frontMatter: "", body: "# No front matter\n" });
  assert.equal(pageTitle({ title: "From front matter" }, "# Heading", "x.md"), "From front matter");
  assert.equal(pageTitle({}, "Intro\n\n## The `cli` reference\n", "x.md"), "The cli reference");
  assert.equal(pageTitle({}, "no heading", "docs/notes.md"), "notes");
});

test("heading slugs follow GitHub's rules and de-duplicate", () => {
  const slug = slugger();
  assert.equal(slug("Exit codes"), "exit-codes");
  assert.equal(slug("<code>status --json</code>"), "status---json");
  assert.equal(slug("Identity &amp; execution"), "identity--execution");
  assert.equal(slug("Exit codes"), "exit-codes-1");
});

test("pages escape their titles and link the stylesheet relatively", () => {
  const html = layout({ page: "docs/a/b.html", title: "<A & B>", content: "<p>x</p>", sourceLink: "https://github.com/o/r/blob/abc/docs/a/b.md" });
  assert.match(html, /<title>&lt;A &amp; B&gt; \| Praxis<\/title>/);
  assert.match(html, /href="\.\.\/\.\.\/assets\/site\.css"/);
  assert.match(html, /href="\.\.\/\.\.\/index\.html"/);
  assert.equal(escapeHtml(`"'`), "&quot;&#39;");
});

test("the all-pages index groups pages by directory with the overview first", () => {
  const index = pagesIndex([
    { repoPath: "docs/cli.md", page: "docs/cli.html", title: "CLI" },
    { repoPath: "AGENTS.md", page: "AGENTS.html", title: "Agents" }
  ]);
  assert.ok(index.indexOf("<h2>Overview</h2>") < index.indexOf("<h2>docs</h2>"));
  assert.match(index, /<a href="docs\/cli.html">CLI<\/a>/);
});

test("the published set is the README, root guides, docs, research, and requirements -- never source or work state", () => {
  const files = publishedFiles(repositoryRoot);
  assert.ok(files.includes("README.md") && files.includes("docs/cli.md"));
  assert.ok(files.some((file) => file.startsWith("research/decisions/")));
  assert.ok(files.every((file) => /^(README|AGENTS|PACKAGE-USAGE|BOOTSTRAP|SDE-MAP)\.md$|^(docs|research|requirements)\/.+\.md$/.test(file)), files.join("\n"));
});
