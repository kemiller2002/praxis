#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const ID_RE =
  /^(?:(RP|JR|EV|HY|TH|EX|DF|CN|GL|MS)-[A-Z0-9]+(?:-[A-Z0-9]+)*-[0-9]{4}-(?:[0-9]{4}|[A-F0-9]{4})|RP-[0-9]{4}-[0-9]{2}-[0-9]{2}-[A-Z0-9]+(?:-[A-Z0-9]+)*)$/;
const REFERENCE_FIELDS = new Set([
  "contradicts",
  "contradicting_evidence",
  "depends_on",
  "derived_from",
  "evidence_ids",
  "hypothesis_ids",
  "related_documents",
  "related_mission",
  "related_package",
  "related_theories",
  "supporting_evidence",
  "supports",
  "superseded_by",
  "supersedes",
  "tests_hypotheses",
  "theory_ids"
]);
const ALLOWED_STATUS = {
  DF: new Set(["draft", "review", "accepted", "superseded", "withdrawn"]),
  EV: new Set(["draft", "review", "accepted", "superseded", "withdrawn"]),
  EX: new Set(["proposed", "active", "blocked", "completed", "cancelled"]),
  HY: new Set(["proposed", "active", "supported", "rejected", "superseded", "withdrawn"]),
  MS: new Set(["proposed", "approved", "active", "blocked", "completed", "cancelled", "archived"]),
  RP: new Set([
    "draft",
    "review",
    "accepted",
    "canonical",
    "deprecated",
    "archived",
    "superseded",
    "withdrawn"
  ]),
  TH: new Set(["candidate", "supported", "established", "challenged", "superseded", "rejected"])
};
const CONFIDENCE = new Set(["very-low", "low", "medium", "medium-high", "high", "very-high"]);
const KIND_CONFIG = {
  decisions: ["research/decisions", "registries/decisions.json", "DF"],
  evidence: ["research/evidence", "registries/evidence.json", "EV"],
  experiments: ["research/experiments", "registries/experiments.json", "EX"],
  hypotheses: ["research/hypotheses", "registries/hypotheses.json", "HY"],
  journals: ["research/journals", "registries/journals.json", "JR"],
  missions: ["missions", "registries/missions.json", "MS"],
  "research-packages": ["research/packages", "registries/research-packages.json", "RP"],
  theories: ["research/theories", "registries/theories.json", "TH"]
};

const SEMANTIC_STATES = new Set(["backlog", "ready", "active", "review", "blocked", "complete"]);
const TRANSITIONS = {
  ready: new Set(["begin", "block"]),
  active: new Set(["complete", "block"]),
  blocked: new Set(["resume"]),
  complete: new Set()
};

function readJson(file, fallback = null) {
  return fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, "utf8")) : fallback;
}

function writeJson(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function workConfig(root) {
  const config = readJson(path.join(root, "ros.json"), {});
  const result = {
    protocolVersion: config.workProtocol?.version ?? "1.0.0",
    repository: config.repository?.id ?? config.name ?? path.basename(root),
    stateMapping: config.workProtocol?.semanticMapping ?? {
      ready: "ready", active: "active", blocked: "blocked", complete: "complete"
    },
    evidence: config.workProtocol?.completionEvidence ?? { default: ["implementation", "tests"] },
    meaningful: config.workProtocol?.meaningfulPaths ?? ["**"],
    ignored: config.workProtocol?.ignoredPaths ?? [".git/**", ".ros/context/**", ".ros/events/**"],
    enforce: config.workProtocol?.enforceAttribution === true
  };
  for (const [local, semantic] of Object.entries(result.stateMapping)) {
    if (!SEMANTIC_STATES.has(semantic)) throw new Error(`ros.json maps '${local}' to invalid semantic state '${semantic}'`);
  }
  return result;
}

function gitPaths(root, base = "HEAD") {
  try {
    const output = execFileSync("git", ["-C", root, "status", "--porcelain=v1", "-z", "--untracked-files=all"], { encoding: "utf8" });
    const paths = output.split("\0").filter(Boolean).map((line) => {
      const value = line.slice(3);
      return value.includes(" -> ") ? value.split(" -> ").at(-1) : value;
    });
    const comparison = process.env.ROS_BASE_REF;
    if (comparison) {
      const committed = execFileSync("git", ["-C", root, "diff", "--name-only", `${comparison}...HEAD`], { encoding: "utf8" });
      paths.push(...committed.split(/\r?\n/).filter(Boolean));
    }
    return [...new Set(paths)].sort();
  } catch {
    return [];
  }
}

function globMatch(value, pattern) {
  const escaped = pattern.replace(/[.+^${}()|[\]\\]/g, "\\$&").replaceAll("**", "\u0000").replaceAll("*", "[^/]*").replaceAll("\u0000", ".*");
  return new RegExp(`^${escaped}$`).test(value);
}

function meaningfulPaths(root, paths) {
  const config = workConfig(root);
  return paths.filter((item) => config.meaningful.some((pattern) => globMatch(item, pattern)) && !config.ignored.some((pattern) => globMatch(item, pattern)));
}

function contextPath(root) { return path.join(root, ".ros", "context", "current.json"); }
function eventsPath(root) { return path.join(root, ".ros", "events", "events.jsonl"); }

function loadContext(root) {
  return readJson(contextPath(root), { schemaVersion: "1.0.0", repository: workConfig(root).repository, workItems: [] });
}

function allowedActions(item) {
  return [...(TRANSITIONS[item.semanticState] ?? [])].sort();
}

function contextView(root, requestedId) {
  const config = workConfig(root);
  const context = loadContext(root);
  const items = requestedId ? context.workItems.filter((item) => item.id === requestedId) : context.workItems;
  if (requestedId && !items.length) throw new Error(`work item '${requestedId}' is not in repository context`);
  return {
    schemaVersion: context.schemaVersion,
    protocolVersion: config.protocolVersion,
    repository: config.repository,
    actor: context.actor ?? "unknown",
    workItems: items.map((item) => ({
      ...item,
      allowedActions: allowedActions(item),
      requiredEvidenceForCompletion: config.evidence[item.type] ?? config.evidence.default ?? []
    }))
  };
}

function appendEvent(root, event) {
  const payload = { schemaVersion: "1.0.0", ...event };
  payload.eventId = crypto.createHash("sha256").update(JSON.stringify(payload)).digest("hex").slice(0, 24);
  const file = eventsPath(root);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const existing = fs.existsSync(file) ? fs.readFileSync(file, "utf8").split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];
  if (!existing.some((item) => item.eventId === payload.eventId)) fs.appendFileSync(file, `${JSON.stringify(payload)}\n`, "utf8");
  return payload;
}

function adapterResult(request, outcome, fields = {}) {
  return {
    schemaVersion: "1.0.0",
    protocolVersion: request.protocolVersion,
    requestId: request.requestId,
    operation: request.operation,
    outcome,
    ...fields
  };
}

function validateAdapterRequest(request) {
  for (const field of ["protocolVersion", "requestId", "operation", "repository", "principal"]) {
    if (!request[field]) throw new Error(`adapter request is missing '${field}'`);
  }
  if (request.protocolVersion !== "1.0.0") return adapterResult(request, "failure", { error: { code: "protocol_mismatch", message: "adapter supports protocol 1.0.0" } });
  if (!["getWorkItem", "transitionWorkItem", "publishRepositoryEvent"].includes(request.operation)) {
    return adapterResult(request, "failure", { error: { code: "unsupported_operation", message: `unsupported operation '${request.operation}'` } });
  }
  return null;
}

function callFileAdapter(storeFile, request) {
  const invalid = validateAdapterRequest(request);
  if (invalid) return invalid;
  const store = readJson(storeFile, { schemaVersion: "1.0.0", protocolVersion: "1.0.0", repositories: [], workItems: {}, events: [], requests: {} });
  if (store.requests?.[request.requestId]) return store.requests[request.requestId];
  let result;
  if (!store.repositories.includes(request.repository)) {
    result = adapterResult(request, "failure", { error: { code: "repository_unknown", message: `repository '${request.repository}' is not authorized` } });
  } else if (request.simulateOutcome === "unknown") {
    result = adapterResult(request, "unknown", { error: { code: "remote_outcome_unknown", message: "the remote effect could not be confirmed" } });
  } else if (request.operation === "getWorkItem" && !(request.scopes ?? []).includes("work:read")) {
    result = adapterResult(request, "failure", { error: { code: "forbidden", message: "principal lacks work:read scope" } });
  } else if (request.operation === "getWorkItem") {
    const item = store.workItems[request.workItem];
    result = item
      ? adapterResult(request, "success", { data: { workItem: item } })
      : adapterResult(request, "failure", { error: { code: "work_item_not_found", message: `work item '${request.workItem}' was not found` } });
  } else if (!(request.scopes ?? []).includes("work:transition") && request.operation === "transitionWorkItem") {
    result = adapterResult(request, "failure", { error: { code: "forbidden", message: "principal lacks work:transition scope" } });
  } else if (request.operation === "transitionWorkItem") {
    const item = store.workItems[request.workItem];
    if (!item) result = adapterResult(request, "failure", { error: { code: "work_item_not_found", message: `work item '${request.workItem}' was not found` } });
    else if (request.expectedState && item.state !== request.expectedState) result = adapterResult(request, "failure", { error: { code: "state_conflict", message: `expected '${request.expectedState}', found '${item.state}'` } });
    else {
      item.state = request.targetState;
      item.updatedBy = request.principal;
      result = adapterResult(request, "success", { data: { workItem: item } });
    }
  } else if (!(request.scopes ?? []).includes("event:publish")) {
    result = adapterResult(request, "failure", { error: { code: "forbidden", message: "principal lacks event:publish scope" } });
  } else {
    const event = request.event;
    if (!event?.eventId) result = adapterResult(request, "failure", { error: { code: "invalid_event", message: "event.eventId is required" } });
    else {
      if (!store.events.some((item) => item.eventId === event.eventId)) store.events.push(event);
      result = adapterResult(request, "success", { data: { eventId: event.eventId } });
    }
  }
  store.requests ??= {};
  store.requests[request.requestId] = result;
  writeJson(storeFile, store);
  return result;
}

function transition(root, action, ids, options = {}) {
  if (!ids.length) throw new Error(`${action} requires at least one work-item ID`);
  const config = workConfig(root);
  const context = loadContext(root);
  const now = new Date().toISOString();
  const byId = new Map(context.workItems.map((item) => [item.id, item]));
  const events = [];
  for (const id of ids) {
    if (!/^[A-Z][A-Z0-9_-]*-[A-Z0-9][A-Z0-9_-]*$/.test(id)) throw new Error(`invalid work-item ID '${id}'`);
    let item = byId.get(id);
    if (!item) {
      if (action !== "begin") throw new Error(`work item '${id}' is not in repository context`);
      item = { id, type: options.type ?? "task", state: "ready", semanticState: "ready", evidence: [] };
      context.workItems.push(item);
      byId.set(id, item);
    }
    const current = item.semanticState;
    if (!TRANSITIONS[current]?.has(action)) throw new Error(`cannot ${action} '${id}' from '${current}'`);
    if (action === "begin" || action === "resume") item.state = options.localState ?? "active";
    if (action === "block") {
      if (!options.reason) throw new Error("block requires --reason");
      item.state = "blocked";
      item.blockReason = options.reason;
    }
    if (action === "complete") {
      const evidence = options.evidence ?? [];
      const required = config.evidence[item.type] ?? config.evidence.default ?? [];
      const types = new Set(evidence.map((entry) => entry.type));
      const missing = required.filter((kind) => !types.has(kind));
      if (missing.length) throw new Error(`completion evidence missing for '${id}': ${missing.join(", ")}`);
      for (const entry of evidence) if (!fs.existsSync(path.resolve(root, entry.path))) throw new Error(`evidence path does not exist: ${entry.path}`);
      item.evidence = evidence;
      item.state = options.localState ?? "complete";
      item.completedAt = now;
      if (item.type === "research") item.conclusion = options.conclusion ?? "inconclusive";
    }
    item.semanticState = config.stateMapping[item.state] ?? (action === "begin" || action === "resume" ? "active" : action === "block" ? "blocked" : "complete");
    if (!SEMANTIC_STATES.has(item.semanticState)) throw new Error(`invalid semantic state '${item.semanticState}'`);
    item.updatedAt = now;
    const event = appendEvent(root, {
      type: `work.${action === "begin" ? "started" : action === "complete" ? "completed" : action === "block" ? "blocked" : "resumed"}`,
      workItem: id, repository: config.repository, protocolVersion: config.protocolVersion,
      occurredAt: now, reason: options.reason, evidence: item.evidence,
      paths: action === "complete" ? meaningfulPaths(root, gitPaths(root)).filter((p) => !(context.baselineDirtyPaths ?? []).includes(p)) : [],
      publication: { status: "pending" }
    });
    events.push(event);
  }
  context.protocolVersion = config.protocolVersion;
  context.repository = config.repository;
  context.actor = options.actor ?? process.env.ROS_ACTOR ?? context.actor ?? "unknown";
  context.updatedAt = now;
  if (action === "begin" && !context.startedAt) {
    context.startedAt = now;
    context.baselineDirtyPaths = gitPaths(root).filter((item) => !context.workItems.some((work) => work.id === item));
  }
  writeJson(contextPath(root), context);
  return { context, events };
}

function workFindings(root) {
  const config = workConfig(root);
  if (!config.enforce) return [];
  const context = loadContext(root);
  const changed = meaningfulPaths(root, gitPaths(root)).filter((item) => !(context.baselineDirtyPaths ?? []).includes(item));
  if (!changed.length) return [];
  const events = fs.existsSync(eventsPath(root)) ? fs.readFileSync(eventsPath(root), "utf8").split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];
  const attributed = new Set(events.flatMap((event) => event.paths ?? []));
  const active = context.workItems.some((item) => item.semanticState === "active" || item.semanticState === "blocked");
  return changed.filter((item) => !attributed.has(item) && !active).map((item) => ({ path: item, field: "work_items", message: "meaningful change has no active or completed work-item attribution" }));
}

function scalar(raw) {
  const value = raw.trim();
  if (!value) return "";
  if (value === "[]") return [];
  if (value === "{}") return {};
  if (value.startsWith("[") && value.endsWith("]")) {
    const inner = value.slice(1, -1).trim();
    if (!inner) return [];
    return inner.split(",").map((item) => item.trim().replace(/^['"]|['"]$/g, ""));
  }
  if (["true", "false"].includes(value.toLowerCase())) return value.toLowerCase() === "true";
  if (/^-?\d+(?:\.\d+)?$/.test(value)) return Number(value);
  return value.replace(/^['"]|['"]$/g, "");
}

export function parseFrontMatter(text) {
  const lines = text.split(/\r?\n/);
  if (lines[0]?.trim() !== "---") throw new Error("missing opening '---'");
  const end = lines.findIndex((line, index) => index > 0 && line.trim() === "---");
  if (end < 0) throw new Error("missing closing '---'");
  const result = {};
  const stack = [{ indent: -1, value: result }];
  for (let index = 1; index < end; index += 1) {
    const raw = lines[index];
    if (!raw.trim() || raw.trimStart().startsWith("#")) continue;
    const indent = raw.length - raw.trimStart().length;
    const stripped = raw.trim();
    while (stack.at(-1).indent >= indent) stack.pop();
    const parent = stack.at(-1).value;
    if (stripped.startsWith("- ")) {
      if (!Array.isArray(parent)) throw new Error(`line ${index + 1}: list item has no list field`);
      parent.push(scalar(stripped.slice(2)));
      continue;
    }
    const separator = stripped.indexOf(":");
    if (separator < 1 || Array.isArray(parent)) {
      throw new Error(`line ${index + 1}: expected 'field: value'`);
    }
    const key = stripped.slice(0, separator).trim();
    const rawValue = stripped.slice(separator + 1);
    if (rawValue.trim()) {
      parent[key] = scalar(rawValue);
      continue;
    }
    const next = lines[index + 1];
    const nextIndent = next ? next.length - next.trimStart().length : -1;
    const child = next && nextIndent > indent && next.trim().startsWith("- ") ? [] : {};
    parent[key] = child;
    stack.push({ indent, value: child });
  }
  return result;
}

function walkMarkdown(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const target = path.join(directory, entry.name);
    if (entry.isDirectory()) return walkMarkdown(target);
    return entry.isFile() && entry.name.endsWith(".md") && !entry.name.startsWith(".") ? [target] : [];
  });
}

function artifactFiles(root) {
  const files = new Set();
  for (const [directory] of Object.values(KIND_CONFIG)) {
    for (const file of walkMarkdown(path.join(root, directory))) files.add(file);
  }
  return [...files].sort();
}

function loadArtifacts(root) {
  const artifacts = [];
  const findings = [];
  for (const file of artifactFiles(root)) {
    const relative = path.relative(root, file).split(path.sep).join("/");
    try {
      const metadata = parseFrontMatter(fs.readFileSync(file, "utf8"));
      artifacts.push({
        file,
        relative,
        metadata,
        id: String(metadata.id ?? metadata.identifier ?? "")
      });
    } catch (error) {
      findings.push({ path: relative, field: "front_matter", message: error.message });
    }
  }
  return { artifacts, findings };
}

function prefix(identifier) {
  return identifier.includes("-") ? identifier.split("-", 1)[0] : "";
}

function references(value) {
  if (typeof value === "string") return ID_RE.test(value) ? [value] : [];
  if (Array.isArray(value)) return value.filter((item) => typeof item === "string" && ID_RE.test(item));
  return [];
}

function stable(value) {
  if (Array.isArray(value)) return value.map(stable);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, stable(value[key])]));
  }
  return value;
}

function renderedRegistries(root, artifacts) {
  const rendered = new Map();
  for (const [, registry, artifactPrefix] of Object.values(KIND_CONFIG)) {
    const entries = artifacts
      .filter((artifact) => prefix(artifact.id) === artifactPrefix)
      .map((artifact) => {
        const entry = { ...artifact.metadata, id: artifact.id, path: artifact.relative };
        delete entry.identifier;
        return stable(entry);
      })
      .sort((a, b) => a.id.localeCompare(b.id));
    rendered.set(path.join(root, registry), `${JSON.stringify(entries, null, 2)}\n`);
  }
  return rendered;
}

function registryFindings(root, artifacts) {
  const findings = [];
  for (const [file, expected] of renderedRegistries(root, artifacts)) {
    const actual = fs.existsSync(file) ? fs.readFileSync(file, "utf8") : null;
    if (actual !== expected) {
      findings.push({
        path: path.relative(root, file).split(path.sep).join("/"),
        field: "",
        message: "registry is stale; run 'ros registry build'"
      });
    }
  }
  return findings;
}

export function validate(root, { checkRegistries = true } = {}) {
  const loaded = loadArtifacts(root);
  const findings = [...loaded.findings];
  const byId = new Map();
  for (const artifact of loaded.artifacts) {
    if (!artifact.id) {
      findings.push({ path: artifact.relative, field: "id", message: "required field is missing" });
      continue;
    }
    if (!ID_RE.test(artifact.id)) {
      findings.push({ path: artifact.relative, field: "id", message: `invalid identifier '${artifact.id}'` });
    }
    if (!byId.has(artifact.id)) byId.set(artifact.id, []);
    byId.get(artifact.id).push(artifact);
    if (!artifact.metadata.title) {
      findings.push({ path: artifact.relative, field: "title", message: "required field is missing" });
    }
    const basename = path.basename(artifact.file);
    const legacyRepFilename = /^RP-[0-9]{4}-[0-9]{2}-[0-9]{2}-/.test(artifact.id) && basename === `${artifact.id}.md`;
    if (!basename.startsWith(`${artifact.id}--`) && !legacyRepFilename) {
      findings.push({
        path: artifact.relative,
        field: "id",
        message: `filename must start with '${artifact.id}--'`
      });
    }
    const kind = prefix(artifact.id);
    const status = artifact.metadata.status;
    if (status && ALLOWED_STATUS[kind] && !ALLOWED_STATUS[kind].has(status)) {
      findings.push({ path: artifact.relative, field: "status", message: `'${status}' is not allowed for ${kind}` });
    }
    const confidence = artifact.metadata.confidence;
    if (typeof confidence === "string" && !CONFIDENCE.has(confidence)) {
      findings.push({ path: artifact.relative, field: "confidence", message: `unknown label '${confidence}'` });
    }
  }
  for (const [identifier, records] of byId) {
    if (records.length > 1) {
      const paths = records.map((record) => record.relative).join(", ");
      for (const record of records) {
        findings.push({
          path: record.relative,
          field: "id",
          message: `duplicate '${identifier}' also in ${paths}`
        });
      }
    }
  }
  const known = new Set(byId.keys());
  for (const artifact of loaded.artifacts) {
    for (const field of REFERENCE_FIELDS) {
      for (const target of references(artifact.metadata[field])) {
        if (!known.has(target)) {
          findings.push({ path: artifact.relative, field, message: `broken reference '${target}'` });
        }
        if (target === artifact.id && ["supersedes", "superseded_by"].includes(field)) {
          findings.push({ path: artifact.relative, field, message: "artifact cannot supersede itself" });
        }
      }
    }
    for (const target of references(artifact.metadata.supersedes)) {
      const reciprocal = byId.get(target)?.[0];
      if (reciprocal && !references(reciprocal.metadata.superseded_by).includes(artifact.id)) {
        findings.push({ path: artifact.relative, field: "supersedes", message: `'${target}' is not reciprocal` });
      }
    }
    for (const target of references(artifact.metadata.superseded_by)) {
      const reciprocal = byId.get(target)?.[0];
      if (reciprocal && !references(reciprocal.metadata.supersedes).includes(artifact.id)) {
        findings.push({ path: artifact.relative, field: "superseded_by", message: `'${target}' is not reciprocal` });
      }
    }
  }
  if (checkRegistries) findings.push(...registryFindings(root, loaded.artifacts));
  findings.push(...workFindings(root));
  return findings.sort((a, b) =>
    [a.path, a.field, a.message].join("\0").localeCompare([b.path, b.field, b.message].join("\0"))
  );
}

export function buildRegistries(root, { dryRun = false } = {}) {
  const loaded = loadArtifacts(root);
  if (loaded.findings.length) return { changed: 0, findings: loaded.findings };
  let changed = 0;
  for (const [file, content] of renderedRegistries(root, loaded.artifacts)) {
    const actual = fs.existsSync(file) ? fs.readFileSync(file, "utf8") : null;
    if (actual === content) continue;
    changed += 1;
    console.log(`${dryRun ? "WOULD WRITE" : "WROTE"} ${path.relative(root, file).split(path.sep).join("/")}`);
    if (!dryRun) {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, content, "utf8");
    }
  }
  return { changed, findings: [] };
}

function renderFinding(finding) {
  const location = finding.field ? `${finding.path}:${finding.field}` : finding.path;
  return `${location}: ${finding.message}`;
}

function findingRecord(finding) {
  const repair = finding.message.includes("registry is stale")
    ? "Run './ros registry build'."
    : finding.field === "work_items"
      ? "Run './ros work begin WORK-ID', perform the change, then complete it with configured evidence."
      : "Correct the named file and field, then run './ros validate' again.";
  return { severity: "error", path: finding.path, field: finding.field || null, message: finding.message, repair };
}

function statusView(root) {
  const context = contextView(root);
  const findings = validate(root);
  return {
    repository: workConfig(root).repository,
    protocolVersion: workConfig(root).protocolVersion,
    validation: findings.length ? "failed" : "passed",
    findingCount: findings.length,
    workItems: context.workItems.map(({ id, type, state, semanticState, allowedActions }) => ({ id, type, state, semanticState, allowedActions })),
    nextActions: findings.length ? [...new Set(findings.map((item) => findingRecord(item).repair))] : ["Select an allowed work transition or begin a new work item."]
  };
}

function parseCli(argv) {
  let root = process.cwd();
  const args = [...argv];
  const rootIndex = args.indexOf("--root");
  if (rootIndex >= 0) {
    if (!args[rootIndex + 1]) throw new Error("--root requires a value");
    root = path.resolve(args[rootIndex + 1]);
    args.splice(rootIndex, 2);
  }
  return { root: path.resolve(root), args };
}

function option(args, name) {
  const index = args.indexOf(name);
  if (index < 0) return undefined;
  if (!args[index + 1] || args[index + 1].startsWith("--")) throw new Error(`${name} requires a value`);
  return args[index + 1];
}

function evidenceOptions(args) {
  const entries = [];
  for (let i = 0; i < args.length; i += 1) if (args[i] === "--evidence") {
    const value = args[++i];
    if (!value?.includes("=")) throw new Error("--evidence requires TYPE=PATH");
    const [type, ...rest] = value.split("="); entries.push({ type, path: rest.join("=") });
  }
  return entries;
}

export function main(argv) {
  try {
    const { root, args } = parseCli(argv);
    if (args[0] === "work" && args[1] === "context") {
      console.log(JSON.stringify(contextView(root, args[2]), null, 2)); return 0;
    }
    if (args[0] === "work" && ["begin", "block", "resume", "complete"].includes(args[1])) {
      const action = args[1];
      const ids = args.slice(2).filter((arg, index, all) => !arg.startsWith("--") && (index === 0 || !all[index - 1].startsWith("--")));
      const result = transition(root, action, ids, {
        type: option(args, "--type"), actor: option(args, "--actor"), reason: option(args, "--reason"),
        localState: option(args, "--local-state"), conclusion: option(args, "--conclusion"), evidence: evidenceOptions(args)
      });
      console.log(JSON.stringify({ workItems: result.context.workItems, events: result.events.map((event) => event.eventId) }, null, 2)); return 0;
    }
    if (args[0] === "adapter" && args[1] === "publish") {
      const target = option(args, "--target");
      if (!target) throw new Error("adapter publish requires --target");
      const source = eventsPath(root);
      const events = fs.existsSync(source) ? fs.readFileSync(source, "utf8").split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];
      const destination = path.resolve(root, target);
      const published = fs.existsSync(destination) ? fs.readFileSync(destination, "utf8").split(/\r?\n/).filter(Boolean).map(JSON.parse) : [];
      const known = new Set(published.map((event) => event.eventId));
      const additions = events.filter((event) => !known.has(event.eventId));
      fs.mkdirSync(path.dirname(destination), { recursive: true });
      for (const event of additions) fs.appendFileSync(destination, `${JSON.stringify(event)}\n`, "utf8");
      const receiptsFile = path.join(root, ".ros", "publications.json");
      const receipts = readJson(receiptsFile, {});
      const publishedAt = new Date().toISOString();
      for (const event of events) receipts[event.eventId] = { status: "success", target, publishedAt };
      writeJson(receiptsFile, receipts);
      console.log(`published ${additions.length} event(s); ${events.length - additions.length} duplicate(s) skipped`); return 0;
    }
    if (args[0] === "adapter" && args[1] === "call") {
      const store = option(args, "--store");
      const requestFile = option(args, "--request");
      if (!store || !requestFile) throw new Error("adapter call requires --store and --request");
      const request = readJson(path.resolve(root, requestFile));
      if (!request) throw new Error(`adapter request not found: ${requestFile}`);
      const result = callFileAdapter(path.resolve(root, store), request);
      console.log(JSON.stringify(result, null, 2));
      return result.outcome === "failure" ? 1 : result.outcome === "unknown" ? 2 : 0;
    }
    if (args[0] === "validate") {
      const findings = validate(root);
      if (args.includes("--json")) {
        console.log(JSON.stringify({ valid: findings.length === 0, findings: findings.map(findingRecord) }, null, 2));
        return findings.length ? 1 : 0;
      }
      if (findings.length) {
        for (const finding of findings) console.error(`ERROR ${renderFinding(finding)}\n  REPAIR ${findingRecord(finding).repair}`);
        console.error(`validation failed with ${findings.length} error(s)`);
        return 1;
      }
      console.log("validation passed");
      return 0;
    }
    if (args[0] === "status") {
      console.log(JSON.stringify(statusView(root), null, 2));
      return 0;
    }
    if (args[0] === "registry" && args[1] === "build") {
      const result = buildRegistries(root, { dryRun: args.includes("--dry-run") });
      if (result.findings.length) {
        for (const finding of result.findings) console.error(`ERROR ${renderFinding(finding)}`);
        return 1;
      }
      console.log(`${result.changed} registry file(s) ${args.includes("--dry-run") ? "would change" : "changed"}`);
      return 0;
    }
    if (args[0] === "registry" && args[1] === "check") {
      const loaded = loadArtifacts(root);
      const findings = [...loaded.findings, ...registryFindings(root, loaded.artifacts)];
      if (findings.length) {
        for (const finding of findings) console.error(`ERROR ${renderFinding(finding)}`);
        return 1;
      }
      console.log("registries are current");
      return 0;
    }
    console.error("Usage: ros [--root PATH] validate [--json] | status | registry build [--dry-run] | registry check | work begin|block|resume|complete|context [ID] | adapter call --store FILE --request FILE | adapter publish --target FILE");
    return 2;
  } catch (error) {
    console.error(`ERROR ${error.message}`);
    return 1;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
