// Pure checks for the public site (docs/public-site.md). No dependencies:
// the site is small, static, and hand-written, so a strict tokenizer is
// enough and keeps the repository free of a validator dependency tree.

const VOID = new Set(["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"]);

// Tokenizes HTML into start/end tags with attributes, skipping comments,
// doctype, and the raw text inside <script> and <style>.
export const tokenize = (html) => {
  const tokens = [];
  const pattern = /<!--[\s\S]*?-->|<!doctype[^>]*>|<\/?([a-zA-Z][a-zA-Z0-9-]*)((?:\s+[^\s"'>\/=]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'=<>`]+))?)*)\s*(\/?)>/gi;
  let match;
  let rawUntil = null;
  while ((match = pattern.exec(html)) !== null) {
    const [whole, rawName, rawAttributes = "", selfClosing] = match;
    if (!rawName) continue;
    const name = rawName.toLowerCase();
    const closing = whole.startsWith("</");
    if (rawUntil && !(closing && name === rawUntil)) continue;
    rawUntil = null;
    const attributes = Object.fromEntries(
      [...rawAttributes.matchAll(/([^\s"'>\/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?/g)].map((a) => [
        a[1].toLowerCase(),
        a[2] ?? a[3] ?? a[4] ?? "",
      ]),
    );
    tokens.push({ name, closing, attributes, selfClosing: selfClosing === "/", index: match.index });
    if (!closing && (name === "script" || name === "style")) rawUntil = name;
  }
  return tokens;
};

const lineOf = (html, index) => html.slice(0, index).split("\n").length;

// Structural well-formedness: every non-void element is closed in order.
export const structureProblems = (html) => {
  const problems = [];
  const stack = [];
  for (const token of tokenize(html)) {
    if (VOID.has(token.name)) {
      if (token.closing) problems.push(`line ${lineOf(html, token.index)}: </${token.name}> closes a void element`);
      continue;
    }
    if (!token.closing) {
      if (!token.selfClosing) stack.push(token);
      continue;
    }
    const open = stack.pop();
    if (!open || open.name !== token.name)
      problems.push(`line ${lineOf(html, token.index)}: </${token.name}> does not close <${open ? open.name : "nothing"}>`);
  }
  stack.forEach((open) => problems.push(`line ${lineOf(html, open.index)}: <${open.name}> is never closed`));
  return problems;
};

const textBetween = (html, start, endName) => {
  const close = html.toLowerCase().indexOf(`</${endName}>`, start);
  return close < 0 ? "" : html.slice(start, close).replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim();
};

// Accessibility and semantics rules that can be decided from markup alone.
export const accessibilityProblems = (html) => {
  const tokens = tokenize(html);
  const starts = tokens.filter((t) => !t.closing);
  const problems = [];
  const html0 = starts.find((t) => t.name === "html");
  if (!html0 || !html0.attributes.lang) problems.push("<html> needs a lang attribute");
  if (!starts.some((t) => t.name === "title")) problems.push("document needs a <title>");
  ["main", "header", "footer", "nav"].forEach((landmark) => {
    if (!starts.some((t) => t.name === landmark)) problems.push(`missing <${landmark}> landmark`);
  });
  if (starts.filter((t) => t.name === "main").length > 1) problems.push("more than one <main>");
  const headings = starts.filter((t) => /^h[1-6]$/.test(t.name)).map((t) => Number(t.name[1]));
  if (headings.filter((level) => level === 1).length !== 1) problems.push("exactly one <h1> is required");
  headings.forEach((level, i) => {
    if (i > 0 && level > headings[i - 1] + 1) problems.push(`heading level jumps from h${headings[i - 1]} to h${level}`);
  });
  const ids = starts.filter((t) => t.attributes.id).map((t) => t.attributes.id);
  const duplicates = ids.filter((id, i) => ids.indexOf(id) !== i);
  if (duplicates.length) problems.push(`duplicate ids: ${[...new Set(duplicates)].join(", ")}`);
  const idSet = new Set(ids);
  starts
    .filter((t) => t.name === "a" && (t.attributes.href ?? "").startsWith("#") && t.attributes.href.length > 1)
    .forEach((t) => {
      if (!idSet.has(t.attributes.href.slice(1))) problems.push(`in-page link ${t.attributes.href} has no target`);
    });
  starts
    .filter((t) => t.attributes["aria-labelledby"])
    .forEach((t) =>
      t.attributes["aria-labelledby"].split(/\s+/).forEach((id) => {
        if (!idSet.has(id)) problems.push(`aria-labelledby references missing id ${id}`);
      }),
    );
  starts.filter((t) => t.name === "img" && !("alt" in t.attributes)).forEach((t) => problems.push(`<img src="${t.attributes.src}"> has no alt`));
  starts
    .filter((t) => t.name === "a" && t.attributes.target === "_blank" && !/noopener/.test(t.attributes.rel ?? ""))
    .forEach((t) => problems.push(`${t.attributes.href} opens a new window without rel="noopener"`));
  starts
    .filter((t) => t.name === "a" || t.name === "button")
    .forEach((t) => {
      const label = t.attributes["aria-label"] ?? textBetween(html, html.indexOf(">", t.index) + 1, t.name);
      if (!label) problems.push(`<${t.name}> at line ${lineOf(html, t.index)} has no accessible name`);
      if (/^(click here|here|read more|more|link)$/i.test(label)) problems.push(`link text "${label}" is not meaningful`);
    });
  if (!starts.some((t) => t.name === "a" && t.attributes.href === "#main")) problems.push("missing skip link to #main");
  return problems;
};

// Every reference must be relative and must resolve inside the site, so the
// same files work at a domain root and at a GitHub Pages project path.
export const referenceProblems = (html, exists) =>
  tokenize(html)
    .filter((t) => !t.closing)
    .flatMap((t) => ["href", "src"].filter((a) => a in t.attributes).map((a) => [t, t.attributes[a]]))
    .flatMap(([t, value]) => {
      if (/^(https?:|mailto:)/i.test(value) || value.startsWith("#")) return [];
      if (value.startsWith("/")) return [`<${t.name}> uses root-relative ${value}; use a relative path`];
      const file = value.split(/[?#]/)[0];
      return exists(file) ? [] : [`<${t.name}> references missing ${value}`];
    });

// The public site must never point at the operational Praxis HTTP adapter
// or at anything local, and must never carry credential-shaped values.
export const boundaryProblems = (name, text) => {
  const rules = [
    [/\b(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])\b/i, "loopback host"],
    [/\/api\//i, "operational API path"],
    [/(?:^|[\s"'(])\/(?:home|Users|root|tmp|var)\//, "local filesystem path"],
    [/[A-Za-z]:\\\\?(?:Users|home)/, "local filesystem path"],
    [/\b(?:sessionId|conversationId|runId)\b/, "session identifier field"],
    [/\bsk-[A-Za-z0-9_-]{16,}|\bgh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|-----BEGIN [A-Z ]*PRIVATE KEY-----/, "credential"],
    [/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i, "UUID (possible session identifier)"],
  ];
  return rules.filter(([pattern]) => pattern.test(text)).map(([, label]) => `${name}: contains a ${label}`);
};

// WCAG 2.x relative luminance and contrast ratio.
const channel = (value) => {
  const c = value / 255;
  return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
};
export const luminance = (hex) => {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
};
export const contrast = (a, b) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};
