#!/usr/bin/env node
// Builds the static HTML documentation site that GitHub Pages publishes (see
// .github/workflows/pages.yml): the repository README as the home page, the
// root guides, docs/, and the canonical research and requirement records,
// rendered from Markdown to self-contained HTML pages plus one stylesheet.
//
// Links between published pages become links between the generated .html
// files; links to anything unpublished (source, workflows, work state) point
// at the file on GitHub, so the site never links to a missing page. Every
// link and asset reference is relative, so the site works under any base
// path (a project site's /REPO/ or a custom domain's root).
//
//   node scripts/build-pages-site.mjs OUTPUT_DIR [--repository OWNER/REPO] [--ref REF]
//
// --repository and --ref default to GITHUB_REPOSITORY and GITHUB_SHA.

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const ROOT_PAGES = ["README.md", "AGENTS.md", "PACKAGE-USAGE.md", "BOOTSTRAP.md", "SDE-MAP.md"];
const PUBLISHED_TREES = ["docs", "research", "requirements"];
const SITE_TITLE = "Praxis";

// ---- pure core -----------------------------------------------------------

/** The generated page for a published Markdown file. */
export const htmlPath = (repoPath) => (repoPath === "README.md" ? "index.html" : repoPath.replace(/\.md$/, ".html"));

const isExternal = (target) => /^(?:[a-z][a-z0-9+.-]*:|\/\/|#)/i.test(target);

/** A relative URL from the page at `fromPage` to the site path `toPath`. */
export const relativeUrl = (fromPage, toPath) =>
  path.posix.relative(path.posix.dirname(fromPage), toPath) || path.posix.basename(toPath);

/**
 * Rewrites the relative Markdown link targets in `text` (the file at
 * `repoPath`): published Markdown targets become their generated `.html`
 * page; anything else in the repository becomes its GitHub source URL.
 * Anchors are kept; absolute URLs, pure anchors, and targets outside the
 * repository are untouched.
 */
export function rewriteLinks(text, repoPath, published, sourceUrl) {
  const directory = path.posix.dirname(repoPath);
  const page = htmlPath(repoPath);

  return text.replace(/(\]\()([^)\s]+)(\))/g, (match, open, target, close) => {
    if (isExternal(target)) return match;
    const [file, anchor = ""] = target.split(/(?=#)/);
    const resolved = path.posix.normalize(path.posix.join(directory, decodeURI(file)));
    if (resolved.startsWith("..")) return match;
    const url = published.has(resolved) ? relativeUrl(page, htmlPath(resolved)) : sourceUrl(resolved);
    return `${open}${url}${anchor}${close}`;
  });
}

/** Splits leading YAML front matter from the Markdown body. */
export function splitFrontMatter(text) {
  const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?/.exec(text);
  return match ? { frontMatter: match[1], body: text.slice(match[0].length) } : { frontMatter: "", body: text };
}

const unquote = (value) => value.trim().replace(/^(["'])(.*)\1$/, "$2");

/** Top-level `key: value` scalars of front matter (enough for id/title/status). */
export const frontMatterFields = (frontMatter) =>
  Object.fromEntries(
    frontMatter
      .split(/\r?\n/)
      .map((line) => /^([A-Za-z_][\w-]*):[ \t]+(.+)$/.exec(line))
      .filter(Boolean)
      .map(([, key, value]) => [key, unquote(value)])
  );

/** Page title: front-matter `title`, else the first heading, else the file name. */
export function pageTitle(fields, body, repoPath) {
  const heading = /^#{1,6}[ \t]+(.+?)[ \t#]*$/m.exec(body);
  return fields.title ?? heading?.[1]?.replace(/[`*_]/g, "") ?? path.posix.basename(repoPath, ".md");
}

/** GitHub-style heading anchor slugs, de-duplicated per page. */
export function slugger() {
  const seen = new Map();
  return (text) => {
    const base = text
      .replace(/<[^>]*>/g, "")
      .replace(/&(amp|lt|gt|quot|#39);/g, (entity, name) => ({ amp: "&", lt: "<", gt: ">", quot: '"', "#39": "'" })[name])
      .toLowerCase()
      .trim()
      .replace(/[^\p{L}\p{N}\s_-]/gu, "")
      .replace(/\s/g, "-");
    const count = seen.get(base) ?? 0;
    seen.set(base, count + 1);
    return count === 0 ? base : `${base}-${count}`;
  };
}

export const escapeHtml = (value) =>
  String(value).replace(/[&<>"']/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[character]);

const NAVIGATION = [
  ["Home", "index.html"],
  ["Agents", "AGENTS.html"],
  ["CLI", "docs/cli.html"],
  ["Provenance", "docs/agent-provenance.html"],
  ["All pages", "pages.html"]
];

/** The full HTML document for one page. */
export function layout({ page, title, meta = [], content, sourceLink }) {
  const to = (target) => escapeHtml(relativeUrl(page, target));
  const navigation = NAVIGATION.map(([label, target]) => `<a href="${to(target)}">${escapeHtml(label)}</a>`).join("\n        ");
  const metaLine = meta.length
    ? `\n      <p class="meta">${meta.map(([label, value]) => `<span><strong>${escapeHtml(label)}</strong> ${escapeHtml(value)}</span>`).join(" ")}</p>`
    : "";
  const pageTitleText = title === SITE_TITLE ? SITE_TITLE : `${title} | ${SITE_TITLE}`;

  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>${escapeHtml(pageTitleText)}</title>
    <link rel="stylesheet" href="${to("assets/site.css")}">
  </head>
  <body>
    <header class="site-header">
      <a class="site-title" href="${to("index.html")}">${SITE_TITLE}</a>
      <nav>
        ${navigation}
      </nav>
    </header>
    <main>${metaLine}
${content}
    </main>
    <footer class="site-footer">${sourceLink ? `<a href="${escapeHtml(sourceLink)}">View source on GitHub</a>` : ""}</footer>
  </body>
</html>
`;
}

/** The "All pages" index: every published page grouped by directory. */
export function pagesIndex(entries) {
  const groups = entries.reduce((result, entry) => {
    const directory = path.posix.dirname(entry.repoPath);
    const group = directory === "." ? "Overview" : directory;
    return { ...result, [group]: [...(result[group] ?? []), entry] };
  }, {});

  return Object.keys(groups)
    .sort((left, right) => (left === "Overview" ? -1 : right === "Overview" ? 1 : left.localeCompare(right)))
    .map((group) => {
      const items = groups[group]
        .map((entry) => `  <li><a href="${escapeHtml(entry.page)}">${escapeHtml(entry.title)}</a> <code>${escapeHtml(entry.repoPath)}</code></li>`)
        .join("\n");
      return `<h2>${escapeHtml(group)}</h2>\n<ul class="page-list">\n${items}\n</ul>`;
    })
    .join("\n");
}

export const STYLESHEET = `:root {
  --text: #1f2328; --muted: #59636e; --border: #d1d9e0; --accent: #0969da;
  --surface: #f6f8fa; --background: #ffffff;
  color-scheme: light dark;
}
@media (prefers-color-scheme: dark) {
  :root { --text: #e6edf3; --muted: #9198a1; --border: #3d444d; --accent: #4493f8; --surface: #151b23; --background: #0d1117; }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--background); color: var(--text);
  font: 16px/1.6 -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif; }
a { color: var(--accent); }
.site-header { display: flex; flex-wrap: wrap; gap: 1rem; align-items: baseline; justify-content: space-between;
  padding: 1rem 1.5rem; border-bottom: 1px solid var(--border); }
.site-title { font-weight: 700; font-size: 1.25rem; color: var(--text); text-decoration: none; }
.site-header nav { display: flex; flex-wrap: wrap; gap: 1rem; }
main { max-width: 56rem; margin: 0 auto; padding: 1.5rem; overflow-wrap: anywhere; }
.meta { color: var(--muted); font-size: 0.9rem; display: flex; flex-wrap: wrap; gap: 1rem; }
h1, h2, h3, h4 { line-height: 1.25; margin-top: 1.75em; }
h1 { border-bottom: 1px solid var(--border); padding-bottom: 0.3em; }
code, pre { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: 0.875em; }
code { background: var(--surface); padding: 0.1em 0.35em; border-radius: 4px; }
pre { background: var(--surface); padding: 1rem; border-radius: 6px; overflow-x: auto; }
pre code { background: none; padding: 0; }
blockquote { margin: 0; padding: 0 1rem; color: var(--muted); border-left: 4px solid var(--border); }
table { border-collapse: collapse; display: block; overflow-x: auto; }
th, td { border: 1px solid var(--border); padding: 0.4rem 0.75rem; text-align: left; vertical-align: top; }
th { background: var(--surface); }
.page-list code { color: var(--muted); background: none; }
.site-footer { max-width: 56rem; margin: 2rem auto; padding: 1rem 1.5rem; border-top: 1px solid var(--border); font-size: 0.9rem; }
`;

// ---- effects -------------------------------------------------------------

const listMarkdown = (root, tree) => {
  const directory = path.join(root, tree);
  if (!fs.existsSync(directory)) return [];
  return fs
    .readdirSync(directory, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith(".md") && !entry.name.startsWith("."))
    .map((entry) => path.relative(root, path.join(entry.parentPath ?? entry.path, entry.name)).split(path.sep).join("/"));
};

export function publishedFiles(root) {
  return [
    ...ROOT_PAGES.filter((file) => fs.existsSync(path.join(root, file))),
    ...PUBLISHED_TREES.flatMap((tree) => listMarkdown(root, tree))
  ].sort();
}

/** A Markdown renderer with GitHub-style heading anchors. `marked` is loaded
 * only here, so the pure helpers above need no installed dependencies. */
async function markdownRenderer() {
  const { Marked } = await import("marked");

  return (markdown) => {
    const slug = slugger();
    const marked = new Marked({
      gfm: true,
      renderer: {
        heading({ tokens, depth }) {
          const inner = this.parser.parseInline(tokens);
          return `<h${depth} id="${escapeHtml(slug(inner))}">${inner}</h${depth}>\n`;
        }
      }
    });
    return marked.parse(markdown);
  };
}

export async function buildSite({ root, output, repository, ref }) {
  const render = await markdownRenderer();
  const files = publishedFiles(root);
  const published = new Set(files);
  const sourceUrl = (repoPath) => `https://github.com/${repository}/blob/${ref}/${repoPath}`;

  fs.rmSync(output, { recursive: true, force: true });

  const write = (sitePath, content) => {
    const target = path.join(output, sitePath);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, content);
  };

  const entries = files.map((repoPath) => {
    const { frontMatter, body } = splitFrontMatter(fs.readFileSync(path.join(root, repoPath), "utf8"));
    const fields = frontMatterFields(frontMatter);
    const page = htmlPath(repoPath);
    const title = repoPath === "README.md" ? SITE_TITLE : pageTitle(fields, body, repoPath);
    const meta = [
      ["ID", fields.id],
      ["Status", fields.status],
      ["Updated", fields.updated]
    ].filter(([, value]) => value);

    write(
      page,
      layout({
        page,
        title,
        meta,
        content: render(rewriteLinks(body, repoPath, published, sourceUrl)),
        sourceLink: sourceUrl(repoPath)
      })
    );

    return { repoPath, page, title };
  });

  write(
    "pages.html",
    layout({ page: "pages.html", title: "All pages", content: `<h1 id="all-pages">All pages</h1>\n${pagesIndex(entries)}` })
  );
  write("assets/site.css", STYLESHEET);
  write(".nojekyll", "");

  return entries;
}

// ---- command line ----------------------------------------------------------

const option = (args, name) => {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : undefined;
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const output = args.find((arg, index) => !arg.startsWith("--") && !args[index - 1]?.startsWith("--"));
  const repository = option(args, "--repository") ?? process.env.GITHUB_REPOSITORY;
  const ref = option(args, "--ref") ?? process.env.GITHUB_SHA ?? "main";

  if (!output || !repository) {
    console.error("usage: node scripts/build-pages-site.mjs OUTPUT_DIR [--repository OWNER/REPO] [--ref REF]");
    process.exit(2);
  }

  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
  const entries = await buildSite({ root, output: path.resolve(output), repository, ref });
  console.log(`built ${entries.length} page(s) into ${output}`);
}
