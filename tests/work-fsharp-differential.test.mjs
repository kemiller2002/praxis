import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");
const states = ["ready", "active", "blocked", "complete"];
const actions = ["begin", "block", "resume", "complete"];

// Golden masters below were captured once from production's own Node
// implementation (tools/ros_cli.mjs's transition/backlogTransition/
// startWork/captureWork, and tools/ros_git.mjs's observeGitStatus) with the
// exact same fixture setup and call sequence as each test, then frozen
// here. Node is retained in this repository only as the web server's
// internal dependency (DF-ROS-2026-A033) and is no longer executed as a
// live oracle by this test suite.
const GOLDEN = {
  test1Matrix: {
    "ready/begin": { allowed: true, targetState: "active" },
    "ready/block": { allowed: true, targetState: "blocked" },
    "ready/resume": { allowed: false, message: "cannot resume 'TASK-MATRIX' from 'ready'" },
    "ready/complete": { allowed: false, message: "cannot complete 'TASK-MATRIX' from 'ready'" },
    "active/begin": { allowed: false, message: "cannot begin 'TASK-MATRIX' from 'active'" },
    "active/block": { allowed: true, targetState: "blocked" },
    "active/resume": { allowed: false, message: "cannot resume 'TASK-MATRIX' from 'active'" },
    "active/complete": { allowed: true, targetState: "complete" },
    "blocked/begin": { allowed: false, message: "cannot begin 'TASK-MATRIX' from 'blocked'" },
    "blocked/block": { allowed: false, message: "cannot block 'TASK-MATRIX' from 'blocked'" },
    "blocked/resume": { allowed: true, targetState: "active" },
    "blocked/complete": { allowed: false, message: "cannot complete 'TASK-MATRIX' from 'blocked'" },
    "complete/begin": { allowed: false, message: "cannot begin 'TASK-MATRIX' from 'complete'" },
    "complete/block": { allowed: false, message: "cannot block 'TASK-MATRIX' from 'complete'" },
    "complete/resume": { allowed: false, message: "cannot resume 'TASK-MATRIX' from 'complete'" },
    "complete/complete": { allowed: false, message: "cannot complete 'TASK-MATRIX' from 'complete'" }
  },
  test2: { allowed: false, message: "completion evidence missing for 'TASK-MATRIX': tests" },
  test3: { "": false, "  ": true },
  test4Cases: [
    {
      state: "ready",
      action: "begin",
      item: { id: "TASK-MATRIX", type: "mechanical", state: "active", semanticState: "active", evidence: [], updatedAt: "2026-09-11T05:55:03.432Z" },
      event: {
        schemaVersion: "1.0.0", type: "work.started", workItem: "TASK-MATRIX", repository: "work-differential",
        protocolVersion: "1.0.0", occurredAt: "2026-09-11T05:55:03.432Z", evidence: [], paths: [], telemetryExecutions: []
      }
    },
    {
      state: "ready",
      action: "block",
      item: { id: "TASK-MATRIX", type: "mechanical", state: "blocked", semanticState: "blocked", evidence: [], blockReason: "reason", updatedAt: "2026-09-11T05:55:03.455Z" },
      event: {
        schemaVersion: "1.0.0", type: "work.blocked", workItem: "TASK-MATRIX", repository: "work-differential",
        protocolVersion: "1.0.0", occurredAt: "2026-09-11T05:55:03.455Z", reason: "reason", evidence: [], paths: [], telemetryExecutions: []
      }
    },
    {
      state: "active",
      action: "block",
      item: { id: "TASK-MATRIX", type: "mechanical", state: "blocked", semanticState: "blocked", evidence: [], blockReason: "reason", updatedAt: "2026-09-11T05:55:03.470Z" },
      event: {
        schemaVersion: "1.0.0", type: "work.blocked", workItem: "TASK-MATRIX", repository: "work-differential",
        protocolVersion: "1.0.0", occurredAt: "2026-09-11T05:55:03.470Z", reason: "reason", evidence: [], paths: [], telemetryExecutions: []
      }
    },
    {
      state: "blocked",
      action: "resume",
      item: { id: "TASK-MATRIX", type: "mechanical", state: "active", semanticState: "active", evidence: [], updatedAt: "2026-09-11T05:55:03.486Z" },
      event: {
        schemaVersion: "1.0.0", type: "work.resumed", workItem: "TASK-MATRIX", repository: "work-differential",
        protocolVersion: "1.0.0", occurredAt: "2026-09-11T05:55:03.486Z", evidence: [], paths: [], telemetryExecutions: []
      }
    },
    {
      state: "active",
      action: "complete",
      item: { id: "TASK-MATRIX", type: "mechanical", state: "complete", semanticState: "complete", evidence: [], completedAt: "2026-09-11T05:55:03.505Z", updatedAt: "2026-09-11T05:55:03.505Z" },
      event: {
        schemaVersion: "1.0.0", type: "work.completed", workItem: "TASK-MATRIX", repository: "work-differential",
        protocolVersion: "1.0.0", occurredAt: "2026-09-11T05:55:03.505Z", evidence: [],
        paths: [
          ".editorconfig", ".gitattributes", ".github/copilot-instructions.md", ".github/workflows/ros-validation.yml",
          ".gitignore", ".ros/installation.json", "AGENTS.md", "BOOTSTRAP.md", "CLAUDE.md", "GEMINI.md", "HANDOFF.md",
          "PROJECT-CHARTER.md", "README.md", "context/ARCHITECTURE.md", "context/CURRENT-STATE.md", "context/DECISIONS.md",
          "context/KNOWN-RISKS.md", "context/RESEARCH-QUEUE.md", "docs/00-governance/AI-Repository-Operating-System.md",
          "docs/00-governance/Agent-Operating-Manual.md", "docs/00-governance/Engineering-Standards.md",
          "docs/00-governance/Governance-Decision-Log.md", "docs/00-governance/README.md",
          "docs/00-governance/Research-Execution-Package-Specification.md", "docs/PILOT-MEASUREMENT-PLAN.md",
          "docs/architecture/README.md", "docs/decisions/README.md", "docs/development-telemetry.md",
          "docs/work-adapter-contract.md", "docs/work-protocol.md", "framework/REP-SPECIFICATION.md",
          "framework/policies/EVIDENCE-POLICY.md", "framework/policies/OUTPUT-POLICY.md",
          "framework/policies/RESEARCH-POLICY.md", "framework/protocols/ARTIFACT-LIFECYCLE.md",
          "framework/protocols/SUPERSESSION.md", "framework/standards/ARTIFACT-TIERS.md",
          "framework/standards/CONFIDENCE.md", "framework/standards/IDENTIFIERS.md",
          "framework/standards/NAMING-STANDARD.md", "framework/standards/TAXONOMY.md", "missions/active/.gitkeep",
          "missions/backlog/.gitkeep", "missions/completed/.gitkeep", "research/decisions/.gitkeep",
          "research/evidence/.gitkeep", "research/experiments/.gitkeep", "research/frontier/README.md",
          "research/hypotheses/.gitkeep", "research/journals/.gitkeep", "research/packages/.gitkeep",
          "research/theories/.gitkeep", "ros", "ros.json", "schemas/artifact-metadata.schema.json",
          "schemas/evidence.schema.json", "schemas/execution-telemetry.schema.json", "schemas/experiment.schema.json",
          "schemas/hypothesis.schema.json", "schemas/journal.schema.json", "schemas/mission.schema.json",
          "schemas/rep.schema.json", "schemas/theory.schema.json", "schemas/work-adapter-request.schema.json",
          "schemas/work-adapter-result.schema.json", "schemas/work-protocol.schema.json", "telemetry/metrics.json",
          "templates/missions/MISSION-TEMPLATE.md", "templates/research/EVIDENCE-TEMPLATE.md",
          "templates/research/EXPERIMENT-TEMPLATE.md", "templates/research/HYPOTHESIS-TEMPLATE.md",
          "templates/research/JOURNAL-TEMPLATE.md", "templates/research/REP-TEMPLATE.md",
          "templates/research/THEORY-TEMPLATE.md", "tools/ros_fs_launcher.mjs"
        ],
        telemetryExecutions: []
      }
    }
  ],
  test6: { "ros.json": true, ".": true, outside: true, "missing-evidence.txt": false },
  test7ObservationOutcome: "changed",
  test7ObservedGitPaths: [
    ".editorconfig", ".gitattributes", ".github/copilot-instructions.md", ".github/workflows/ros-validation.yml",
    ".gitignore", ".ros/context/current.json", ".ros/events/events.jsonl", ".ros/installation.json",
    ".ros/work/queue.json", ".ros/work/queue.md", "AGENTS.md", "BOOTSTRAP.md", "CLAUDE.md", "GEMINI.md", "HANDOFF.md",
    "PROJECT-CHARTER.md", "README.md", "context/ARCHITECTURE.md", "context/CURRENT-STATE.md", "context/DECISIONS.md",
    "context/KNOWN-RISKS.md", "context/RESEARCH-QUEUE.md", "docs/00-governance/AI-Repository-Operating-System.md",
    "docs/00-governance/Agent-Operating-Manual.md", "docs/00-governance/Engineering-Standards.md",
    "docs/00-governance/Governance-Decision-Log.md", "docs/00-governance/README.md",
    "docs/00-governance/Research-Execution-Package-Specification.md", "docs/PILOT-MEASUREMENT-PLAN.md",
    "docs/architecture/README.md", "docs/decisions/README.md", "docs/development-telemetry.md",
    "docs/work-adapter-contract.md", "docs/work-protocol.md", "framework/REP-SPECIFICATION.md",
    "framework/policies/EVIDENCE-POLICY.md", "framework/policies/OUTPUT-POLICY.md",
    "framework/policies/RESEARCH-POLICY.md", "framework/protocols/ARTIFACT-LIFECYCLE.md",
    "framework/protocols/SUPERSESSION.md", "framework/standards/ARTIFACT-TIERS.md", "framework/standards/CONFIDENCE.md",
    "framework/standards/IDENTIFIERS.md", "framework/standards/NAMING-STANDARD.md", "framework/standards/TAXONOMY.md",
    "missions/active/.gitkeep", "missions/backlog/.gitkeep", "missions/completed/.gitkeep",
    "registries/decisions.json", "registries/evidence.json", "registries/experiments.json",
    "registries/hypotheses.json", "registries/journals.json", "registries/missions.json",
    "registries/research-packages.json", "registries/theories.json", "research/decisions/.gitkeep",
    "research/evidence/.gitkeep", "research/experiments/.gitkeep", "research/frontier/README.md",
    "research/hypotheses/.gitkeep", "research/journals/.gitkeep", "research/packages/.gitkeep",
    "research/theories/.gitkeep", "ros", "ros.json", "schemas/artifact-metadata.schema.json",
    "schemas/evidence.schema.json", "schemas/execution-telemetry.schema.json", "schemas/experiment.schema.json",
    "schemas/hypothesis.schema.json", "schemas/journal.schema.json", "schemas/mission.schema.json",
    "schemas/rep.schema.json", "schemas/theory.schema.json", "schemas/work-adapter-request.schema.json",
    "schemas/work-adapter-result.schema.json", "schemas/work-protocol.schema.json", "telemetry/metrics.json",
    "templates/missions/MISSION-TEMPLATE.md", "templates/research/EVIDENCE-TEMPLATE.md",
    "templates/research/EXPERIMENT-TEMPLATE.md", "templates/research/HYPOTHESIS-TEMPLATE.md",
    "templates/research/JOURNAL-TEMPLATE.md", "templates/research/REP-TEMPLATE.md",
    "templates/research/THEORY-TEMPLATE.md", "tools/ros_fs_launcher.mjs"
  ],
  test7Production: {
    context: {
      schemaVersion: "1.0.0",
      repository: "work-differential",
      workItems: [
        { id: "TASK-EXIST", type: "task", state: "active", semanticState: "active", evidence: [], updatedAt: "2026-09-11T05:55:03.636Z" },
        { id: "TASK-NEW", type: "task", state: "active", semanticState: "active", evidence: [], updatedAt: "2026-09-11T05:55:03.636Z" }
      ],
      protocolVersion: "1.0.0",
      actor: "differential",
      updatedAt: "2026-09-11T05:55:03.636Z",
      startedAt: "2026-09-11T05:55:03.636Z",
      baselineDirtyPaths: [
        ".editorconfig", ".gitattributes", ".github/copilot-instructions.md", ".github/workflows/ros-validation.yml",
        ".gitignore", ".ros/context/current.json", ".ros/events/events.jsonl", ".ros/installation.json",
        ".ros/work/queue.json", ".ros/work/queue.md", "AGENTS.md", "BOOTSTRAP.md", "CLAUDE.md", "GEMINI.md",
        "HANDOFF.md", "PROJECT-CHARTER.md", "README.md", "context/ARCHITECTURE.md", "context/CURRENT-STATE.md",
        "context/DECISIONS.md", "context/KNOWN-RISKS.md", "context/RESEARCH-QUEUE.md",
        "docs/00-governance/AI-Repository-Operating-System.md", "docs/00-governance/Agent-Operating-Manual.md",
        "docs/00-governance/Engineering-Standards.md", "docs/00-governance/Governance-Decision-Log.md",
        "docs/00-governance/README.md", "docs/00-governance/Research-Execution-Package-Specification.md",
        "docs/PILOT-MEASUREMENT-PLAN.md", "docs/architecture/README.md", "docs/decisions/README.md",
        "docs/development-telemetry.md", "docs/work-adapter-contract.md", "docs/work-protocol.md",
        "framework/REP-SPECIFICATION.md", "framework/policies/EVIDENCE-POLICY.md",
        "framework/policies/OUTPUT-POLICY.md", "framework/policies/RESEARCH-POLICY.md",
        "framework/protocols/ARTIFACT-LIFECYCLE.md", "framework/protocols/SUPERSESSION.md",
        "framework/standards/ARTIFACT-TIERS.md", "framework/standards/CONFIDENCE.md",
        "framework/standards/IDENTIFIERS.md", "framework/standards/NAMING-STANDARD.md",
        "framework/standards/TAXONOMY.md", "missions/active/.gitkeep", "missions/backlog/.gitkeep",
        "missions/completed/.gitkeep", "registries/decisions.json", "registries/evidence.json",
        "registries/experiments.json", "registries/hypotheses.json", "registries/journals.json",
        "registries/missions.json", "registries/research-packages.json", "registries/theories.json",
        "research/decisions/.gitkeep", "research/evidence/.gitkeep", "research/experiments/.gitkeep",
        "research/frontier/README.md", "research/hypotheses/.gitkeep", "research/journals/.gitkeep",
        "research/packages/.gitkeep", "research/theories/.gitkeep", "ros", "ros.json",
        "schemas/artifact-metadata.schema.json", "schemas/evidence.schema.json",
        "schemas/execution-telemetry.schema.json", "schemas/experiment.schema.json", "schemas/hypothesis.schema.json",
        "schemas/journal.schema.json", "schemas/mission.schema.json", "schemas/rep.schema.json",
        "schemas/theory.schema.json", "schemas/work-adapter-request.schema.json",
        "schemas/work-adapter-result.schema.json", "schemas/work-protocol.schema.json", "telemetry/metrics.json",
        "templates/missions/MISSION-TEMPLATE.md", "templates/research/EVIDENCE-TEMPLATE.md",
        "templates/research/EXPERIMENT-TEMPLATE.md", "templates/research/HYPOTHESIS-TEMPLATE.md",
        "templates/research/JOURNAL-TEMPLATE.md", "templates/research/REP-TEMPLATE.md",
        "templates/research/THEORY-TEMPLATE.md", "tools/ros_fs_launcher.mjs"
      ]
    },
    events: [
      {
        schemaVersion: "1.0.0", type: "work.started", workItem: "TASK-NEW", repository: "work-differential",
        protocolVersion: "1.0.0", occurredAt: "2026-09-11T05:55:03.636Z", evidence: [], paths: [], telemetryExecutions: []
      },
      {
        schemaVersion: "1.0.0", type: "work.started", workItem: "TASK-EXIST", repository: "work-differential",
        protocolVersion: "1.0.0", occurredAt: "2026-09-11T05:55:03.636Z", evidence: [], paths: [], telemetryExecutions: []
      }
    ]
  },
  test8Message: "cannot begin 'TASK-DONE' from 'complete'",
  test9Matrix: {
    "captured/ready": { beforeBlockedReason: null, beforeAbandonedReason: null, allowed: true, afterStatus: "ready", afterBlockedReason: null, afterAbandonedReason: null },
    "captured/block": { beforeBlockedReason: null, beforeAbandonedReason: null, allowed: false },
    "captured/abandon": { beforeBlockedReason: null, beforeAbandonedReason: null, allowed: true, afterStatus: "abandoned", afterBlockedReason: null, afterAbandonedReason: "reason" },
    "captured/start": { beforeBlockedReason: null, beforeAbandonedReason: null, allowed: false },
    "ready/ready": { beforeBlockedReason: null, beforeAbandonedReason: null, allowed: false },
    "ready/block": { beforeBlockedReason: null, beforeAbandonedReason: null, allowed: true, afterStatus: "blocked", afterBlockedReason: "reason", afterAbandonedReason: null },
    "ready/abandon": { beforeBlockedReason: null, beforeAbandonedReason: null, allowed: true, afterStatus: "abandoned", afterBlockedReason: null, afterAbandonedReason: "reason" },
    "ready/start": { beforeBlockedReason: null, beforeAbandonedReason: null, allowed: true, afterStatus: "ready" },
    "blocked/ready": { beforeBlockedReason: "prior", beforeAbandonedReason: null, allowed: true, afterStatus: "ready", afterBlockedReason: null, afterAbandonedReason: null },
    "blocked/block": { beforeBlockedReason: "prior", beforeAbandonedReason: null, allowed: false },
    "blocked/abandon": { beforeBlockedReason: "prior", beforeAbandonedReason: null, allowed: true, afterStatus: "abandoned", afterBlockedReason: "prior", afterAbandonedReason: "reason" },
    "blocked/start": { beforeBlockedReason: "prior", beforeAbandonedReason: null, allowed: false },
    "abandoned/ready": { beforeBlockedReason: null, beforeAbandonedReason: "prior", allowed: false },
    "abandoned/block": { beforeBlockedReason: null, beforeAbandonedReason: "prior", allowed: false },
    "abandoned/abandon": { beforeBlockedReason: null, beforeAbandonedReason: "prior", allowed: false },
    "abandoned/start": { beforeBlockedReason: null, beforeAbandonedReason: "prior", allowed: false }
  },
  test10RejectMessage: "cannot start backlog item 'WI-CAPTURED' from 'captured'; mark it ready first",
  test10ExtDirectSemanticState: "active",
  test11: {
    "ros.json": { allowed: true, repository: "work-differential", protocolVersion: "1.0.0", actor: "differential" },
    "missing-context-evidence.txt": { allowed: false, repository: "work-differential", protocolVersion: "1.0.0", actor: "differential" }
  }
};

function fixture(t, state, type = "mechanical") {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-work-differential-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 50 }));
  initializeProject({ target: root, project: "Work Differential" });
  execFileSync("git", ["-C", root, "init", "-q"]);
  const configFile = path.join(root, "ros.json");
  const config = JSON.parse(fs.readFileSync(configFile, "utf8"));
  config.telemetry.enabled = false;
  fs.writeFileSync(configFile, `${JSON.stringify(config, null, 2)}\n`);
  const contextFile = path.join(root, ".ros", "context", "current.json");
  fs.writeFileSync(contextFile, `${JSON.stringify({
    schemaVersion: "1.0.0", repository: config.repository.id,
    workItems: [{ id: "TASK-MATRIX", type, state, semanticState: state, evidence: [] }]
  }, null, 2)}\n`);
  return root;
}

function fsharpDecision(state, action, options = {}) {
  const args = [fsharpCli, "work", "decide", "--state", state, "--action", action];
  if (options.reason !== undefined) args.push("--reason", options.reason);
  for (const kind of options.required ?? []) args.push("--required", kind);
  for (const kind of options.provided ?? []) args.push("--provided", kind);
  const result = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: JSON.parse(result.stdout), stderr: result.stderr };
}

function fsharpPlan(item, event, state, action) {
  const args = [
    fsharpCli, "work", "plan",
    "--id", item.id,
    "--type", item.type,
    "--state", state,
    "--action", action,
    "--occurred-at", event.occurredAt,
    "--repository", event.repository,
    "--protocol-version", event.protocolVersion
  ];
  if (event.reason !== undefined) args.push("--reason", event.reason);
  for (const evidence of event.evidence ?? []) args.push("--evidence", `${evidence.type}=${evidence.path}`);
  for (const changedPath of event.paths ?? []) args.push("--path", changedPath);
  const result = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: JSON.parse(result.stdout), stderr: result.stderr };
}

function normalizedItem(item) {
  return {
    id: item.id,
    type: item.type,
    state: item.state,
    semanticState: item.semanticState,
    evidence: item.evidence,
    blockReason: item.blockReason ?? null,
    updatedAt: item.updatedAt ?? null,
    completedAt: item.completedAt ?? null,
    telemetryExecutionIds: item.telemetryExecutionIds ?? []
  };
}

function normalizedEvent(event) {
  return {
    type: event.type,
    workItem: event.workItem,
    repository: event.repository,
    protocolVersion: event.protocolVersion,
    occurredAt: event.occurredAt,
    reason: event.reason ?? null,
    evidence: event.evidence ?? [],
    paths: event.paths ?? [],
    telemetryExecutions: event.telemetryExecutions ?? []
  };
}

function contextFixture(t, workItems) {
  const root = fixture(t, "ready");
  const contextFile = path.join(root, ".ros", "context", "current.json");
  fs.writeFileSync(contextFile, `${JSON.stringify({
    schemaVersion: "1.0.0",
    repository: "work-differential",
    workItems
  }, null, 2)}\n`);
  return root;
}

function fsharpContextPlan(t, root, beforeContext, action, ids, production, options = {}) {
  const snapshot = path.join(os.tmpdir(), `ros-work-context-${process.pid}-${Math.random().toString(16).slice(2)}.json`);
  fs.writeFileSync(snapshot, beforeContext);
  t.after(() => fs.rmSync(snapshot, { force: true }));
  const args = [
    fsharpCli, "--root", root, "work", "context-plan", "--context", snapshot,
    "--action", action, "--occurred-at", options.occurredAt,
    "--repository", production.context.repository,
    "--protocol-version", production.context.protocolVersion,
    "--actor", production.context.actor,
    "--type", options.type ?? "task"
  ];
  for (const id of ids) args.push("--id", id);
  for (const changedPath of options.changedPaths ?? []) args.push("--path", changedPath);
  for (const observedPath of options.observedGitPaths ?? []) args.push("--observed-git-path", observedPath);
  for (const kind of options.required ?? []) args.push("--required", kind);
  for (const evidence of options.evidence ?? []) args.push("--evidence", `${evidence.type}=${evidence.path}`);
  if (options.reason !== undefined) args.push("--reason", options.reason);
  if (options.verifyEvidence) args.push("--verify-evidence");
  return spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
}

function fsharpBacklogDecision(state, action, reason = "reason") {
  const args = [fsharpCli, "work", "backlog-decide", "--state", state, "--action", action];
  if (reason !== undefined) args.push("--reason", reason);
  const result = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
  return { status: result.status, json: JSON.parse(result.stdout), stderr: result.stderr };
}

function projectedField(change, current) {
  if (change.kind === "keep") return current ?? null;
  if (change.kind === "clear") return null;
  if (change.kind === "set") return change.value;
  throw new Error(`unknown backlog field change '${change.kind}'`);
}

test("F# live-work decision matrix matches the Node transition guard", () => {
  for (const state of states) {
    for (const action of actions) {
      const node = GOLDEN.test1Matrix[`${state}/${action}`];
      const fsharp = fsharpDecision(state, action, { reason: "reason" });
      assert.equal(fsharp.status === 0, node.allowed, `${state}/${action}: ${node.message ?? fsharp.stderr}`);
      if (node.allowed) assert.equal(fsharp.json.targetState, node.targetState, `${state}/${action}`);
    }
  }
});

test("F# completion evidence-type guard matches Node missing evidence", () => {
  const node = GOLDEN.test2;
  const fsharp = fsharpDecision("active", "complete", {
    required: ["implementation", "tests"], provided: ["implementation"]
  });
  assert.equal(node.allowed, false);
  assert.match(node.message, /completion evidence missing.*tests/);
  assert.equal(fsharp.status, 1);
  assert.deepEqual(fsharp.json.rejection, { reason: "missing-evidence", missingEvidence: ["tests"] });
});

test("F# block-reason guard preserves Node empty versus whitespace behavior", () => {
  for (const reason of ["", "  "]) {
    const node = { allowed: GOLDEN.test3[reason] };
    const fsharp = fsharpDecision("active", "block", { reason });
    assert.equal(fsharp.status === 0, node.allowed, JSON.stringify({ reason, node, fsharp }));
  }
});

test("F# work plans match production item and event projections for every legal edge", () => {
  for (const { state, action, item, event } of GOLDEN.test4Cases) {
    const fsharp = fsharpPlan(item, event, state, action);
    assert.equal(fsharp.status, 0, `${state}/${action}: ${fsharp.stderr}`);
    assert.deepEqual(fsharp.json.plan.item, normalizedItem(item), `${state}/${action} item`);
    assert.deepEqual(fsharp.json.plan.event, normalizedEvent(event), `${state}/${action} event`);
    assert.deepEqual(fsharp.json.plan.telemetry, []);
  }
});

test("F# work plan rejection retains missing-evidence obligations", () => {
  const result = spawnSync("dotnet", [
    fsharpCli, "work", "plan", "--id", "TASK-PLAN", "--type", "feature",
    "--state", "active", "--action", "complete", "--occurred-at", "2026-09-08T18:30:00Z",
    "--required", "tests"
  ], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(result.status, 1, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), {
    schemaVersion: "1.0.0",
    outcome: "rejected",
    plan: null,
    rejection: { reason: "missing-evidence", missingEvidence: ["tests"] }
  });
});

test("F# evidence verification matches production file directory and absolute-path existence", (t) => {
  const outside = path.join(os.tmpdir(), `ros-work-evidence-${process.pid}.txt`);
  fs.writeFileSync(outside, "outside fixture\n");
  t.after(() => fs.rmSync(outside, { force: true }));
  const evidenceCases = [
    { key: "ros.json", evidencePath: "ros.json" },
    { key: ".", evidencePath: "." },
    { key: "outside", evidencePath: outside },
    { key: "missing-evidence.txt", evidencePath: "missing-evidence.txt" }
  ];
  for (const { key, evidencePath } of evidenceCases) {
    const root = fixture(t, "active", "feature");
    const nodeAllowed = GOLDEN.test6[key];
    const args = [
      fsharpCli, "--root", root, "work", "plan", "--verify-evidence",
      "--id", "TASK-MATRIX", "--type", "feature", "--state", "active", "--action", "complete",
      "--occurred-at", "2026-09-08T18:30:00Z", "--required", "implementation", "--required", "tests",
      "--evidence", `implementation=${evidencePath}`, "--evidence", `tests=${evidencePath}`
    ];
    const fsharp = spawnSync("dotnet", args, { cwd: repositoryRoot, encoding: "utf8" });
    assert.equal(fsharp.status === 0, nodeAllowed, evidencePath);
    const payload = JSON.parse(fsharp.stdout);
    if (nodeAllowed) assert.equal(payload.outcome, "planned", evidencePath);
    else {
      assert.equal(payload.outcome, "evidence-rejected", evidencePath);
      assert.deepEqual(payload.rejection.evidenceIssues.map((issue) => issue.outcome), ["missing", "missing"]);
    }
  }
});

test("F# context plan matches production multi-item begin order and metadata", (t) => {
  const root = contextFixture(t, [
    { id: "TASK-EXIST", type: "task", state: "ready", semanticState: "ready", evidence: [] }
  ]);
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const before = fs.readFileSync(contextFile, "utf8");
  assert.notEqual(GOLDEN.test7ObservationOutcome, "unavailable");
  const observedGitPaths = GOLDEN.test7ObservedGitPaths;
  const ids = ["TASK-NEW", "TASK-EXIST"];
  const production = GOLDEN.test7Production;
  const fsharp = fsharpContextPlan(t, root, before, "begin", ids, production, {
    occurredAt: production.events[0].occurredAt,
    observedGitPaths
  });
  assert.equal(fsharp.status, 0, fsharp.stderr);
  const plan = JSON.parse(fsharp.stdout).plan;
  assert.deepEqual(plan.workItems, production.context.workItems.map(normalizedItem));
  assert.deepEqual(plan.events, production.events.map(normalizedEvent));
  assert.equal(plan.repository, production.context.repository);
  assert.equal(plan.protocolVersion, production.context.protocolVersion);
  assert.equal(plan.actor, production.context.actor);
  assert.equal(plan.startedAt, production.context.startedAt);
  assert.equal(plan.updatedAt, production.context.updatedAt);
  assert.deepEqual(plan.baselineDirtyPaths, production.context.baselineDirtyPaths);
});

test("F# context plan and production both reject a later illegal item without context writes", (t) => {
  const workItems = [
    { id: "TASK-READY", type: "task", state: "ready", semanticState: "ready", evidence: [] },
    { id: "TASK-DONE", type: "task", state: "complete", semanticState: "complete", evidence: [], completedAt: "earlier" }
  ];
  const root = contextFixture(t, workItems);
  const contextFile = path.join(root, ".ros", "context", "current.json");
  const before = fs.readFileSync(contextFile, "utf8");
  const ids = ["TASK-READY", "TASK-DONE"];
  assert.match(GOLDEN.test8Message, /cannot begin 'TASK-DONE' from 'complete'/);
  const productionShape = { context: { repository: "work-differential", protocolVersion: "1.0.0", actor: "differential" } };
  const fsharp = fsharpContextPlan(t, root, before, "begin", ids, productionShape, {
    occurredAt: "2026-09-08T23:45:00Z"
  });
  assert.equal(fsharp.status, 1, fsharp.stderr);
  assert.deepEqual(JSON.parse(fsharp.stdout), {
    schemaVersion: "1.0.0",
    outcome: "rejected",
    plan: null,
    rejection: {
      reason: "item-transition-rejected",
      workItem: "TASK-DONE",
      transition: { reason: "illegal-transition", state: "complete", action: "begin", missingEvidence: [] }
    }
  });
});

test("F# backlog decision matrix matches production and keeps start as promotion", () => {
  const backlogStates = ["captured", "ready", "blocked", "abandoned"];
  const backlogActions = ["ready", "block", "abandon", "start"];
  for (const state of backlogStates) {
    for (const action of backlogActions) {
      const golden = GOLDEN.test9Matrix[`${state}/${action}`];
      const fsharp = fsharpBacklogDecision(state, action);
      assert.equal(fsharp.status === 0, golden.allowed, `${state}/${action}: ${fsharp.stderr}`);
      if (!golden.allowed) continue;
      if (action === "start") {
        assert.equal(fsharp.json.effect.kind, "promote-to-live-work");
        assert.equal(golden.afterStatus, "ready");
      } else {
        assert.equal(fsharp.json.effect.kind, "change-state");
        assert.equal(fsharp.json.effect.state, golden.afterStatus);
        assert.equal(projectedField(fsharp.json.effect.blockedReason, golden.beforeBlockedReason), golden.afterBlockedReason);
        assert.equal(projectedField(fsharp.json.effect.abandonedReason, golden.beforeAbandonedReason), golden.afterAbandonedReason);
      }
    }
  }
});

test("F# backlog promotion preflight matches production batch rejection and direct-ID allowance", () => {
  assert.match(GOLDEN.test10RejectMessage, /cannot start backlog item 'WI-CAPTURED' from 'captured'/);
  const rejected = spawnSync("dotnet", [
    fsharpCli, "work", "backlog-promotion-plan",
    "--id", "WI-READY", "--id", "WI-CAPTURED", "--type", "feature",
    "--queue-state", "WI-READY=ready", "--queue-state", "WI-CAPTURED=captured"
  ], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(rejected.status, 1, rejected.stderr);
  assert.deepEqual(JSON.parse(rejected.stdout).rejection, {
    reason: "backlog-item-not-ready", workItem: "WI-CAPTURED", state: "captured"
  });

  assert.equal(GOLDEN.test10ExtDirectSemanticState, "active");
  const planned = spawnSync("dotnet", [
    fsharpCli, "work", "backlog-promotion-plan", "--id", "EXT-DIRECT", "--type", "feature"
  ], { cwd: repositoryRoot, encoding: "utf8" });
  assert.equal(planned.status, 0, planned.stderr);
  assert.deepEqual(JSON.parse(planned.stdout).plan, { workItems: ["EXT-DIRECT"], workType: "feature" });
});

test("F# verified context plan matches production multi-item evidence-path outcomes", (t) => {
  for (const evidencePath of ["ros.json", "missing-context-evidence.txt"]) {
    const workItems = [
      { id: "TASK-ONE", type: "mechanical", state: "active", semanticState: "active", evidence: [] },
      { id: "TASK-TWO", type: "mechanical", state: "active", semanticState: "active", evidence: [] }
    ];
    const root = contextFixture(t, workItems);
    const contextFile = path.join(root, ".ros", "context", "current.json");
    const before = fs.readFileSync(contextFile, "utf8");
    const ids = ["TASK-ONE", "TASK-TWO"];
    const evidence = [{ type: "note", path: evidencePath }];
    const golden = GOLDEN.test11[evidencePath];
    const productionAllowed = golden.allowed;
    const production = { context: { repository: golden.repository, protocolVersion: golden.protocolVersion, actor: golden.actor } };
    const fsharp = fsharpContextPlan(t, root, before, "complete", ids, production, {
      occurredAt: "2026-09-09T01:00:00Z",
      evidence,
      verifyEvidence: true
    });
    assert.equal(fsharp.status === 0, productionAllowed, `${evidencePath}: ${fsharp.stderr}`);
    const payload = JSON.parse(fsharp.stdout);
    assert.equal(payload.outcome, productionAllowed ? "planned" : "evidence-rejected");
  }
});
