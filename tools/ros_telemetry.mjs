import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";

export const TELEMETRY_SCHEMA_VERSION = "1.0.0";
export const TELEMETRY_ADAPTERS = [
  "generic",
  "openai-codex",
  "anthropic-claude-statusline",
  "anthropic-claude-hook",
  "anthropic-claude-otel",
  "google-gemini-hook",
  "google-gemini-otel",
  "github-copilot-hook",
  "github-copilot-otel",
  "otel-json"
];

export const WORK_CLASSIFICATIONS = new Set([
  "research",
  "development",
  "research-development",
  "maintenance",
  "defect-bug-fix",
  "investigation-diagnostic",
  "architecture-design",
  "documentation",
  "testing-verification",
  "infrastructure-devops",
  "security",
  "operational-support",
  "refactoring",
  "experiment",
  "prototype-proof-of-concept",
  "administrative-process"
]);

const CAPABILITY_STATUSES = new Set([
  "supported-observed",
  "supported-unavailable",
  "unsupported",
  "unknown",
  "derived",
  "estimated"
]);
const QUALITIES = new Set(["observed", "derived", "estimated"]);
const SOURCE_TYPES = new Set([
  "runtime-api",
  "runtime-hook",
  "runtime-output",
  "environment",
  "ros-git",
  "ros-clock",
  "agent-report",
  "human-report",
  "external-tool",
  "calculated"
]);
const QUALITY_DETECTORS = new Set([
  "compiler",
  "type-system",
  "test",
  "static-analysis",
  "architecture-check",
  "runtime",
  "agent-self",
  "human",
  "escaped-defect",
  "mutation-test",
  "ros-state-system"
]);
const RAW_REDACTED_KEY = /^(?:authorization|cookie|set-cookie|password|passwd|secret|credential|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|prompt|prompts|messages?|content|tool[_-]?input|tool[_-]?response|request|response|stdout|stderr|command|full[_-]?command|transcript[_-]?path|cwd|current[_-]?dir|project[_-]?dir|workspace[_-]?path|file[_-]?path|email|user\.email)$/i;
const MAX_RAW_STRING = 2048;

function readJson(file, fallback = null) {
  return fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, "utf8")) : fallback;
}

function writeJson(file, value) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function stable(value) {
  if (Array.isArray(value)) return value.map(stable);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, stable(value[key])]));
  }
  return value;
}

function digest(value, length = 24) {
  return crypto.createHash("sha256").update(JSON.stringify(stable(value))).digest("hex").slice(0, length);
}

function nowIso() {
  return new Date().toISOString();
}

function isTimestamp(value) {
  return typeof value === "string" && Number.isFinite(Date.parse(value));
}

function telemetryConfig(root) {
  const config = readJson(path.join(root, "ros.json"), {});
  const telemetry = config.telemetry ?? {};
  return {
    enabled: telemetry.enabled !== false,
    requireFinalization: telemetry.requireFinalization !== false,
    executionRoot: telemetry.executionRoot ?? ".ros/telemetry/executions",
    metricRegistry: telemetry.metricRegistry ?? "telemetry/metrics.json",
    maxRawPayloadBytes: telemetry.maxRawPayloadBytes ?? 262_144,
    allowRawTelemetry: telemetry.allowRawTelemetry !== false,
    disabledReason: telemetry.disabledReason ?? null,
    repository: config.repository?.id ?? config.name ?? path.basename(root)
  };
}

export function loadMetricRegistry(root) {
  const config = telemetryConfig(root);
  const file = path.resolve(root, config.metricRegistry);
  const registry = readJson(file);
  if (!registry || registry.schemaVersion !== "1.0.0" || !Array.isArray(registry.metrics)) {
    throw new Error(`telemetry metric registry is missing or unsupported: ${config.metricRegistry}`);
  }
  const metrics = new Map();
  for (const metric of registry.metrics) {
    if (!metric?.id || metrics.has(metric.id)) throw new Error(`telemetry metric registry has invalid or duplicate id '${metric?.id ?? ""}'`);
    metrics.set(metric.id, metric);
  }
  return { ...registry, file, metrics };
}

function executionDirectory(root) {
  return path.resolve(root, telemetryConfig(root).executionRoot);
}

function executionFile(root, executionId) {
  if (!/^EXE-[A-Za-z0-9._:-]+$/.test(executionId)) throw new Error(`invalid execution ID '${executionId}'`);
  return path.join(executionDirectory(root), `${executionId}.json`);
}

function executionFiles(root) {
  const directory = executionDirectory(root);
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, { withFileTypes: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith(".json"))
    .map((entry) => path.join(directory, entry.name))
    .sort();
}

export function loadExecutions(root) {
  return executionFiles(root).map((file) => ({ file, record: readJson(file) }));
}

function contextFile(root) {
  return path.join(root, ".ros", "context", "current.json");
}

function loadContext(root) {
  return readJson(contextFile(root), { schemaVersion: "1.0.0", workItems: [] });
}

function updateContextExecution(root, workItemId, executionId) {
  const context = loadContext(root);
  const item = context.workItems.find((entry) => entry.id === workItemId);
  if (!item) throw new Error(`work item '${workItemId}' is not in repository context`);
  item.telemetryExecutionIds ??= [];
  if (!item.telemetryExecutionIds.includes(executionId)) item.telemetryExecutionIds.push(executionId);
  context.updatedAt = nowIso();
  writeJson(contextFile(root), context);
}

function git(root, args, fallback = null) {
  try {
    return execFileSync("git", ["-C", root, ...args], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"]
    }).trim();
  } catch {
    return fallback;
  }
}

function gitPaths(root) {
  let output = "";
  try {
    output = execFileSync("git", ["-C", root, "status", "--porcelain=v1", "-z", "--untracked-files=all"], {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "ignore"]
    });
  } catch {
    return [];
  }
  if (!output) return [];
  const parts = output.split("\0").filter(Boolean);
  const paths = [];
  for (let index = 0; index < parts.length; index += 1) {
    const entry = parts[index];
    const status = entry.slice(0, 2);
    paths.push(entry.slice(3));
    if (/[RC]/.test(status) && parts[index + 1]) index += 1;
  }
  return [...new Set(paths.filter((item) => item && !item.startsWith(".ros/telemetry/")))].sort();
}

function gitSnapshot(root) {
  const inside = git(root, ["rev-parse", "--is-inside-work-tree"], "false") === "true";
  if (!inside) return { available: false, repository: telemetryConfig(root).repository, branch: null, commit: null, dirty: null, dirtyPaths: [] };
  const dirtyPaths = gitPaths(root);
  return {
    available: true,
    repository: telemetryConfig(root).repository,
    branch: git(root, ["branch", "--show-current"], "") || null,
    commit: git(root, ["rev-parse", "HEAD"], null),
    dirty: dirtyPaths.length > 0,
    dirtyPaths
  };
}

function extensionOf(file) {
  const extension = path.extname(file).toLowerCase();
  return extension || "[no-extension]";
}

function isTestFile(file) {
  return /(^|\/)(?:test|tests|__tests__)(\/|$)/i.test(file) || /(?:^|[._-])(?:test|spec)\.[^/]+$/i.test(file);
}

function isDocumentation(file) {
  return /(^|\/)docs?\//i.test(file) || /(^|\/)readme(?:\.[^/]*)?$/i.test(file) || /\.(?:md|mdx|rst|adoc)$/i.test(file);
}

function lineCount(file) {
  try {
    const content = fs.readFileSync(file);
    if (content.includes(0)) return { binary: true, lines: 0 };
    if (!content.length) return { binary: false, lines: 0 };
    const text = content.toString("utf8");
    return { binary: false, lines: text.split("\n").length - (text.endsWith("\n") ? 1 : 0) };
  } catch {
    return { binary: false, lines: 0 };
  }
}

function cleanBaselineChanges(root, start) {
  if (!start.available || !start.commit || start.dirty) {
    return { available: false, reason: !start.available ? "git-unavailable" : !start.commit ? "starting-commit-unavailable" : "preexisting-dirty-worktree" };
  }
  const end = gitSnapshot(root);
  const nameOutput = git(root, ["diff", "--name-status", "--find-renames", start.commit], "");
  const numstatOutput = git(root, ["diff", "--numstat", "--find-renames", start.commit], "");
  const entries = [];
  for (const line of nameOutput.split(/\r?\n/).filter(Boolean)) {
    const [status, first, second] = line.split("\t");
    const code = status[0];
    entries.push({ status: code, path: code === "R" || code === "C" ? second : first, from: code === "R" || code === "C" ? first : null });
  }
  const known = new Set(entries.map((entry) => entry.path));
  const untracked = (git(root, ["ls-files", "--others", "--exclude-standard", "-z"], "") || "")
    .split("\0")
    .filter((item) => item && !item.startsWith(".ros/telemetry/"));
  for (const file of untracked) if (!known.has(file)) entries.push({ status: "A", path: file, from: null, untracked: true });

  let linesAdded = 0;
  let linesDeleted = 0;
  let binaryFiles = 0;
  const lineStats = new Map();
  for (const line of numstatOutput.split(/\r?\n/).filter(Boolean)) {
    const [added, deleted, ...nameParts] = line.split("\t");
    const file = nameParts.at(-1);
    if (added === "-" || deleted === "-") {
      binaryFiles += 1;
      lineStats.set(file, { added: null, deleted: null });
    } else {
      linesAdded += Number(added);
      linesDeleted += Number(deleted);
      lineStats.set(file, { added: Number(added), deleted: Number(deleted) });
    }
  }
  for (const entry of entries.filter((item) => item.untracked)) {
    const counted = lineCount(path.join(root, entry.path));
    if (counted.binary) binaryFiles += 1;
    else linesAdded += counted.lines;
    lineStats.set(entry.path, { added: counted.binary ? null : counted.lines, deleted: 0 });
  }

  const counts = { added: 0, modified: 0, deleted: 0, renamed: 0 };
  for (const entry of entries) {
    if (entry.status === "A") counts.added += 1;
    else if (entry.status === "D") counts.deleted += 1;
    else if (entry.status === "R") counts.renamed += 1;
    else counts.modified += 1;
  }
  const tests = { added: 0, modified: 0, removed: 0 };
  for (const entry of entries.filter((item) => isTestFile(item.path))) {
    if (entry.status === "A") tests.added += 1;
    else if (entry.status === "D") tests.removed += 1;
    else tests.modified += 1;
  }
  const filesByType = {};
  for (const entry of entries) filesByType[extensionOf(entry.path)] = (filesByType[extensionOf(entry.path)] ?? 0) + 1;
  const commits = end.commit && end.commit !== start.commit
    ? Number(git(root, ["rev-list", "--count", `${start.commit}..${end.commit}`], "0"))
    : 0;
  return {
    available: true,
    mechanism: "git-diff-from-clean-execution-baseline",
    startCommit: start.commit,
    endCommit: end.commit,
    branch: end.branch,
    commits,
    counts,
    linesAdded,
    linesDeleted,
    binaryFiles,
    tests,
    documentationFilesChanged: entries.filter((entry) => isDocumentation(entry.path)).length,
    filesByType,
    paths: entries.map((entry) => ({ ...entry, lineStats: lineStats.get(entry.path) ?? null }))
  };
}

function defaultClassification(workType) {
  const mapped = {
    research: "research",
    feature: "development",
    task: "development",
    maintenance: "maintenance",
    bug: "defect-bug-fix",
    infrastructure: "infrastructure-devops",
    mechanical: "administrative-process"
  }[workType];
  return mapped ?? "development";
}

function source(type, name, mechanism, extra = {}) {
  return { type, name, mechanism, ...extra };
}

function discoverIdentity(options = {}) {
  let provider = options.provider ?? process.env.ROS_TELEMETRY_PROVIDER ?? "unknown";
  let runtime = options.runtime ?? process.env.ROS_TELEMETRY_RUNTIME ?? "unknown";
  let mechanism = "explicit-or-unmapped-environment";
  if (provider === "unknown" && runtime === "unknown" && (process.env.CODEX_SESSION_ID || process.env.CODEX_THREAD_ID)) {
    provider = "openai";
    runtime = "codex";
    mechanism = "whitelisted-codex-environment";
  } else if (provider === "unknown" && runtime === "unknown" && process.env.CLAUDE_CODE_SESSION_ID) {
    provider = "anthropic";
    runtime = "claude-code";
    mechanism = "whitelisted-claude-environment";
  } else if (provider === "unknown" && runtime === "unknown" && process.env.GEMINI_SESSION_ID) {
    provider = "google";
    runtime = "gemini-cli";
    mechanism = "whitelisted-gemini-environment";
  } else if (provider === "unknown" && runtime === "unknown" && process.env.COPILOT_SESSION_ID) {
    provider = "github";
    runtime = "copilot";
    mechanism = "whitelisted-copilot-environment";
  } else if (provider === "unknown" && process.env.GITHUB_ACTIONS === "true") {
    provider = "github";
    runtime = runtime === "unknown" ? "github-actions" : runtime;
    mechanism = "whitelisted-github-actions-environment";
  } else if (provider === "unknown" && process.env.OLLAMA_HOST) {
    provider = "local";
    runtime = runtime === "unknown" ? "ollama" : runtime;
    mechanism = "whitelisted-local-runtime-environment";
  }
  return {
    provider,
    model: options.model ?? process.env.ROS_TELEMETRY_MODEL ?? null,
    modelVersion: options.modelVersion ?? process.env.ROS_TELEMETRY_MODEL_VERSION ?? null,
    runtime,
    runtimeVersion: options.runtimeVersion ?? process.env.ROS_TELEMETRY_RUNTIME_VERSION ?? null,
    sessionId: options.sessionId ?? process.env.ROS_TELEMETRY_SESSION_ID ?? process.env.CODEX_SESSION_ID ?? process.env.CLAUDE_CODE_SESSION_ID ?? process.env.GEMINI_SESSION_ID ?? process.env.COPILOT_SESSION_ID ?? null,
    conversationId: options.conversationId ?? process.env.ROS_TELEMETRY_CONVERSATION_ID ?? process.env.CODEX_THREAD_ID ?? null,
    runId: options.runId ?? process.env.ROS_TELEMETRY_RUN_ID ?? process.env.GITHUB_RUN_ID ?? null,
    agentId: options.agentId ?? process.env.ROS_ACTOR ?? null,
    subagentId: options.subagentId ?? null,
    parentExecutionId: options.parentExecutionId ?? null,
    orchestration: options.orchestration ?? {},
    discoverySource: source("environment", "runtime-identity", mechanism)
  };
}

const RUNTIME_CAPABILITIES = {
  codex: ["tokens.input", "tokens.output", "tokens.cached_input", "tokens.cache_write", "tokens.reasoning", "tokens.total", "context.window_size"],
  "claude-code": ["tokens.input", "tokens.output", "tokens.cache_read", "tokens.cache_write", "context.window_size", "context.utilization", "cost.session_cumulative", "time.active_ms", "time.model_ms", "tool.calls", "tool.failures", "tool.duration_ms", "model.requests", "model.request_failures"],
  "gemini-cli": ["tokens.input", "tokens.output", "tokens.reasoning", "tokens.cached_input", "tokens.tool", "context.compactions", "tool.calls", "tool.failures", "tool.duration_ms", "agent.turns", "runtime.memory_peak_bytes", "runtime.cpu_utilization"],
  copilot: ["tokens.input", "tokens.output", "tool.calls", "tool.failures", "tool.duration_ms", "agent.turns"]
};

function initialCapabilities(registry, identity, repository, discoverySource) {
  const gitAvailable = repository.available;
  const runtimeKnown = new Set(RUNTIME_CAPABILITIES[identity.runtime] ?? []);
  return [...registry.metrics.values()].map((metric) => {
    let status = "unknown";
    let reason = "runtime capability not reported or mapped";
    if (metric.collection === "ros-derived") {
      status = metric.id.startsWith("git.") && !gitAvailable ? "supported-unavailable" : "derived";
      reason = metric.id.startsWith("git.") && !gitAvailable ? "Git is unavailable" : "ROS can derive this metric when its preconditions hold";
    } else if (runtimeKnown.has(metric.id)) {
      status = "supported-unavailable";
      reason = "runtime family can expose this metric, but no observation has been ingested for this execution";
    }
    return { metricId: metric.id, status, reason, discoveredAt: nowIso(), source: discoverySource };
  });
}

function ensureRecordDefaults(record) {
  const fallbackSource = record.provenance?.sources?.[0] ?? source("environment", "runtime-identity", "legacy-record-repair");
  for (const capability of record.capabilities ?? []) capability.source ??= fallbackSource;
  return record;
}

function upsertCapability(record, capability) {
  const key = `${capability.metricId ?? ""}\0${capability.providerField ?? ""}`;
  const index = record.capabilities.findIndex((entry) => `${entry.metricId ?? ""}\0${entry.providerField ?? ""}` === key);
  const value = {
    ...capability,
    discoveredAt: capability.discoveredAt ?? nowIso(),
    source: capability.source ?? source("agent-report", "telemetry-capability", "explicit")
  };
  if (index >= 0) record.capabilities[index] = value;
  else record.capabilities.push(value);
}

function normalizeMetric(root, metric, defaults = {}) {
  const registry = loadMetricRegistry(root).metrics;
  const definition = registry.get(metric.id);
  if (!definition) throw new Error(`unknown normalized metric '${metric.id}'; preserve it in raw telemetry until it is registered`);
  const value = typeof metric.value === "string" && metric.value.trim() !== "" ? Number(metric.value) : metric.value;
  if (typeof value !== "number" || !Number.isFinite(value)) throw new Error(`metric '${metric.id}' requires a finite numeric value`);
  const quality = metric.quality ?? defaults.quality ?? "observed";
  const collectedAt = metric.collectedAt ?? defaults.collectedAt ?? nowIso();
  const metricSource = metric.source ?? defaults.source;
  const normalized = {
    measurementId: metric.measurementId ?? "",
    id: metric.id,
    value,
    unit: metric.unit ?? definition.unit,
    currency: metric.currency ?? null,
    quality,
    confidence: metric.confidence ?? null,
    scope: metric.scope ?? defaults.scope ?? "execution",
    aggregation: metric.aggregation ?? definition.aggregation,
    dimensions: metric.dimensions ?? {},
    source: metricSource,
    collectedAt,
    schemaVersion: TELEMETRY_SCHEMA_VERSION
  };
  if (!normalized.measurementId) normalized.measurementId = `MEAS-${digest(normalized)}`;
  return normalized;
}

function addMetric(root, record, metric, defaults = {}) {
  const normalized = normalizeMetric(root, metric, defaults);
  if (!record.metrics.some((entry) => entry.measurementId === normalized.measurementId)) record.metrics.push(normalized);
  upsertCapability(record, {
    metricId: normalized.id,
    status: normalized.quality === "derived" ? "derived" : normalized.quality === "estimated" ? "estimated" : "supported-observed",
    reason: `measurement ${normalized.measurementId} recorded`,
    discoveredAt: normalized.collectedAt,
    source: normalized.source
  });
  return normalized;
}

function executionId() {
  const stamp = nowIso().replace(/[-:.]/g, "");
  return `EXE-${stamp}-${crypto.randomBytes(4).toString("hex")}`;
}

export function startExecution(root, workItemId, options = {}) {
  const config = telemetryConfig(root);
  if (!config.enabled) return null;
  const id = options.executionId ?? executionId();
  const file = executionFile(root, id);
  if (fs.existsSync(file) || loadExecutions(root).some(({ record }) => record?.executionId === id)) {
    throw new Error(`duplicate execution ID '${id}'`);
  }
  const startedAt = options.startedAt ?? nowIso();
  const identity = discoverIdentity(options.identity ?? options);
  const discoverySource = identity.discoverySource;
  delete identity.discoverySource;
  const repository = gitSnapshot(root);
  const registry = loadMetricRegistry(root);
  const types = options.classifications?.length ? [...new Set(options.classifications)] : [defaultClassification(options.workType)];
  const record = {
    schemaVersion: TELEMETRY_SCHEMA_VERSION,
    executionId: id,
    workItemId,
    status: "active",
    startedAt,
    finalizedAt: null,
    identity,
    provenance: {
      collector: "ros",
      collectorVersion: TELEMETRY_SCHEMA_VERSION,
      discoveredAt: startedAt,
      sources: [discoverySource, source("ros-git", "git", "repository-baseline")]
    },
    classification: {
      types,
      rationale: options.classificationRationale ?? null,
      evidence: options.classificationEvidence ?? [],
      rd: options.rd ?? null
    },
    capabilities: initialCapabilities(registry, identity, repository, discoverySource),
    metrics: [],
    rawTelemetry: [],
    events: [{ type: "execution.started", occurredAt: startedAt, source: source("ros-clock", "ros", "work-lifecycle") }],
    repository: { start: repository, end: null, changeSummary: null },
    scope: options.scope ?? { initial: {}, actual: {} },
    qualitySignals: [],
    links: {
      workItemId,
      parentWorkItemId: options.parentWorkItemId ?? null,
      requirements: options.requirements ?? [],
      acceptanceCriteria: options.acceptanceCriteria ?? [],
      commits: repository.commit ? [repository.commit] : [],
      pullRequests: options.pullRequests ?? [],
      experiments: options.experiments ?? [],
      researchQuestions: options.researchQuestions ?? [],
      decisions: options.decisions ?? [],
      defects: options.defects ?? [],
      dependencies: options.dependencies ?? [],
      evidence: options.evidence ?? []
    }
  };
  addMetric(root, record, {
    id: "git.baseline_dirty_files",
    value: repository.dirtyPaths.length,
    quality: "derived",
    source: source("ros-git", "git-status", "porcelain-v1"),
    collectedAt: startedAt
  });
  writeJson(file, record);
  if (options.attachToContext !== false) updateContextExecution(root, workItemId, id);
  return record;
}

function resolveExecution(root, target, { activeOnly = false } = {}) {
  const executions = loadExecutions(root);
  let candidates;
  if (target?.startsWith("EXE-")) candidates = executions.filter(({ record }) => record.executionId === target);
  else if (target) candidates = executions.filter(({ record }) => record.workItemId === target);
  else {
    const activeWork = loadContext(root).workItems.filter((item) => item.semanticState === "active" || item.semanticState === "blocked");
    if (activeWork.length !== 1) throw new Error("telemetry target is ambiguous; provide a work-item or execution ID");
    candidates = executions.filter(({ record }) => record.workItemId === activeWork[0].id);
  }
  if (activeOnly) candidates = candidates.filter(({ record }) => record.status === "active");
  if (!candidates.length) throw new Error(`telemetry execution '${target ?? "current"}' was not found${activeOnly ? " or is already finalized" : ""}`);
  candidates.sort((a, b) => String(a.record.startedAt).localeCompare(String(b.record.startedAt)));
  return candidates.at(-1);
}

function sanitizeRaw(value, currentPath = "$", redactions = []) {
  if (Array.isArray(value)) return value.map((item, index) => sanitizeRaw(item, `${currentPath}[${index}]`, redactions));
  if (value && typeof value === "object") {
    const result = {};
    for (const [key, child] of Object.entries(value)) {
      const childPath = `${currentPath}.${key}`;
      if (RAW_REDACTED_KEY.test(key)) {
        result[key] = "[REDACTED_BY_ROS]";
        redactions.push(childPath);
      } else {
        result[key] = sanitizeRaw(child, childPath, redactions);
      }
    }
    return result;
  }
  if (typeof value === "string" && value.length > MAX_RAW_STRING) return `[TRUNCATED_BY_ROS length=${value.length}]`;
  return value;
}

function leafPaths(value, prefix = "$", result = []) {
  if (Array.isArray(value)) {
    value.forEach((entry, index) => leafPaths(entry, `${prefix}[${index}]`, result));
  } else if (value && typeof value === "object") {
    for (const [key, child] of Object.entries(value)) leafPaths(child, `${prefix}.${key}`, result);
  } else {
    result.push(prefix.replace(/\[\d+\]/g, "[]"));
  }
  return [...new Set(result)].sort();
}

function metric(id, value, collectedAt, metricSource, extra = {}) {
  return { id, value, collectedAt, source: metricSource, ...extra };
}

function mappedCapability(metricId, present, metricSource, collectedAt) {
  return {
    metricId,
    status: present ? "supported-observed" : "supported-unavailable",
    reason: present ? "provider field observed" : "adapter recognizes the field but it was unavailable in this snapshot",
    source: metricSource,
    discoveredAt: collectedAt
  };
}

function adaptGeneric(input, collectedAt) {
  return {
    identity: input.identity ?? {},
    capabilities: input.capabilities ?? [],
    metrics: input.metrics ?? [],
    events: input.events ?? [],
    classification: input.classification,
    scope: input.scope,
    qualitySignals: input.qualitySignals ?? [],
    links: input.links,
    raw: input.raw ?? input.providerTelemetry ?? {},
    mappedFields: [],
    schemaVersion: input.schemaVersion ?? null,
    snapshotId: input.snapshotId,
    collectedAt: input.collectedAt ?? collectedAt,
    source: input.source ?? source("runtime-output", "generic-telemetry-envelope", "json")
  };
}

function adaptOpenAICodex(input, collectedAt) {
  const records = Array.isArray(input) ? input : input.events ?? [input];
  const completed = records.filter((entry) => entry?.type === "turn.completed" || entry?.usage);
  const metrics = [];
  const capabilities = [];
  const runtimeSource = source("runtime-output", "codex-json", "codex-exec-jsonl", { provider: "openai", runtime: "codex" });
  const fields = {
    input_tokens: "tokens.input",
    output_tokens: "tokens.output",
    cached_input_tokens: "tokens.cached_input",
    cache_write_input_tokens: "tokens.cache_write",
    reasoning_output_tokens: "tokens.reasoning",
    total_tokens: "tokens.total"
  };
  completed.forEach((entry, index) => {
    const at = entry.timestamp ?? collectedAt;
    for (const [field, metricId] of Object.entries(fields)) {
      const present = typeof entry.usage?.[field] === "number";
      capabilities.push(mappedCapability(metricId, present, runtimeSource, at));
      if (present) metrics.push(metric(metricId, entry.usage[field], at, runtimeSource, { scope: "turn", dimensions: { turnIndex: index } }));
    }
    if (typeof entry.usage?.model_context_window === "number") {
      metrics.push(metric("context.window_size", entry.usage.model_context_window, at, runtimeSource, { scope: "session" }));
    }
  });
  const identityEvent = [...records].reverse().find((entry) => entry?.server_model || entry?.model) ?? {};
  return {
    identity: { provider: "openai", runtime: "codex", model: identityEvent.server_model ?? identityEvent.model ?? null },
    capabilities,
    metrics,
    events: completed.map((entry) => ({ type: "agent.turn.completed", occurredAt: entry.timestamp ?? collectedAt, source: runtimeSource })),
    raw: input,
    mappedFields: Object.keys(fields).map((field) => `usage.${field}`),
    schemaVersion: input.schemaVersion ?? null,
    collectedAt,
    source: runtimeSource
  };
}

function adaptClaudeStatusline(input, collectedAt) {
  const runtimeSource = source("runtime-output", "claude-code-statusline", "statusline-json", { provider: "anthropic", runtime: "claude-code" });
  const metrics = [];
  const capabilities = [];
  const current = input.context_window?.current_usage;
  const mappings = [
    ["context.window_size", input.context_window?.context_window_size, "session", "observed"],
    ["context.utilization", typeof input.context_window?.used_percentage === "number" ? input.context_window.used_percentage / 100 : undefined, "session", "observed"],
    ["cost.session_cumulative", input.cost?.total_cost_usd, "session", "estimated"]
  ];
  for (const [id, value, scopeName, quality] of mappings) {
    const present = typeof value === "number";
    capabilities.push({ ...mappedCapability(id, present, runtimeSource, collectedAt), ...(quality === "estimated" && present ? { status: "estimated" } : {}) });
    if (present) metrics.push(metric(id, value, collectedAt, runtimeSource, {
      scope: scopeName,
      quality,
      confidence: quality === "estimated" ? 0.8 : null,
      ...(id.startsWith("cost.") ? { currency: "USD" } : {})
    }));
  }
  const currentMappings = {
    input_tokens: "context.current_input_tokens",
    output_tokens: "context.current_output_tokens",
    cache_creation_input_tokens: "context.current_cache_write_tokens",
    cache_read_input_tokens: "context.current_cache_read_tokens"
  };
  for (const [field, id] of Object.entries(currentMappings)) {
    const present = typeof current?.[field] === "number";
    capabilities.push(mappedCapability(id, present, runtimeSource, collectedAt));
    if (present) metrics.push(metric(id, current[field], collectedAt, runtimeSource, { scope: "session" }));
  }
  return {
    identity: {
      provider: "anthropic",
      runtime: "claude-code",
      runtimeVersion: input.version ?? null,
      model: input.model?.id ?? null,
      sessionId: input.session_id ?? null,
      agentId: input.agent?.name ?? null
    },
    capabilities,
    metrics,
    events: [],
    raw: input,
    mappedFields: ["context_window", "cost.total_cost_usd", "model.id", "version", "session_id"],
    schemaVersion: input.schemaVersion ?? null,
    collectedAt,
    source: runtimeSource
  };
}

function nestedCandidates(record) {
  return [record, record?.attributes, record?.resource?.attributes, record?.body, record?.dataPoint?.attributes].filter((value) => value && typeof value === "object");
}

function firstValue(record, names) {
  for (const candidate of nestedCandidates(record)) {
    for (const name of names) if (candidate[name] !== undefined) return candidate[name];
  }
  return undefined;
}

function numericValue(record) {
  for (const value of [record?.value, record?.sum, record?.count, record?.dataPoint?.value, record?.body?.value]) {
    if (typeof value === "number") return value;
  }
  return undefined;
}

function adaptOtel(input, collectedAt, identityDefaults = {}) {
  const records = Array.isArray(input) ? input : input.records ?? input.events ?? [input];
  const metrics = [];
  const capabilities = [];
  let provider = identityDefaults.provider ?? "unknown";
  let runtime = identityDefaults.runtime ?? "unknown";
  let model = null;
  let sessionId = null;
  for (const record of records) {
    const name = firstValue(record, ["name", "event.name", "metric.name", "instrumentation.name"]);
    const at = firstValue(record, ["timestamp", "time", "observedTimeUnixNano"]) ?? collectedAt;
    provider = firstValue(record, ["gen_ai.provider.name", "provider"]) ?? provider;
    model = firstValue(record, ["gen_ai.response.model", "gen_ai.request.model", "model"]) ?? model;
    sessionId = firstValue(record, ["session.id", "gen_ai.conversation.id", "session_id"]) ?? sessionId;
    const runtimeSource = source("runtime-output", "opentelemetry-json", "otel-json-export", { provider, runtime });
    const direct = {
      "gen_ai.usage.input_tokens": "tokens.input",
      "input_tokens": "tokens.input",
      "gen_ai.usage.output_tokens": "tokens.output",
      "output_tokens": "tokens.output",
      "cache_read_tokens": "tokens.cache_read",
      "cache_creation_tokens": "tokens.cache_write",
      "duration_ms": "time.model_ms",
      "ttft_ms": "time.first_token_ms"
    };
    for (const [field, id] of Object.entries(direct)) {
      const value = firstValue(record, [field]);
      if (typeof value === "number") metrics.push(metric(id, value, at, runtimeSource, { scope: "operation", dimensions: name ? { event: name } : {} }));
    }
    if (name === "gemini_cli.token.usage" || name === "gen_ai.client.token.usage") {
      const type = firstValue(record, ["type", "gen_ai.token.type"]);
      const id = { input: "tokens.input", output: "tokens.output", thought: "tokens.reasoning", cache: "tokens.cached_input", tool: "tokens.tool" }[type];
      const value = numericValue(record);
      if (id && typeof value === "number") metrics.push(metric(id, value, at, runtimeSource, { scope: "operation", dimensions: { tokenType: type } }));
    }
    if (["claude_code.api_request", "api_request"].includes(name)) {
      metrics.push(metric("model.requests", 1, at, runtimeSource, { scope: "operation" }));
      const success = firstValue(record, ["success"]);
      if (success === false || success === "false") metrics.push(metric("model.request_failures", 1, at, runtimeSource, { scope: "operation" }));
    }
    if (["claude_code.tool_result", "tool_result", "tool_call"].includes(name) || name === "gemini_cli.tool.call.count") {
      const count = numericValue(record) ?? 1;
      const tool = firstValue(record, ["tool_name", "function_name", "gen_ai.tool.name"]) ?? "unknown";
      metrics.push(metric("tool.calls", count, at, runtimeSource, { scope: "tool", dimensions: { toolType: tool } }));
      const success = firstValue(record, ["success"]);
      if (success === false || success === "false") metrics.push(metric("tool.failures", 1, at, runtimeSource, { scope: "tool", dimensions: { toolType: tool } }));
    }
    if (name === "gemini_cli.agent.turns") {
      const value = numericValue(record);
      if (typeof value === "number") metrics.push(metric("agent.turns", value, at, runtimeSource));
    }
    if (name === "gemini_cli.chat_compression") metrics.push(metric("context.compactions", 1, at, runtimeSource));
    if (name === "gemini_cli.memory.usage" && firstValue(record, ["memory_type"]) === "rss") {
      const value = numericValue(record);
      if (typeof value === "number") metrics.push(metric("runtime.memory_peak_bytes", value, at, runtimeSource));
    }
  }
  for (const item of metrics) capabilities.push(mappedCapability(item.id, true, item.source, item.collectedAt));
  return {
    identity: { provider, runtime, model, sessionId },
    capabilities,
    metrics,
    events: [],
    raw: input,
    mappedFields: ["attributes.gen_ai.*", "attributes.session.id", "name", "value"],
    schemaVersion: input.schemaVersion ?? null,
    collectedAt,
    source: source("runtime-output", "opentelemetry-json", "otel-json-export", { provider, runtime })
  };
}

function toolCategoryMetric(toolName) {
  const value = String(toolName ?? "").toLowerCase();
  if (/bash|shell|command|terminal|powershell/.test(value)) return "tool.shell_commands";
  if (/read|view|open_file/.test(value)) return "tool.file_reads";
  if (/write|edit|patch|replace|create_file/.test(value)) return "tool.file_writes";
  if (/search|grep|find|glob/.test(value)) return "tool.searches";
  if (/web|browser|fetch|chrome/.test(value)) return "tool.web_activity";
  if (/git|repository/.test(value)) return "tool.repository_operations";
  if (/test/.test(value)) return "tool.test_executions";
  if (/build|compile/.test(value)) return "tool.build_executions";
  if (/deploy/.test(value)) return "tool.deployments";
  if (/database|sql|query/.test(value)) return "tool.database_operations";
  if (/api|http|mcp/.test(value)) return "tool.api_operations";
  return "tool.external_service_calls";
}

function adaptHook(input, collectedAt, identityDefaults) {
  const hookName = input.hook_event_name ?? input.hookEventName ?? input.event ?? "unknown";
  const toolName = input.tool_name ?? input.toolName ?? input.tool?.name ?? "unknown";
  const runtimeSource = source("runtime-hook", `${identityDefaults.runtime}-hook`, "lifecycle-hook-json", identityDefaults);
  const metrics = [];
  const events = [{ type: `runtime.${String(hookName).replace(/[^A-Za-z0-9]+/g, "-").toLowerCase()}`, occurredAt: input.timestamp ?? collectedAt, source: runtimeSource }];
  if (/posttooluse|aftertool|toolresult/i.test(hookName)) {
    metrics.push(metric("tool.calls", 1, input.timestamp ?? collectedAt, runtimeSource, { scope: "tool", dimensions: { toolType: toolName } }));
    metrics.push(metric(toolCategoryMetric(toolName), 1, input.timestamp ?? collectedAt, runtimeSource, { scope: "tool", dimensions: { toolType: toolName } }));
    if (input.error || input.tool_response?.error || input.success === false) metrics.push(metric("tool.failures", 1, input.timestamp ?? collectedAt, runtimeSource, { scope: "tool", dimensions: { toolType: toolName } }));
  }
  if (/subagentstart/i.test(hookName)) metrics.push(metric("agent.subagents_spawned", 1, input.timestamp ?? collectedAt, runtimeSource));
  if (/postcompact/i.test(hookName)) metrics.push(metric("context.compactions", 1, input.timestamp ?? collectedAt, runtimeSource));
  if (/permissionrequest/i.test(hookName)) metrics.push(metric("agent.approvals_requested", 1, input.timestamp ?? collectedAt, runtimeSource));
  if (/permissiondenied/i.test(hookName)) metrics.push(metric("agent.approvals_denied", 1, input.timestamp ?? collectedAt, runtimeSource));
  return {
    identity: { ...identityDefaults, sessionId: input.session_id ?? null, model: input.model?.id ?? input.model ?? null, agentId: input.agent_type ?? null },
    capabilities: metrics.map((item) => mappedCapability(item.id, true, runtimeSource, item.collectedAt)),
    metrics,
    events,
    raw: input,
    mappedFields: ["hook_event_name", "tool_name", "timestamp", "session_id", "model", "agent_type"],
    schemaVersion: input.schemaVersion ?? null,
    collectedAt: input.timestamp ?? collectedAt,
    source: runtimeSource
  };
}

function adaptInput(adapter, input, collectedAt) {
  if (!TELEMETRY_ADAPTERS.includes(adapter)) throw new Error(`unknown telemetry adapter '${adapter}'`);
  if (adapter === "generic") return adaptGeneric(input, collectedAt);
  if (adapter === "openai-codex") return adaptOpenAICodex(input, collectedAt);
  if (adapter === "anthropic-claude-statusline") return adaptClaudeStatusline(input, collectedAt);
  if (adapter === "anthropic-claude-hook") return adaptHook(input, collectedAt, { provider: "anthropic", runtime: "claude-code" });
  if (adapter === "google-gemini-hook") return adaptHook(input, collectedAt, { provider: "google", runtime: "gemini-cli" });
  if (adapter === "github-copilot-hook") return adaptHook(input, collectedAt, { provider: "github", runtime: "copilot" });
  if (adapter === "anthropic-claude-otel") return adaptOtel(input, collectedAt, { provider: "anthropic", runtime: "claude-code" });
  if (adapter === "google-gemini-otel") return adaptOtel(input, collectedAt, { provider: "google", runtime: "gemini-cli" });
  if (adapter === "github-copilot-otel") return adaptOtel(input, collectedAt, { provider: "github", runtime: "copilot" });
  return adaptOtel(input, collectedAt);
}

function mergeObject(target, incoming) {
  if (!incoming || typeof incoming !== "object") return target;
  for (const [key, value] of Object.entries(incoming)) {
    if (value !== undefined && value !== null) target[key] = value;
  }
  return target;
}

export function ingestTelemetry(root, target, input, { adapter = "generic", collectedAt = nowIso() } = {}) {
  const resolved = resolveExecution(root, target, { activeOnly: true });
  const record = ensureRecordDefaults(resolved.record);
  const adapted = adaptInput(adapter, input, collectedAt);
  const snapshotId = adapted.snapshotId ?? `SNAP-${digest({ adapter, input })}`;
  if (record.rawTelemetry.some((snapshot) => snapshot.snapshotId === snapshotId)) return record;
  mergeObject(record.identity, adapted.identity);
  for (const capability of adapted.capabilities ?? []) upsertCapability(record, capability);
  for (const item of adapted.metrics ?? []) addMetric(root, record, item, { source: adapted.source, collectedAt: adapted.collectedAt });
  for (const event of adapted.events ?? []) {
    const normalized = { eventId: event.eventId ?? `TEVT-${digest(event)}`, ...event };
    if (!record.events.some((existing) => existing.eventId === normalized.eventId)) record.events.push(normalized);
  }
  if (adapted.classification) record.classification = { ...record.classification, ...adapted.classification };
  if (adapted.scope) record.scope = { ...record.scope, ...adapted.scope };
  if (adapted.links) {
    for (const [key, values] of Object.entries(adapted.links)) {
      if (Array.isArray(values)) record.links[key] = [...new Set([...(record.links[key] ?? []), ...values])];
      else if (values !== undefined) record.links[key] = values;
    }
  }
  for (const signal of adapted.qualitySignals ?? []) {
    const normalized = { signalId: signal.signalId ?? `QS-${digest(signal)}`, ...signal };
    if (!record.qualitySignals.some((existing) => existing.signalId === normalized.signalId)) record.qualitySignals.push(normalized);
  }
  const config = telemetryConfig(root);
  if (config.allowRawTelemetry) {
    const redactions = [];
    const payload = sanitizeRaw(adapted.raw, "$", redactions);
    const serializedBytes = Buffer.byteLength(JSON.stringify(payload));
    if (serializedBytes > config.maxRawPayloadBytes) throw new Error(`sanitized telemetry payload exceeds ${config.maxRawPayloadBytes} bytes`);
    const fields = leafPaths(payload);
    const mapped = new Set(adapted.mappedFields ?? []);
    const discoveredFields = fields.filter((field) => ![...mapped].some((known) => field.includes(known.replace("*", ""))));
    record.rawTelemetry.push({
      snapshotId,
      adapter,
      schemaVersion: adapted.schemaVersion ?? null,
      collectedAt: adapted.collectedAt,
      source: adapted.source,
      payload,
      discoveredFields,
      redactions
    });
    for (const field of discoveredFields) upsertCapability(record, {
      providerField: field,
      status: "unknown",
      reason: "provider field preserved but not normalized by this adapter version",
      discoveredAt: adapted.collectedAt,
      source: adapted.source
    });
    if (redactions.length) addMetric(root, record, metric("telemetry.redactions", redactions.length, adapted.collectedAt, source("calculated", "ros-raw-filter", "sensitive-key-redaction"), { quality: "derived" }));
    if (discoveredFields.length) addMetric(root, record, metric("telemetry.unknown_fields", discoveredFields.length, adapted.collectedAt, source("calculated", "ros-field-discovery", "unmapped-leaf-count"), { quality: "derived" }));
  }
  record.provenance.sources.push(adapted.source);
  record.provenance.sources = [...new Map(record.provenance.sources.map((item) => [digest(item), item])).values()];
  writeJson(resolved.file, record);
  return record;
}

export function recordTelemetryMetric(root, target, input) {
  const resolved = resolveExecution(root, target, { activeOnly: true });
  ensureRecordDefaults(resolved.record);
  addMetric(root, resolved.record, input);
  writeJson(resolved.file, resolved.record);
  return resolved.record;
}

export function recordTelemetryLifecycle(root, workItemId, type, fields = {}) {
  const active = loadExecutions(root).filter(({ record }) => record.workItemId === workItemId && record.status === "active");
  const at = fields.occurredAt ?? nowIso();
  for (const entry of active) {
    ensureRecordDefaults(entry.record);
    const event = { type: `work.${type}`, occurredAt: at, reason: fields.reason ?? null, source: source("ros-clock", "ros", "work-lifecycle") };
    event.eventId = `TEVT-${digest(event)}`;
    if (!entry.record.events.some((existing) => existing.eventId === event.eventId)) entry.record.events.push(event);
    if (type === "blocked") addMetric(root, entry.record, metric("agent.interruptions", 1, at, source("calculated", "ros-work-protocol", "blocked-transition"), { quality: "derived" }));
    if (type === "resumed") addMetric(root, entry.record, metric("agent.resumes", 1, at, source("calculated", "ros-work-protocol", "resume-transition"), { quality: "derived" }));
    writeJson(entry.file, entry.record);
  }
}

function blockedDuration(events, finalizedAt) {
  let blockedAt = null;
  let total = 0;
  for (const event of [...events].sort((a, b) => String(a.occurredAt).localeCompare(String(b.occurredAt)))) {
    if (event.type === "work.blocked" && !blockedAt) blockedAt = Date.parse(event.occurredAt);
    if (event.type === "work.resumed" && blockedAt !== null) {
      total += Math.max(0, Date.parse(event.occurredAt) - blockedAt);
      blockedAt = null;
    }
  }
  if (blockedAt !== null) total += Math.max(0, Date.parse(finalizedAt) - blockedAt);
  return total;
}

const GIT_CHANGE_METRICS = [
  "git.commits_created", "git.files_added", "git.files_modified", "git.files_deleted", "git.files_renamed",
  "git.binary_files_changed", "git.lines_added", "git.lines_deleted", "tests.added", "tests.modified",
  "tests.removed", "documentation.files_changed"
];

export function finalizeExecution(root, target, options = {}) {
  const resolved = resolveExecution(root, target);
  const record = resolved.record;
  if (record.status === "finalized") return record;
  if (options.input) ingestTelemetry(root, record.executionId, options.input, { adapter: options.adapter ?? "generic" });
  const refreshed = ensureRecordDefaults(readJson(resolved.file));
  const finalizedAt = options.finalizedAt ?? nowIso();
  refreshed.repository.end = gitSnapshot(root);
  const summary = cleanBaselineChanges(root, refreshed.repository.start);
  refreshed.repository.changeSummary = summary;
  addMetric(root, refreshed, metric("time.wall_ms", Math.max(0, Date.parse(finalizedAt) - Date.parse(refreshed.startedAt)), finalizedAt, source("ros-clock", "ros", "timestamp-difference"), { quality: "derived" }));
  addMetric(root, refreshed, metric("time.blocked_ms", blockedDuration(refreshed.events, finalizedAt), finalizedAt, source("calculated", "ros-work-lifecycle", "block-resume-intervals"), { quality: "derived" }));
  addMetric(root, refreshed, metric("git.ending_dirty_files", refreshed.repository.end.dirtyPaths.length, finalizedAt, source("ros-git", "git-status", "porcelain-v1"), { quality: "derived" }));
  if (summary.available) {
    const values = {
      "git.commits_created": summary.commits,
      "git.files_added": summary.counts.added,
      "git.files_modified": summary.counts.modified,
      "git.files_deleted": summary.counts.deleted,
      "git.files_renamed": summary.counts.renamed,
      "git.binary_files_changed": summary.binaryFiles,
      "git.lines_added": summary.linesAdded,
      "git.lines_deleted": summary.linesDeleted,
      "tests.added": summary.tests.added,
      "tests.modified": summary.tests.modified,
      "tests.removed": summary.tests.removed,
      "documentation.files_changed": summary.documentationFilesChanged
    };
    for (const [id, value] of Object.entries(values)) addMetric(root, refreshed, metric(id, value, finalizedAt, source("ros-git", "git-diff", summary.mechanism), { quality: "derived" }));
    refreshed.links.commits = [...new Set([...(refreshed.links.commits ?? []), summary.startCommit, summary.endCommit].filter(Boolean))];
  } else {
    for (const id of GIT_CHANGE_METRICS) upsertCapability(refreshed, {
      metricId: id,
      status: "supported-unavailable",
      reason: `execution attribution unavailable: ${summary.reason}`,
      discoveredAt: finalizedAt,
      source: source("ros-git", "git-diff", "precondition-check")
    });
  }
  refreshed.status = "finalized";
  refreshed.finalizedAt = finalizedAt;
  refreshed.events.push({ type: "execution.finalized", occurredAt: finalizedAt, source: source("ros-clock", "ros", "work-lifecycle"), eventId: `TEVT-${digest({ type: "execution.finalized", finalizedAt })}` });
  writeJson(resolved.file, refreshed);
  return refreshed;
}

export function finalizeWorkExecutions(root, workItemId, options = {}) {
  return loadExecutions(root)
    .filter(({ record }) => record.workItemId === workItemId && record.status === "active")
    .map(({ record }) => finalizeExecution(root, record.executionId, options));
}

export function showTelemetry(root, target) {
  if (!target) return loadExecutions(root).map(({ record }) => record);
  if (target.startsWith("EXE-")) return resolveExecution(root, target).record;
  return loadExecutions(root).filter(({ record }) => record.workItemId === target).map(({ record }) => record);
}

function dimensionsKey(metricItem) {
  return JSON.stringify(stable(metricItem.dimensions ?? {}));
}

export function summarizeTelemetry(root, workItemId) {
  const records = loadExecutions(root).filter(({ record }) => !workItemId || record.workItemId === workItemId).map(({ record }) => record);
  const registry = loadMetricRegistry(root).metrics;
  const grouped = new Map();
  for (const record of records) {
    for (const item of record.metrics ?? []) {
      const key = `${item.id}\0${item.unit}\0${item.currency ?? ""}\0${dimensionsKey(item)}`;
      if (!grouped.has(key)) grouped.set(key, []);
      grouped.get(key).push({ record, item });
    }
  }
  const metrics = [];
  for (const values of grouped.values()) {
    const definition = registry.get(values[0].item.id);
    const aggregation = definition?.aggregation ?? values[0].item.aggregation;
    let value = null;
    let note = null;
    if (aggregation === "sum") value = values.reduce((total, entry) => total + entry.item.value, 0);
    else if (aggregation === "maximum") value = Math.max(...values.map((entry) => entry.item.value));
    else if (aggregation === "latest-per-session") {
      const bySession = new Map();
      for (const entry of values) {
        const session = entry.record.identity?.sessionId ?? `execution:${entry.record.executionId}`;
        const previous = bySession.get(session);
        if (!previous || String(previous.item.collectedAt).localeCompare(String(entry.item.collectedAt)) < 0) bySession.set(session, entry);
      }
      value = [...bySession.values()].reduce((total, entry) => total + entry.item.value, 0);
      note = "latest value per unique provider session; cumulative snapshots are not summed";
    } else if (aggregation === "none") {
      note = "not aggregated; inspect per-execution measurements";
    } else {
      const latest = [...values].sort((a, b) => String(a.item.collectedAt).localeCompare(String(b.item.collectedAt))).at(-1);
      value = latest.item.value;
    }
    const first = values[0].item;
    metrics.push({ id: first.id, value, unit: first.unit, currency: first.currency ?? null, dimensions: first.dimensions ?? {}, aggregation, measurements: values.length, note });
  }
  return {
    schemaVersion: TELEMETRY_SCHEMA_VERSION,
    workItemId: workItemId ?? null,
    executionCount: records.length,
    providers: [...new Set(records.map((record) => record.identity?.provider ?? "unknown"))].sort(),
    runtimes: [...new Set(records.map((record) => record.identity?.runtime ?? "unknown"))].sort(),
    metrics: metrics.sort((a, b) => a.id.localeCompare(b.id))
  };
}

function finding(file, field, message) {
  return { path: file, field, message };
}

function validClassification(value) {
  return WORK_CLASSIFICATIONS.has(value) || /^x-[a-z0-9][a-z0-9._-]*(?:\/[a-z0-9][a-z0-9._-]*)?$/.test(value);
}

export function telemetryFindings(root, context = loadContext(root)) {
  const config = telemetryConfig(root);
  const findings = [];
  if (!config.enabled) {
    if (!config.disabledReason) findings.push(finding("ros.json", "telemetry.disabledReason", "disabled telemetry requires an explicit reason"));
    return findings;
  }
  let registry;
  try {
    registry = loadMetricRegistry(root).metrics;
  } catch (error) {
    findings.push(finding(config.metricRegistry, "schemaVersion", error.message));
    return findings;
  }
  const seen = new Map();
  const records = [];
  for (const file of executionFiles(root)) {
    const relative = path.relative(root, file).split(path.sep).join("/");
    let record;
    try {
      record = readJson(file);
    } catch (error) {
      findings.push(finding(relative, "", `malformed telemetry JSON: ${error.message}`));
      continue;
    }
    records.push({ relative, record });
    if (record.schemaVersion !== TELEMETRY_SCHEMA_VERSION) findings.push(finding(relative, "schemaVersion", `unsupported telemetry schema version '${record.schemaVersion ?? "missing"}'`));
    if (!record.executionId) findings.push(finding(relative, "executionId", "execution identity is required"));
    else {
      if (seen.has(record.executionId)) findings.push(finding(relative, "executionId", `duplicate execution ID also in ${seen.get(record.executionId)}`));
      seen.set(record.executionId, relative);
      if (path.basename(file) !== `${record.executionId}.json`) findings.push(finding(relative, "executionId", "execution filename must match executionId"));
    }
    if (!record.workItemId) findings.push(finding(relative, "workItemId", "work-item linkage is required"));
    if (!record.identity?.provider || !record.identity?.runtime) findings.push(finding(relative, "identity", "provider and runtime identity are required; use 'unknown' only when discovery cannot resolve them"));
    if (record.provenance?.collector !== "ros" || !record.provenance?.collectorVersion || !isTimestamp(record.provenance?.discoveredAt)) findings.push(finding(relative, "provenance", "collector, collectorVersion, and discovery timestamp are required"));
    if (!isTimestamp(record.startedAt)) findings.push(finding(relative, "startedAt", "valid execution start timestamp is required"));
    if (!["active", "finalized"].includes(record.status)) findings.push(finding(relative, "status", `invalid execution status '${record.status}'`));
    if (record.status === "finalized" && !isTimestamp(record.finalizedAt)) findings.push(finding(relative, "finalizedAt", "finalized execution requires a completion timestamp"));
    if (record.finalizedAt && isTimestamp(record.startedAt) && Date.parse(record.finalizedAt) < Date.parse(record.startedAt)) findings.push(finding(relative, "finalizedAt", "execution completion precedes its start"));
    const types = record.classification?.types;
    if (!Array.isArray(types) || !types.length) findings.push(finding(relative, "classification.types", "at least one work classification is required"));
    else for (const type of types) if (!validClassification(type)) findings.push(finding(relative, "classification.types", `invalid work classification '${type}'`));
    if (types?.includes("research-development")) {
      const rd = record.classification?.rd;
      if (!rd || !["researchQuestion", "hypothesis", "technicalUncertainty", "experimentalObjective", "knowledgeGap"].some((key) => rd[key])) {
        findings.push(finding(relative, "classification.rd", "research-development classification requires factual uncertainty or experimental context"));
      }
    }
    const capabilityByMetric = new Map();
    for (const [index, capability] of (record.capabilities ?? []).entries()) {
      if (!CAPABILITY_STATUSES.has(capability.status)) findings.push(finding(relative, `capabilities[${index}].status`, `invalid capability status '${capability.status}'`));
      if (!capability.metricId && !capability.providerField) findings.push(finding(relative, `capabilities[${index}]`, "capability requires metricId or providerField"));
      if (!capability.source?.type || !capability.source?.name || !capability.source?.mechanism) findings.push(finding(relative, `capabilities[${index}].source`, "capability source provenance is required"));
      if (capability.metricId) capabilityByMetric.set(capability.metricId, capability);
    }
    const unitByMetric = new Map();
    for (const [index, item] of (record.metrics ?? []).entries()) {
      const definition = registry.get(item.id);
      const field = `metrics[${index}]`;
      if (!definition) findings.push(finding(relative, `${field}.id`, `unknown normalized metric '${item.id}'; retain it as raw telemetry`));
      if (typeof item.value !== "number" || !Number.isFinite(item.value) || item.value < 0) findings.push(finding(relative, `${field}.value`, "metric value must be a finite non-negative number"));
      if (!QUALITIES.has(item.quality)) findings.push(finding(relative, `${field}.quality`, `invalid metric quality '${item.quality}'`));
      if (item.quality === "estimated" && !(typeof item.confidence === "number" && item.confidence >= 0 && item.confidence <= 1)) findings.push(finding(relative, `${field}.confidence`, "estimated metric requires confidence from zero through one"));
      if (!item.source?.type || !item.source?.name || !item.source?.mechanism) findings.push(finding(relative, `${field}.source`, "metric source provenance is required"));
      else if (!SOURCE_TYPES.has(item.source.type)) findings.push(finding(relative, `${field}.source.type`, `unknown source type '${item.source.type}'`));
      if (!isTimestamp(item.collectedAt)) findings.push(finding(relative, `${field}.collectedAt`, "metric collection timestamp is required"));
      if (definition && item.aggregation !== definition.aggregation) findings.push(finding(relative, `${field}.aggregation`, `metric aggregation must be '${definition.aggregation}'`));
      if (definition?.unit === "currency") {
        if (item.unit !== "currency" || !/^[A-Z]{3}$/.test(item.currency ?? "")) findings.push(finding(relative, `${field}.unit`, "cost metric requires unit 'currency' and an ISO-style three-letter currency"));
      } else if (definition && item.unit !== definition.unit) findings.push(finding(relative, `${field}.unit`, `metric unit must be '${definition.unit}'`));
      if (unitByMetric.has(item.id) && unitByMetric.get(item.id) !== `${item.unit}:${item.currency ?? ""}`) findings.push(finding(relative, `${field}.unit`, "conflicting units for the same normalized metric"));
      unitByMetric.set(item.id, `${item.unit}:${item.currency ?? ""}`);
      if (definition?.collection === "ros-derived" && item.quality !== "derived") findings.push(finding(relative, `${field}.quality`, "ROS-derived metric cannot be represented as observed or estimated"));
      if ((item.id === "context.utilization" || item.id === "runtime.cpu_utilization") && item.value > 1) findings.push(finding(relative, `${field}.value`, "ratio cannot exceed one"));
      if (definition?.aggregation === "latest-per-session" && item.scope === "session" && !record.identity?.sessionId) findings.push(finding(relative, `${field}.scope`, "session-cumulative or session-gauge metric requires a session ID"));
      const capability = capabilityByMetric.get(item.id);
      if (capability?.status === "unsupported") findings.push(finding(relative, field, "metric is present while its capability is marked unsupported"));
    }
    for (const [index, signal] of (record.qualitySignals ?? []).entries()) {
      if (!QUALITY_DETECTORS.has(signal.detector) && !String(signal.detector ?? "").startsWith("x-")) findings.push(finding(relative, `qualitySignals[${index}].detector`, `invalid quality-signal detector '${signal.detector}'`));
      if (!signal.source) findings.push(finding(relative, `qualitySignals[${index}].source`, "quality signal requires provenance"));
    }
    for (const [index, snapshot] of (record.rawTelemetry ?? []).entries()) {
      if (!snapshot.snapshotId || !snapshot.adapter || !snapshot.source || !isTimestamp(snapshot.collectedAt)) findings.push(finding(relative, `rawTelemetry[${index}]`, "raw snapshot requires identity, adapter, source, and timestamp"));
      if (Buffer.byteLength(JSON.stringify(snapshot.payload)) > config.maxRawPayloadBytes) findings.push(finding(relative, `rawTelemetry[${index}].payload`, "raw snapshot exceeds configured storage limit"));
    }
  }
  const byId = new Map(records.map((entry) => [entry.record.executionId, entry]));
  for (const item of context.workItems ?? []) {
    for (const id of item.telemetryExecutionIds ?? []) {
      if (!byId.has(id)) findings.push(finding(".ros/context/current.json", "telemetryExecutionIds", `work item '${item.id}' links missing execution '${id}'`));
    }
    if (config.requireFinalization && item.semanticState === "complete" && (item.telemetryExecutionIds ?? []).length) {
      for (const id of item.telemetryExecutionIds) if (byId.get(id)?.record.status !== "finalized") findings.push(finding(byId.get(id)?.relative ?? ".ros/context/current.json", "status", `completed work item '${item.id}' has unfinalized telemetry`));
    }
  }
  return findings;
}

export function readTelemetryInput(file, maxBytes = 262_144) {
  const raw = file === "-" ? fs.readFileSync(0, "utf8") : fs.readFileSync(file, "utf8");
  if (Buffer.byteLength(raw) > maxBytes) throw new Error(`telemetry input exceeds ${maxBytes} bytes`);
  const trimmed = raw.trim();
  if (!trimmed) throw new Error("telemetry input is empty");
  try {
    return JSON.parse(trimmed);
  } catch {
    try {
      return trimmed.split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line));
    } catch (error) {
      throw new Error(`telemetry input must be JSON or JSON Lines: ${error.message}`);
    }
  }
}

export function configuredTelemetry(root) {
  return telemetryConfig(root);
}
