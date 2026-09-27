#!/usr/bin/env node
// Local preview server for the public site (docs/public-site.md).
// Serves only site/, binds to loopback, and refuses path traversal.
// Development convenience only; deployment is static hosting.

import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const siteRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..", "site");

const types = new Map([
  [".html", "text/html; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
  [".svg", "image/svg+xml"],
  [".png", "image/png"],
  [".ico", "image/x-icon"],
  [".txt", "text/plain; charset=utf-8"],
]);

const argValue = (name, fallback) => {
  const index = process.argv.indexOf(name);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
};

export const resolveRequest = (root, url) => {
  const pathname = decodeURIComponent(new URL(url, "http://localhost").pathname);
  const relative = pathname.endsWith("/") ? `${pathname}index.html` : pathname;
  const full = path.resolve(root, `.${relative}`);
  return full === root || full.startsWith(root + path.sep) ? full : null;
};

const handler = (request, response) => {
  const file = resolveRequest(siteRoot, request.url ?? "/");
  const found = file !== null && fs.existsSync(file) && fs.statSync(file).isFile();
  if (!found) {
    response.writeHead(file === null ? 403 : 404, { "content-type": "text/plain; charset=utf-8" });
    response.end(file === null ? "Forbidden\n" : "Not found\n");
    return;
  }
  response.writeHead(200, {
    "content-type": types.get(path.extname(file)) ?? "application/octet-stream",
    "cache-control": "no-store",
  });
  fs.createReadStream(file).pipe(response);
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const port = Number(argValue("--port", "4173"));
  http.createServer(handler).listen(port, "127.0.0.1", () => {
    console.log(`Praxis public site: http://127.0.0.1:${port}/ (serving ${path.relative(process.cwd(), siteRoot) || "."})`);
  });
}
