#!/usr/bin/env node
// Public GH-84 evidence snapshot for the site (docs/public-site.md).
//
//   node site-tools/evidence.mjs            write site/data/gh-84.json from .ros records
//   node site-tools/evidence.mjs --render   regenerate the ledger and data-evidence values in site/index.html
//   node site-tools/evidence.mjs --check    verify the snapshot against the records and
//                                             every data-evidence value in site/*.html
//
// The snapshot publishes only fields the public/private evidence boundary allows:
// work item IDs, titles and states; execution IDs, status and times; the recorded
// actor kind, provider, runtime and model; parent links; branch and short commit;
// and whether token and cost metrics were recorded. Session, conversation and run
// IDs, local paths, environment values and raw telemetry are never copied.
//
// Records keep moving after a snapshot is taken, so --check accepts a record that
// has progressed (an active execution that is now finalized, a ready item that is
// now complete) but never one whose identity, parentage or start differs.

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const snapshotPath = path.join(repoRoot, "site", "data", "gh-84.json");
const siteRoot = path.join(repoRoot, "site");

const readJson = (file) => JSON.parse(fs.readFileSync(file, "utf8"));
const readLines = (file) =>
  fs
    .readFileSync(file, "utf8")
    .split("\n")
    .filter((line) => line.trim() !== "")
    .map((line) => JSON.parse(line));

const inScope = (id) => id === "GH-84" || /^PRAXIS-SITE-\d+$/.test(id);
const shortCommit = (commit) => (typeof commit === "string" ? commit.slice(0, 7) : null);
const orUnknown = (value) => (value === null || value === undefined || value === "" ? "unknown" : value);

const stateFromEvent = { "work.started": "active", "work.resumed": "active", "work.blocked": "blocked", "work.completed": "complete" };

export const workItems = ({ queue, events }) => {
  const lastState = events
    .filter((event) => inScope(event.workItem) && stateFromEvent[event.type])
    .reduce((states, event) => ({ ...states, [event.workItem]: stateFromEvent[event.type] }), {});
  const evidence = events
    .filter((event) => inScope(event.workItem) && event.type === "work.completed")
    .reduce((found, event) => ({ ...found, [event.workItem]: (event.evidence ?? []).map(({ type, path: file }) => ({ type, path: file })) }), {});
  const executions = events
    .filter((event) => inScope(event.workItem))
    .reduce((found, event) => ({ ...found, [event.workItem]: [...new Set([...(found[event.workItem] ?? []), ...(event.telemetryExecutions ?? [])])] }), {});
  const sources = Object.fromEntries(queue.items.filter((item) => inScope(item.id)).map((item) => [item.id, item.sourceReference ?? null]));
  const shape = (id, title, state) => ({ id, title, state, sourceReference: sources[id] ?? null, executions: executions[id] ?? [], evidence: evidence[id] ?? [] });
  const titled = queue.items.filter((item) => inScope(item.id)).map((item) => shape(item.id, item.title, lastState[item.id] ?? item.status));
  const unqueued = Object.keys(lastState)
    .filter((id) => !titled.some((item) => item.id === id))
    .map((id) => shape(id, id === "GH-84" ? "Build the public Praxis website" : null, lastState[id]));
  const order = (id) => (id === "GH-84" ? -1 : Number(id.split("-").pop()));
  return [...unqueued, ...titled].sort((a, b) => order(a.id) - order(b.id));
};

export const lifecycle = (events) =>
  events
    .filter((event) => event.workItem === "GH-84")
    .map((event) => ({
      type: event.type,
      occurredAt: event.occurredAt,
      actor: event.actor ? { kind: event.actor.kind, provider: orUnknown(event.actor.provider), runtime: orUnknown(event.actor.runtime), model: orUnknown(event.actor.model) } : null,
      ...(event.reason ? { reason: event.reason } : {}),
      executions: event.telemetryExecutions ?? [],
    }));

// Status of one headline metric: recorded when any metric in its family was
// observed, otherwise the capability status of the total itself.
const metricStatus = (record, prefix, total) => {
  const observed = record.metrics.filter((metric) => metric.id.startsWith(prefix));
  const capability = record.capabilities.find((entry) => entry.metricId === total);
  return observed.length > 0 ? "recorded" : capability?.status === "supported-unavailable" ? "unavailable" : "unknown";
};

const metricValue = (record, id) => record.metrics.find((metric) => metric.id === id)?.value ?? null;

export const execution = (record) => ({
  executionId: record.executionId,
  workItemId: record.workItemId,
  status: record.status,
  startedAt: record.startedAt,
  finalizedAt: record.finalizedAt ?? null,
  identity: {
    actorKind: orUnknown(record.identity.actorKind),
    provider: orUnknown(record.identity.provider),
    runtime: orUnknown(record.identity.runtime),
    model: orUnknown(record.identity.model),
  },
  parentExecutionId: record.identity.parentExecutionId ?? null,
  start: record.repository?.start?.available ? { branch: record.repository.start.branch, commit: shortCommit(record.repository.start.commit) } : null,
  end: record.repository?.end?.available ? { branch: record.repository.end.branch, commit: shortCommit(record.repository.end.commit) } : null,
  metrics: {
    wallMs: metricValue(record, "time.wall_ms"),
    commitsCreated: metricValue(record, "git.commits_created"),
    filesAdded: metricValue(record, "git.files_added"),
    filesModified: metricValue(record, "git.files_modified"),
    testsAdded: metricValue(record, "tests.added"),
    tokens: metricStatus(record, "tokens.", "tokens.total"),
    cost: metricStatus(record, "cost.", "cost.execution_total"),
  },
});

const readRecords = (root) => {
  const events = readLines(path.join(root, ".ros", "events", "events.jsonl"));
  const queue = readJson(path.join(root, ".ros", "work", "queue.json"));
  const ids = [...new Set(events.filter((event) => inScope(event.workItem)).flatMap((event) => event.telemetryExecutions ?? []))];
  const executions = ids
    .map((id) => path.join(root, ".ros", "telemetry", "executions", `${id}.json`))
    .filter((file) => fs.existsSync(file))
    .map((file) => execution(readJson(file)))
    .sort((a, b) => a.startedAt.localeCompare(b.startedAt));
  return { events, queue, executions };
};

export const buildSnapshot = (root = repoRoot, asOf = new Date().toISOString()) => {
  const { events, queue, executions } = readRecords(root);
  return {
    schemaVersion: "1.0.0",
    kind: "praxis-public-evidence",
    asOf,
    source: "https://github.com/kemiller2002/praxis",
    note: "Generated by site-tools/evidence.mjs from the repository's Praxis records. Values reflect the records at asOf.",
    workItems: workItems({ queue, events }),
    lifecycle: lifecycle(events),
    executions,
  };
};

const stateRank = { captured: 0, ready: 1, active: 2, blocked: 2, complete: 3 };
const statusRank = { active: 0, finalized: 1 };

export const snapshotProblems = (snapshot, current) => {
  const executionProblems = snapshot.executions.flatMap((published) => {
    const record = current.executions.find((entry) => entry.executionId === published.executionId);
    if (!record) return [`${published.executionId}: no such execution record`];
    const fixed = ["workItemId", "startedAt", "parentExecutionId"]
      .filter((key) => published[key] !== record[key])
      .map((key) => `${published.executionId}: ${key} ${published[key]} differs from record ${record[key]}`);
    const identity = Object.keys(published.identity)
      .filter((key) => published.identity[key] !== record.identity[key])
      .map((key) => `${published.executionId}: identity.${key} ${published.identity[key]} differs from record ${record.identity[key]}`);
    const start = JSON.stringify(published.start) === JSON.stringify(record.start) ? [] : [`${published.executionId}: start differs from record`];
    const status = statusRank[record.status] >= statusRank[published.status] ? [] : [`${published.executionId}: status ${published.status} is ahead of record ${record.status}`];
    const settled =
      published.status === "finalized"
        ? ["finalizedAt", "end", "metrics"]
            .filter((key) => JSON.stringify(published[key]) !== JSON.stringify(record[key]))
            .map((key) => `${published.executionId}: finalized ${key} differs from record`)
        : [];
    return [...fixed, ...identity, ...start, ...status, ...settled];
  });
  const itemProblems = snapshot.workItems.flatMap((published) => {
    const record = current.workItems.find((entry) => entry.id === published.id);
    if (!record) return [`${published.id}: no such work item`];
    const title = published.title === record.title ? [] : [`${published.id}: title differs from record`];
    const state = (stateRank[record.state] ?? 0) >= (stateRank[published.state] ?? 0) ? [] : [`${published.id}: state ${published.state} is ahead of record ${record.state}`];
    const evidence =
      published.state === "complete" && JSON.stringify(published.evidence) !== JSON.stringify(record.evidence) ? [`${published.id}: completion evidence differs from record`] : [];
    return [...title, ...state, ...evidence];
  });
  const lifecycleProblems = snapshot.lifecycle
    .filter((published, index) => JSON.stringify(published) !== JSON.stringify(current.lifecycle[index]))
    .map((published) => `lifecycle ${published.type} at ${published.occurredAt} differs from record`);
  return [...executionProblems, ...itemProblems, ...lifecycleProblems];
};

// data-evidence="EXE-...:identity.runtime", "GH-84:state", "count:complete",
// "lifecycle:1.reason" or "asOf:date"
export const lookup = (snapshot, key) => {
  const [subject, field] = key.split(":");
  const counts = {
    total: snapshot.workItems.filter((item) => item.id !== "GH-84").length,
    complete: snapshot.workItems.filter((item) => item.id !== "GH-84" && item.state === "complete").length,
    executions: snapshot.executions.length,
    tokensRecorded: snapshot.executions.filter((execution) => execution.metrics.tokens === "recorded").length,
    costRecorded: snapshot.executions.filter((execution) => execution.metrics.cost === "recorded").length,
    models: snapshot.executions.filter((execution) => execution.identity.model !== "unknown").length,
  };
  if (subject === "count") return counts[field] === undefined ? undefined : String(counts[field]);
  if (subject === "asOf") return snapshot.asOf.slice(0, 10);
  if (subject === "lifecycle") {
    const [index, ...path] = field.split(".");
    const found = path.reduce((node, part) => (node === null || node === undefined ? undefined : node[part]), snapshot.lifecycle[Number(index)]);
    return found === undefined ? undefined : String(found);
  }
  const entity = snapshot.executions.find((entry) => entry.executionId === subject) ?? snapshot.workItems.find((entry) => entry.id === subject);
  const [head, rest] = field.split(/\.(.*)/s);
  if (head === "evidence" && entity?.evidence) return entity.evidence.find((entry) => entry.type === rest)?.path;
  const value = field.split(".").reduce((node, part) => (node === null || node === undefined ? undefined : node[part]), entity);
  return value === undefined ? undefined : String(value);
};

const decode = (text) =>
  text
    .replace(/<[^>]+>/g, "")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/\s+/g, " ")
    .trim();

export const htmlProblems = (snapshot, name, html) =>
  [...html.matchAll(/<([a-z0-9]+)\b[^>]*\bdata-evidence="([^"]+)"[^>]*>([\s\S]*?)<\/\1>/g)].flatMap(([, , key, inner]) => {
    const expected = lookup(snapshot, key);
    const shown = decode(inner);
    return expected === undefined
      ? [`${name}: data-evidence "${key}" is not in the snapshot`]
      : shown === expected
        ? []
        : [`${name}: data-evidence "${key}" shows "${shown}" but the snapshot records "${expected}"`];
  });

const escape = (text) => String(text).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

const stateClass = { complete: "status--verified", active: "status--pending", blocked: "status--unresolved", ready: "status--ready", captured: "status--ready" };

// The case-study ledger (PRAXIS-SITE-23) is generated, never typed: every row
// cites the snapshot, so the evidence check covers it like any other value.
export const renderLedger = (snapshot) =>
  snapshot.workItems
    .map(
      (item) => `            <tr>
              <th scope="row"><span class="mono" data-evidence="${item.id}:id">${item.id}</span></th>
              <td data-evidence="${item.id}:title">${escape(item.title)}</td>
              <td><span class="status ${stateClass[item.state] ?? "status--unknown"}" data-evidence="${item.id}:state">${item.state}</span></td>
              <td class="mono" data-evidence="${item.id}:executions.length">${item.executions.length}</td>
            </tr>`,
    )
    .join("\n");

const ledgerPattern = /(<!-- ledger:start -->)[\s\S]*?(\n\s*<!-- ledger:end -->)/;

export const withLedger = (html, snapshot) => html.replace(ledgerPattern, (_, start, end) => `${start}\n${renderLedger(snapshot)}${end}`);

// Refreshes every leaf data-evidence value (text only, no markup inside) from
// the snapshot. Values with markup inside are left alone and still checked.
export const withValues = (html, snapshot) =>
  html.replace(/(<([a-z0-9]+)\b[^>]*\bdata-evidence="([^"]+)"[^>]*>)([^<]*)(<\/\2>)/g, (whole, open, _tag, key, _inner, close) => {
    const value = lookup(snapshot, key);
    return value === undefined ? whole : `${open}${escape(value)}${close}`;
  });

const check = () => {
  if (!fs.existsSync(snapshotPath)) return ["site/data/gh-84.json is missing; run node site-tools/evidence.mjs"];
  const snapshot = readJson(snapshotPath);
  const { events, queue, executions } = readRecords(repoRoot);
  const current = { executions, workItems: workItems({ queue, events }), lifecycle: lifecycle(events) };
  const pages = fs.readdirSync(siteRoot).filter((file) => file.endsWith(".html"));
  return [
    ...snapshotProblems(snapshot, current),
    ...pages.flatMap((page) => htmlProblems(snapshot, `site/${page}`, fs.readFileSync(path.join(siteRoot, page), "utf8"))),
  ];
};

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.includes("--render")) {
    const page = path.join(siteRoot, "index.html");
    const snapshot = readJson(snapshotPath);
    fs.writeFileSync(page, withValues(withLedger(fs.readFileSync(page, "utf8"), snapshot), snapshot));
    console.log("rendered ledger and evidence values into site/index.html");
  } else if (process.argv.includes("--check")) {
    const problems = check();
    problems.forEach((problem) => console.error(problem));
    console.log(problems.length === 0 ? "evidence check passed" : `evidence check failed: ${problems.length} problem(s)`);
    process.exitCode = problems.length === 0 ? 0 : 1;
  } else {
    fs.mkdirSync(path.dirname(snapshotPath), { recursive: true });
    fs.writeFileSync(snapshotPath, `${JSON.stringify(buildSnapshot(), null, 2)}\n`);
    console.log(`wrote ${path.relative(repoRoot, snapshotPath)}`);
  }
}
