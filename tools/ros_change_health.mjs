import fs from "node:fs";
import path from "node:path";
import { runGitText } from "./ros_git.mjs";
import { readJson, withFileLock, writeJson } from "./ros_persistence.mjs";

const DEFAULT_POLICY = {
  schemaVersion: "1.0.0",
  enabled: true,
  historyPath: ".ros/telemetry/change-history.json",
  historyWindow: 20,
  maxHistoryUpdates: 200,
  lineBucketSize: 25,
  thresholds: {
    filesChanged: { warning: 25, error: 60 },
    linesChanged: { warning: 800, error: 2000 },
    largestFileChurn: { warning: 300, error: 800 },
    largestChangedFileLines: { warning: 800, error: 1500 },
    hunksChanged: { warning: 30, error: 80 },
    maxHunksPerFile: { warning: 10, error: 25 },
    repeatFileTouches: { warning: 5, error: 10 },
    repeatRegionTouches: { warning: 3, error: 6 }
  }
};

const CODES = {
  filesChanged: "PRAXIS-CHG-001",
  linesChanged: "PRAXIS-CHG-002",
  largestFileChurn: "PRAXIS-CHG-003",
  largestChangedFileLines: "PRAXIS-CHG-004",
  hunksChanged: "PRAXIS-CHG-005",
  maxHunksPerFile: "PRAXIS-CHG-006",
  repeatFileTouches: "PRAXIS-CHG-007",
  repeatRegionTouches: "PRAXIS-CHG-008"
};

const REMEDIATION = {
  filesChanged: "Review whether the change should be decomposed into independently verifiable updates.",
  linesChanged: "Review the change boundary, evidence, and test scope; split unrelated work.",
  largestFileChurn: "Inspect the highest-churn file for mixed responsibilities or unstable boundaries.",
  largestChangedFileLines: "Review the largest changed file for decomposition opportunities.",
  hunksChanged: "Review whether widely scattered edits indicate excessive scope.",
  maxHunksPerFile: "Inspect the file with the most separated edits for cohesion and testability.",
  repeatFileTouches: "Review repeatedly changed files for architectural hotspots, ownership friction, or missing abstractions.",
  repeatRegionTouches: "Review repeatedly changed line regions for unstable logic or concentrated maintenance risk."
};

const SOURCE_EXTENSIONS = new Set([
  ".fs", ".fsx", ".cs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs",
  ".py", ".java", ".kt", ".kts", ".go", ".rs", ".c", ".cc", ".cpp", ".cxx",
  ".h", ".hh", ".hpp", ".swift", ".scala", ".rb", ".php", ".sql", ".html",
  ".htm", ".css", ".scss", ".sass", ".less", ".razor", ".vue", ".svelte"
]);

function repositoryConfig(root) {
  return readJson(path.join(root, "ros.json"), {});
}

export function loadChangeHealthPolicy(root) {
  const config = repositoryConfig(root);
  const relative = config.telemetry?.changeHealthPolicy ?? "telemetry/change-health.json";
  const file = path.resolve(root, relative);
  if (!fs.existsSync(file)) return { ...structuredClone(DEFAULT_POLICY), file: relative };

  const raw = readJson(file);
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    throw new Error("change-health policy must be a JSON object: " + relative);
  }

  const thresholds = {};
  for (const [name, defaults] of Object.entries(DEFAULT_POLICY.thresholds)) {
    const configured = raw.thresholds?.[name] ?? {};
    const warning = configured.warning === null ? null : Number.isInteger(configured.warning) ? configured.warning : defaults.warning;
    const error = configured.error === null ? null : Number.isInteger(configured.error) ? configured.error : defaults.error;
    if ((warning !== null && warning < 0) || (error !== null && error < 0)) {
      throw new Error("change-health threshold '" + name + "' cannot be negative");
    }
    if (warning !== null && error !== null && warning > error) {
      throw new Error("change-health threshold '" + name + "' warning cannot exceed error");
    }
    thresholds[name] = { warning, error };
  }

  const policy = {
    schemaVersion: "1.0.0",
    enabled: raw.enabled !== false,
    historyPath: typeof raw.historyPath === "string" ? raw.historyPath : DEFAULT_POLICY.historyPath,
    historyWindow: Number.isInteger(raw.historyWindow) ? raw.historyWindow : DEFAULT_POLICY.historyWindow,
    maxHistoryUpdates: Number.isInteger(raw.maxHistoryUpdates) ? raw.maxHistoryUpdates : DEFAULT_POLICY.maxHistoryUpdates,
    lineBucketSize: Number.isInteger(raw.lineBucketSize) ? raw.lineBucketSize : DEFAULT_POLICY.lineBucketSize,
    thresholds,
    file: relative
  };

  if (policy.historyWindow < 1) throw new Error("change-health historyWindow must be at least 1");
  if (policy.maxHistoryUpdates < 1) throw new Error("change-health maxHistoryUpdates must be at least 1");
  if (policy.lineBucketSize < 1) throw new Error("change-health lineBucketSize must be at least 1");
  return policy;
}

export function lineBuckets(bucketSize, oldStart, oldLines, newStart, newLines) {
  const size = Math.max(1, bucketSize);
  const start = newLines > 0 ? newStart : oldStart;
  const length = Math.max(1, newLines > 0 ? newLines : oldLines);
  const first = Math.max(0, Math.floor((Math.max(1, start) - 1) / size));
  const last = Math.max(first, Math.floor((Math.max(1, start) + length - 2) / size));
  return Array.from({ length: last - first + 1 }, (_, index) => first + index);
}

function decodeDiffPath(value) {
  const trimmed = value.trim();
  if (trimmed === "/dev/null") return null;
  const unprefixed = /^(?:a|b)\//.test(trimmed) ? trimmed.slice(2) : trimmed;
  if (unprefixed.startsWith('"') && unprefixed.endsWith('"')) {
    try { return JSON.parse(unprefixed); } catch { return unprefixed.slice(1, -1); }
  }
  return unprefixed;
}

export function parseChangeHunks(text, bucketSize, ignored = () => false) {
  const result = new Map();
  let oldPath = null;
  let newPath = null;
  const hunkPattern = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/;

  for (const line of text.split(/\r?\n/)) {
    if (line.startsWith("--- ")) oldPath = decodeDiffPath(line.slice(4));
    else if (line.startsWith("+++ ")) newPath = decodeDiffPath(line.slice(4));
    else if (line.startsWith("@@ ")) {
      const match = hunkPattern.exec(line);
      if (!match) continue;
      const target = newPath ?? oldPath;
      if (!target || ignored(target)) continue;
      const oldStart = Number(match[1]);
      const oldLines = match[2] === undefined ? 1 : Number(match[2]);
      const newStart = Number(match[3]);
      const newLines = match[4] === undefined ? 1 : Number(match[4]);
      const hunk = {
        oldStart, oldLines, newStart, newLines,
        buckets: lineBuckets(bucketSize, oldStart, oldLines, newStart, newLines)
      };
      result.set(target, [...(result.get(target) ?? []), hunk]);
    }
  }
  return result;
}

function readHistory(root, policy) {
  const file = path.resolve(root, policy.historyPath);
  const history = readJson(file, { schemaVersion: "1.0.0", historyOmitted: 0, updates: [] });
  if (!history || history.schemaVersion !== "1.0.0" || !Array.isArray(history.updates)) {
    throw new Error("change-health history is missing or unsupported: " + policy.historyPath);
  }
  return { file, history };
}

function aliases(file) {
  return new Set([file.path, file.from].filter(Boolean));
}

function intersects(left, right) {
  for (const value of left) if (right.has(value)) return true;
  return false;
}

function recentTouchCounts(policy, history, file, buckets) {
  const recent = history.updates.slice(-policy.historyWindow);
  const names = aliases(file);
  const fileTouches = 1 + recent.filter((update) =>
    (update.files ?? []).some((previous) => intersects(names, aliases(previous)))).length;
  const regionTouches = buckets.length
    ? Math.max(...buckets.map((bucket) => 1 + recent.filter((update) =>
        (update.files ?? []).some((previous) =>
          intersects(names, aliases(previous)) &&
          (previous.hunks ?? []).some((hunk) => (hunk.buckets ?? []).includes(bucket)))).length))
    : 0;
  return { fileTouches, regionTouches };
}

function isSourceFile(file, isTestFile, isDocumentation) {
  return !isTestFile(file) && !isDocumentation(file) && SOURCE_EXTENSIONS.has(path.extname(file).toLowerCase());
}

function evaluate(policy, metrics) {
  if (!policy.enabled) return [];
  const findings = [];
  for (const [metric, threshold] of Object.entries(policy.thresholds)) {
    const actual = metrics[metric] ?? 0;
    let severity = null;
    let limit = null;
    if (threshold.error !== null && actual > threshold.error) {
      severity = "error"; limit = threshold.error;
    } else if (threshold.warning !== null && actual > threshold.warning) {
      severity = "warning"; limit = threshold.warning;
    }
    if (!severity) continue;
    findings.push({
      code: CODES[metric],
      metric,
      severity,
      actual,
      threshold: limit,
      message: metric + " is " + actual + ", exceeding the " + severity + " threshold of " + limit + ".",
      remediation: REMEDIATION[metric]
    });
  }
  return findings;
}

function appendHistory(policy, history, update) {
  const updates = [...history.updates.filter((entry) => entry.executionId !== update.executionId), update];
  const overflow = Math.max(0, updates.length - policy.maxHistoryUpdates);
  return {
    schemaVersion: "1.0.0",
    historyOmitted: (history.historyOmitted ?? 0) + overflow,
    updates: overflow ? updates.slice(overflow) : updates
  };
}

function countTextLines(file) {
  try {
    if (!fs.existsSync(file)) return null;
    const content = fs.readFileSync(file);
    if (content.includes(0)) return null;
    if (!content.length) return 0;
    const text = content.toString("utf8");
    return text.split("\n").length - (text.endsWith("\n") ? 1 : 0);
  } catch {
    return null;
  }
}

export function captureChangeHealth(root, record, summary, finalizedAt, options) {
  const policy = loadChangeHealthPolicy(root);
  if (!policy.enabled) {
    return { enabled: false, metrics: null, findings: [],
      node: { schemaVersion: "1.0.0", enabled: false } };
  }

  const unified = runGitText(root, ["diff", "--unified=0", "--no-color", "--no-ext-diff", "--find-renames", summary.startCommit]);
  if (unified.outcome !== "success") {
    throw new Error("change-health hunk capture failed: " + unified.failure.message);
  }
  const hunkMap = parseChangeHunks(unified.value, policy.lineBucketSize, options.ignored);
  const observed = readHistory(root, policy);

  return withFileLock(root, "telemetry-change-history", () => {
    const history = readJson(observed.file, observed.history);
    if (!history || history.schemaVersion !== "1.0.0" || !Array.isArray(history.updates)) {
      throw new Error("change-health history is missing or unsupported: " + policy.historyPath);
    }

    const files = summary.paths.map((entry) => {
      const currentLines = entry.status === "D" ? null : countTextLines(path.join(root, entry.path));
      const linesAdded = entry.untracked ? currentLines : entry.lineStats?.added ?? null;
      const linesDeleted = entry.untracked ? 0 : entry.lineStats?.deleted ?? null;
      const churn = Number.isInteger(linesAdded) && Number.isInteger(linesDeleted) ? linesAdded + linesDeleted : null;
      let hunks = hunkMap.get(entry.path) ?? [];
      if (entry.untracked && currentLines > 0 && !hunks.length) {
        hunks = [{ oldStart: 0, oldLines: 0, newStart: 1, newLines: currentLines,
          buckets: lineBuckets(policy.lineBucketSize, 0, 0, 1, currentLines) }];
      }
      const buckets = [...new Set(hunks.flatMap((hunk) => hunk.buckets))];
      const touches = recentTouchCounts(policy, history, entry, buckets);
      return {
        path: entry.path,
        from: entry.from ?? null,
        status: entry.status,
        linesAdded,
        linesDeleted,
        churn,
        currentLines,
        hunks,
        recentTouches: touches.fileTouches,
        maxRegionTouches: touches.regionTouches
      };
    });

    const metrics = {
      filesChanged: files.length,
      sourceFilesChanged: files.filter((file) => isSourceFile(file.path, options.isTestFile, options.isDocumentation)).length,
      testFilesChanged: files.filter((file) => options.isTestFile(file.path)).length,
      documentationFilesChanged: summary.documentationFilesChanged,
      linesAdded: summary.linesAdded,
      linesDeleted: summary.linesDeleted,
      linesChanged: summary.linesAdded + summary.linesDeleted,
      netLines: summary.linesAdded - summary.linesDeleted,
      hunksChanged: files.reduce((sum, file) => sum + file.hunks.length, 0),
      maxHunksPerFile: files.reduce((max, file) => Math.max(max, file.hunks.length), 0),
      largestFileChurn: files.reduce((max, file) => Math.max(max, file.churn ?? 0), 0),
      largestChangedFileLines: files.reduce((max, file) => Math.max(max, file.currentLines ?? 0), 0),
      repeatFileTouches: files.reduce((max, file) => Math.max(max, file.recentTouches), 0),
      repeatRegionTouches: files.reduce((max, file) => Math.max(max, file.maxRegionTouches), 0)
    };

    const findings = evaluate(policy, metrics);
    const update = {
      executionId: record.executionId,
      workItemId: record.workItemId,
      finalizedAt,
      startCommit: summary.startCommit,
      endCommit: summary.endCommit,
      files: files.map((file) => ({
        path: file.path,
        status: file.status,
        from: file.from,
        hunks: file.hunks.map((hunk) => ({
          oldStart: hunk.oldStart, oldLines: hunk.oldLines,
          newStart: hunk.newStart, newLines: hunk.newLines, buckets: hunk.buckets
        }))
      }))
    };
    const nextHistory = appendHistory(policy, history, update);
    writeJson(observed.file, nextHistory);

    const status = findings.some((finding) => finding.severity === "error") ? "error"
      : findings.some((finding) => finding.severity === "warning") ? "warning" : "healthy";
    return {
      enabled: true,
      metrics,
      findings,
      node: {
        schemaVersion: "1.0.0",
        enabled: true,
        status,
        policy: policy.file,
        historyWindow: policy.historyWindow,
        lineBucketSize: policy.lineBucketSize,
        metrics,
        findings,
        files,
        history: { path: policy.historyPath, retainedUpdates: nextHistory.updates.length,
          historyOmitted: nextHistory.historyOmitted }
      }
    };
  });
}

export function showChangeHotspots(root) {
  const policy = loadChangeHealthPolicy(root);
  const { history } = readHistory(root, policy);
  const recent = history.updates.slice(-policy.historyWindow);
  const fileCounts = new Map();
  const regionCounts = new Map();

  for (const update of recent) {
    const fileSeen = new Set();
    const regionSeen = new Set();
    for (const file of update.files ?? []) {
      if (!fileSeen.has(file.path)) {
        fileSeen.add(file.path);
        const current = fileCounts.get(file.path) ?? { path: file.path, touches: 0, lastTouchedAt: update.finalizedAt };
        current.touches += 1;
        if (update.finalizedAt > current.lastTouchedAt) current.lastTouchedAt = update.finalizedAt;
        fileCounts.set(file.path, current);
      }
      for (const hunk of file.hunks ?? []) {
        for (const bucket of hunk.buckets ?? []) {
          const key = file.path + "\u0000" + bucket;
          if (regionSeen.has(key)) continue;
          regionSeen.add(key);
          const current = regionCounts.get(key) ?? {
            path: file.path, bucket,
            startLine: bucket * policy.lineBucketSize + 1,
            endLine: (bucket + 1) * policy.lineBucketSize,
            touches: 0, lastTouchedAt: update.finalizedAt
          };
          current.touches += 1;
          if (update.finalizedAt > current.lastTouchedAt) current.lastTouchedAt = update.finalizedAt;
          regionCounts.set(key, current);
        }
      }
    }
  }

  const sortHot = (a, b) => b.touches - a.touches ||
    b.lastTouchedAt.localeCompare(a.lastTouchedAt) || a.path.localeCompare(b.path);

  return {
    schemaVersion: "1.0.0",
    policy: policy.file,
    historyWindow: policy.historyWindow,
    retainedUpdates: history.updates.length,
    historyOmitted: history.historyOmitted ?? 0,
    files: [...fileCounts.values()].sort(sortHot).slice(0, 50),
    regions: [...regionCounts.values()].sort((a, b) => sortHot(a, b) || a.bucket - b.bucket).slice(0, 100)
  };
}
