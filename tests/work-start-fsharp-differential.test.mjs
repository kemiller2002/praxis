import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync, spawnSync } from "node:child_process";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { initializeProject } from "../lib/bootstrap.mjs";
import { withoutProvenance } from "./support/provenance-golden.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const installWorkItemId = `ROS-INSTALL-${JSON.parse(fs.readFileSync(path.join(repositoryRoot, "package.json"), "utf8")).version.replaceAll(".", "-")}`;
const fsharpCli = path.join(repositoryRoot, "src", "Ros.Cli", "bin", "Release", "net10.0", "ros-fs.dll");

// This test's golden capability data includes runtime-identity detection
// (Ros.Domain.Telemetry.Identity.discover), which whitelists whichever CI/
// agent environment the caller happens to run in (CLAUDE_CODE_SESSION_ID,
// GITHUB_ACTIONS, etc.) ahead of an explicit override. Clearing every
// whitelisted variable makes the captured execution's identity
// deterministic across environments (a contributor's own machine, this
// sandbox, or a real CI runner) instead of baking in whichever one
// captured the golden literal.
const DETERMINISTIC_ENV = { ...process.env };
for (const key of [
  "CLAUDE_CODE_SESSION_ID", "CODEX_SESSION_ID", "CODEX_THREAD_ID",
  "GEMINI_SESSION_ID", "COPILOT_SESSION_ID", "GITHUB_ACTIONS", "GITHUB_RUN_ID",
  "OLLAMA_HOST", "ROS_TELEMETRY_PROVIDER", "ROS_TELEMETRY_RUNTIME"
]) delete DETERMINISTIC_ENV[key];

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-work-start-${label}-`));
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
  initializeProject({ target: root, project: "Work Start Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function writeQueue(root, queue) {
  fs.writeFileSync(path.join(root, ".ros", "work", "queue.json"), `${JSON.stringify(queue, null, 2)}\n`);
}

function readContextRaw(root) {
  return JSON.parse(fs.readFileSync(path.join(root, ".ros", "context", "current.json"), "utf8"));
}

function readEventsRaw(root) {
  const file = path.join(root, ".ros", "events", "events.jsonl");
  return fs.existsSync(file) ? fs.readFileSync(file, "utf8").split(/\r?\n/).filter(Boolean).map((line) => JSON.parse(line)) : [];
}

function readExecutionsRaw(root) {
  const dir = path.join(root, ".ros", "telemetry", "executions");
  return fs.readdirSync(dir).sort().map((name) => JSON.parse(fs.readFileSync(path.join(dir, name), "utf8")));
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "work", "start", ...args], { cwd: repositoryRoot, encoding: "utf8", env: DETERMINISTIC_ENV });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

const VOLATILE_KEYS = new Set([
  "executionId", "startedAt", "discoveredAt", "lastAssessedAt", "recordedAt", "collectedAt",
  "measurementId", "commit", "branch", "dirtyPaths", "dirty", "commits", "occurredAt",
  "eventId", "updatedAt", "telemetryExecutionIds", "telemetryExecutions",
  // Pre-existing bootstrap-installation work item state (`ROS-INSTALL-*`),
  // unrelated to `work start`/`begin` -- its own `completedAt` is set once
  // at `initializeProject` time and naturally differs between the two
  // independently-created fixture directories.
  "completedAt"
]);

function stripVolatile(value) {
  if (Array.isArray(value)) return value.map(stripVolatile);
  if (value && typeof value === "object") {
    const result = {};
    for (const [key, child] of Object.entries(value)) {
      if (VOLATILE_KEYS.has(key)) continue;
      result[key] = stripVolatile(child);
    }
    return result;
  }
  return value;
}

const queueFixture = {
  schemaVersion: "1.0.0",
  repository: "repository",
  nextSeq: 4,
  items: [
    { id: "WI-READY", title: "Ready item", description: null, tags: [], priority: "medium", status: "ready", attachments: [], createdAt: "2026-01-01T00:00:00.000Z", updatedAt: "2026-01-01T00:00:00.000Z" },
    { id: "WI-CAPTURED", title: "Captured item", description: null, tags: [], priority: "medium", status: "captured", attachments: [], createdAt: "2026-01-01T00:00:00.000Z", updatedAt: "2026-01-01T00:00:00.000Z" },
    { id: "WI-ABANDONED", title: "Abandoned item", description: null, tags: [], priority: "medium", status: "abandoned", attachments: [], createdAt: "2026-01-01T00:00:00.000Z", updatedAt: "2026-01-01T00:00:00.000Z" }
  ]
};

// Golden masters below were captured once from production's own Node
// implementation (tools/ros_cli.mjs's startWork) with the exact same call
// sequence as each test, then frozen here. Node is retained in this
// repository only as the web server's internal dependency
// (DF-ROS-2026-A033) and is no longer executed as a live oracle by this
// test suite.
const GOLDEN ={
  "test1Context": {
    "schemaVersion": "1.0.0",
    "protocolVersion": "1.0.0",
    "repository": "work-start-differential",
    "actor": "ros-bootstrap",
    "baselineDirtyPaths": [],
    "workItems": [
      {
        "id": installWorkItemId,
        "type": "mechanical",
        "state": "complete",
        "semanticState": "complete",
        "evidence": [
          {
            "type": "installation",
            "path": ".ros/installation.json"
          }
        ]
      },
      {
        "id": "WI-NEW",
        "type": "task",
        "state": "active",
        "semanticState": "active",
        "evidence": []
      }
    ]
  },
  "test1ExecIdsLength": 1,
  "test1Events": [
    {
      "schemaVersion": "1.0.0",
      "type": "work.completed",
      "workItem": installWorkItemId,
      "repository": "work-start-differential",
      "protocolVersion": "1.0.0",
      "evidence": [
        {
          "type": "installation",
          "path": ".ros/installation.json"
        }
      ],
      "paths": [
        ".echelon/toolchain.json",
        ".editorconfig",
        ".gitattributes",
        ".github/copilot-instructions.md",
        ".github/workflows/ros-validation.yml",
        ".gitignore",
        ".ros/installation.json",
        "AGENTS.md",
        "BOOTSTRAP.md",
        "CLAUDE.md",
        "GEMINI.md",
        "HANDOFF.md",
        "PROJECT-CHARTER.md",
        "README.md",
        "context/ARCHITECTURE.md",
        "context/CURRENT-STATE.md",
        "context/DECISIONS.md",
        "context/KNOWN-RISKS.md",
        "context/RESEARCH-QUEUE.md",
        "docs/00-governance/AI-Repository-Operating-System.md",
        "docs/00-governance/Agent-Operating-Manual.md",
        "docs/00-governance/Engineering-Standards.md",
        "docs/00-governance/Governance-Decision-Log.md",
        "docs/00-governance/README.md",
        "docs/00-governance/Research-Execution-Package-Specification.md",
        "docs/PILOT-MEASUREMENT-PLAN.md",
        "docs/agent-identity-and-provenance.md",
        "docs/architecture/README.md",
        "docs/decisions/README.md",
        "docs/development-telemetry.md",
        "docs/ordo-observation.md",
        "docs/work-adapter-contract.md",
        "docs/work-protocol.md",
        "framework/REP-SPECIFICATION.md",
        "framework/policies/EVIDENCE-POLICY.md",
        "framework/policies/OUTPUT-POLICY.md",
        "framework/policies/RESEARCH-POLICY.md",
        "framework/protocols/ARTIFACT-LIFECYCLE.md",
        "framework/protocols/SUPERSESSION.md",
        "framework/standards/ARTIFACT-TIERS.md",
        "framework/standards/CONFIDENCE.md",
        "framework/standards/IDENTIFIERS.md",
        "framework/standards/NAMING-STANDARD.md",
        "framework/standards/TAXONOMY.md",
        "missions/active/.gitkeep",
        "missions/backlog/.gitkeep",
        "missions/completed/.gitkeep",
        "registries/decisions.json",
        "registries/evidence.json",
        "registries/experiments.json",
        "registries/hypotheses.json",
        "registries/journals.json",
        "registries/missions.json",
        "registries/research-packages.json",
        "registries/theories.json",
        "research/decisions/.gitkeep",
        "research/evidence/.gitkeep",
        "research/experiments/.gitkeep",
        "research/frontier/README.md",
        "research/hypotheses/.gitkeep",
        "research/journals/.gitkeep",
        "research/packages/.gitkeep",
        "research/theories/.gitkeep",
        "ros",
        "ros.json",
        "schemas/artifact-metadata.schema.json",
        "schemas/evidence.schema.json",
        "schemas/execution-telemetry.schema.json",
        "schemas/experiment.schema.json",
        "schemas/hypothesis.schema.json",
        "schemas/journal.schema.json",
        "schemas/mission.schema.json",
        "schemas/praxis-actor.schema.json",
        "schemas/praxis-provenance.schema.json",
        "schemas/rep.schema.json",
        "schemas/ros-effect-observation.schema.json",
        "schemas/ros-effective-current.schema.json",
        "schemas/ros-handoff-authority.schema.json",
        "schemas/ros-resolution-assessment.schema.json",
        "schemas/ros-search-observation.schema.json",
        "schemas/theory.schema.json",
        "schemas/work-adapter-request.schema.json",
        "schemas/work-adapter-result.schema.json",
        "schemas/work-protocol.schema.json",
        "telemetry/metrics.json",
        "templates/missions/MISSION-TEMPLATE.md",
        "templates/requirements/REQUIREMENT-TEMPLATE.md",
        "templates/research/EVIDENCE-TEMPLATE.md",
        "templates/research/EXPERIMENT-TEMPLATE.md",
        "templates/research/HYPOTHESIS-TEMPLATE.md",
        "templates/research/JOURNAL-TEMPLATE.md",
        "templates/research/REP-TEMPLATE.md",
        "templates/research/THEORY-TEMPLATE.md",
        "tools/ros_fs_launcher.mjs"
      ],
      "publication": {
        "status": "pending"
      }
    },
    {
      "schemaVersion": "1.0.0",
      "type": "work.started",
      "workItem": "WI-NEW",
      "repository": "work-start-differential",
      "protocolVersion": "1.0.0",
      "evidence": [],
      "paths": [],
      "publication": {
        "status": "pending"
      }
    }
  ],
  "test1StartedTelemetryExecutionsLength": 1,
  "test1ExecutionsLength": 1,
  "test1Execution0":   {
    "schemaVersion": "1.0.0",
    "workItemId": "WI-NEW",
    "status": "active",
    "finalizedAt": null,
    "identity": {
      "provider": "unknown",
      "model": null,
      "modelVersion": null,
      "runtime": "unknown",
      "runtimeVersion": null,
      "sessionId": null,
      "conversationId": null,
      "runId": null,
      "agentId": null,
      "subagentId": null,
      "parentExecutionId": null,
      "orchestration": {}
    },
    "provenance": {
      "collector": "ros",
      "collectorVersion": "1.0.0",
      "sources": [
        {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        },
        {
          "type": "ros-git",
          "name": "git",
          "mechanism": "repository-baseline"
        }
      ]
    },
    "classification": {
      "types": [
        "development"
      ],
      "rationale": null,
      "evidence": [],
      "rd": null
    },
    "capabilities": [
      {
        "metricId": "tokens.input",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tokens.output",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tokens.cached_input",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tokens.cache_read",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tokens.cache_write",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tokens.reasoning",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tokens.tool",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tokens.total",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "context.window_size",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "context.utilization",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "context.current_input_tokens",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "context.current_output_tokens",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "context.current_cache_read_tokens",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "context.current_cache_write_tokens",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "context.compactions",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "session.tokens.cumulative",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "cost.input",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "cost.output",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "cost.reasoning",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "cost.cache",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "cost.tool_api",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "cost.execution_total",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "cost.session_cumulative",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "time.wall_ms",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "time.active_ms",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "time.tool_ms",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "time.model_ms",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "time.waiting_ms",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "time.blocked_ms",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "time.human_interruption_ms",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "time.retry_ms",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "time.first_token_ms",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.turns",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.reasoning_cycles",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.retries",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.self_corrections",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.failed_approaches",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.context_resets",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.handoffs",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.subagents_spawned",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.parallel_executions_peak",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.interruptions",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.resumes",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.human_escalations",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.clarifications",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.plan_revisions",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.scope_revisions",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.approvals_requested",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "agent.approvals_denied",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "model.requests",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "model.request_failures",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.calls",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.failures",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.retries",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.duration_ms",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.cost",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.shell_commands",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.file_reads",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.file_writes",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.searches",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.web_activity",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.repository_operations",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.api_operations",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.build_executions",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.test_executions",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.deployments",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.database_operations",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tool.external_service_calls",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "git.baseline_dirty_files",
        "status": "derived",
        "reason": "normalized measurement recorded",
        "source": {
          "type": "ros-git",
          "name": "git-status",
          "mechanism": "porcelain-v1"
        },
        "history": [
          {
            "status": "derived",
            "reason": "ROS can derive this metric when its preconditions hold",
            "source": {
              "type": "environment",
              "name": "runtime-identity",
              "mechanism": "explicit-or-unmapped-environment"
            }
          }
        ]
      },
      {
        "metricId": "git.ending_dirty_files",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "git.commits_created",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "git.files_added",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "git.files_modified",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "git.files_deleted",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "git.files_renamed",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "git.binary_files_changed",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "git.lines_added",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "git.lines_deleted",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tests.added",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tests.modified",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tests.removed",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tests.count_before",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tests.count_after",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tests.executions",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tests.passed",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tests.failed",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "tests.skipped",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "build.executions",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "build.failures",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "compiler.errors",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "lint.errors",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "lint.warnings",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "static_analysis.errors",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "architecture_check.failures",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "requirements.affected",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "requirements.satisfied",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "acceptance_criteria.affected",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "acceptance_criteria.satisfied",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "defects.discovered",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "defects.corrected",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "regressions.found",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "work.new_items",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "blockers.discovered",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "dependencies.discovered",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "architecture.findings",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "documentation.files_changed",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "quality.false_leads",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "quality.disproven_hypotheses",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "quality.silent_omissions",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "quality.invalid_transitions_prevented",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "telemetry.redactions",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "telemetry.unknown_fields",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "telemetry.raw_snapshots_omitted",
        "status": "derived",
        "reason": "ROS can derive this metric when its preconditions hold",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "runtime.memory_peak_bytes",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      },
      {
        "metricId": "runtime.cpu_utilization",
        "status": "unknown",
        "reason": "runtime capability not reported or mapped",
        "source": {
          "type": "environment",
          "name": "runtime-identity",
          "mechanism": "explicit-or-unmapped-environment"
        }
      }
    ],
    "metrics": [
      {
        "id": "git.baseline_dirty_files",
        "value": 0,
        "unit": "count",
        "currency": null,
        "quality": "derived",
        "confidence": null,
        "scope": "execution",
        "aggregation": "maximum",
        "dimensions": {},
        "pricing": null,
        "source": {
          "type": "ros-git",
          "name": "git-status",
          "mechanism": "porcelain-v1"
        },
        "schemaVersion": "1.0.0"
      }
    ],
    "rawTelemetry": [],
    "events": [
      {
        "type": "execution.started",
        "source": {
          "type": "ros-clock",
          "name": "ros",
          "mechanism": "work-lifecycle"
        }
      }
    ],
    "repository": {
      "start": {
        "available": true,
        "repository": "work-start-differential"
      },
      "end": null,
      "changeSummary": null
    },
    "scope": {
      "initial": {},
      "actual": {}
    },
    "qualitySignals": [],
    "links": {
      "workItemId": "WI-NEW",
      "parentWorkItemId": null,
      "requirements": [],
      "acceptanceCriteria": [],
      "pullRequests": [],
      "experiments": [],
      "researchQuestions": [],
      "decisions": [],
      "defects": [],
      "dependencies": [],
      "evidence": []
    }
  },
  "test1CapabilitiesLength": 115,
  "test2Context": {
    "schemaVersion": "1.0.0",
    "protocolVersion": "1.0.0",
    "repository": "work-start-differential",
    "actor": "ros-bootstrap",
    "baselineDirtyPaths": [],
    "workItems": [
      {
        "id": installWorkItemId,
        "type": "mechanical",
        "state": "complete",
        "semanticState": "complete",
        "evidence": [
          {
            "type": "installation",
            "path": ".ros/installation.json"
          }
        ]
      },
      {
        "id": "WI-READY",
        "type": "task",
        "state": "active",
        "semanticState": "active",
        "evidence": []
      }
    ]
  },
  "test3Message": "cannot start backlog item 'WI-CAPTURED' from 'captured'; mark it ready first",
  "test4Message": "cannot start backlog item 'WI-ABANDONED': it was abandoned",
  "test5Message": "cannot begin 'WI-ACTIVE' from 'active'"
};

test("F# work start matches production's real begin transition for a brand-new work item, including a freshly created telemetry execution", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const fsharpRoot = fixture(t, "new-fsharp");

  const fsharpResult = runFsharp(fsharpRoot, ["--id", "WI-NEW", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  const fsharpContext = readContext(fsharpRoot);
  assert.deepEqual(GOLDEN.test1Context, stripVolatile(fsharpContext));
  const fsharpItem = fsharpContext.workItems.find((item) => item.id === "WI-NEW");
  assert.equal(GOLDEN.test1ExecIdsLength, 1);
  assert.equal(fsharpItem.telemetryExecutionIds.length, 1);

  const fsharpEvents = readEvents(fsharpRoot);
  assert.deepEqual(GOLDEN.test1Events, stripVolatile(fsharpEvents));
  const fsharpStarted = fsharpEvents.find((event) => event.workItem === "WI-NEW");
  assert.equal(GOLDEN.test1StartedTelemetryExecutionsLength, 1);
  assert.equal(fsharpStarted.telemetryExecutions.length, 1);

  const fsharpExecutions = readExecutions(fsharpRoot);
  assert.equal(GOLDEN.test1ExecutionsLength, 1);
  assert.equal(fsharpExecutions.length, 1);
  assert.deepEqual(GOLDEN.test1Execution0, stripVolatile(fsharpExecutions[0]));
  assert.equal(GOLDEN.test1CapabilitiesLength, fsharpExecutions[0].capabilities.length);

  const baseline = fsharpExecutions[0].capabilities.find((entry) => entry.metricId === "git.baseline_dirty_files");
  assert.equal(baseline.status, "derived");
  assert.ok(Array.isArray(baseline.history) && baseline.history.length === 1);
  assert.equal(baseline.historyOmitted, undefined);
});

test("F# work start promotes a ready backlog item exactly like production, without touching its own backlog status", (t) => {
  const fsharpRoot = fixture(t, "ready-fsharp");
  writeQueue(fsharpRoot, queueFixture);

  const fsharpResult = runFsharp(fsharpRoot, ["--id", "WI-READY", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);

  assert.deepEqual(GOLDEN.test2Context, stripVolatile(readContext(fsharpRoot)));

  const fsharpQueue = JSON.parse(fs.readFileSync(path.join(fsharpRoot, ".ros", "work", "queue.json"), "utf8"));
  const readyItem = fsharpQueue.items.find((item) => item.id === "WI-READY");
  assert.equal(readyItem.status, "ready");
  // Production's own `work start` leaves an already-ready backlog item's
  // queue entry completely untouched (verified once against production's
  // real output, which was byte-identical to the input fixture).
  assert.deepEqual(queueFixture, fsharpQueue);
});

test("F# work start rejects a captured (not-ready) backlog item with production's exact message, leaving context untouched", (t) => {
  const fsharpRoot = fixture(t, "captured-fsharp");
  writeQueue(fsharpRoot, queueFixture);
  const contextBefore = readContext(fsharpRoot);

  assert.match(GOLDEN.test3Message, /cannot start backlog item 'WI-CAPTURED' from 'captured'; mark it ready first/);
  const fsharpResult = runFsharp(fsharpRoot, ["--id", "WI-CAPTURED", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /cannot start backlog item 'WI-CAPTURED' from 'captured'; mark it ready first/);
  assert.deepEqual(readContext(fsharpRoot), contextBefore);
});

test("F# work start rejects an abandoned backlog item with production's exact message", (t) => {
  const fsharpRoot = fixture(t, "abandoned-fsharp");
  writeQueue(fsharpRoot, queueFixture);

  assert.match(GOLDEN.test4Message, /cannot start backlog item 'WI-ABANDONED': it was abandoned/);
  const fsharpResult = runFsharp(fsharpRoot, ["--id", "WI-ABANDONED", "--occurred-at", "2026-09-09T18:00:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /cannot start backlog item 'WI-ABANDONED': it was abandoned/);
});

test("F# work start rejects starting an already-active work item with production's exact illegal-transition message", (t) => {
  const fsharpRoot = fixture(t, "active-fsharp");

  runFsharp(fsharpRoot, ["--id", "WI-ACTIVE", "--occurred-at", "2026-09-09T18:00:00.000Z", "--type", "task"]);

  assert.match(GOLDEN.test5Message, /cannot begin 'WI-ACTIVE' from 'active'/);
  const fsharpResult = runFsharp(fsharpRoot, ["--id", "WI-ACTIVE", "--occurred-at", "2026-09-09T18:05:00.000Z"]);
  assert.equal(fsharpResult.status, 1);
  assert.match(fsharpResult.stderr, /cannot begin 'WI-ACTIVE' from 'active'/);
});

function readContext(root) {
  return withoutProvenance(readContextRaw(root));
}

function readEvents(root) {
  return withoutProvenance(readEventsRaw(root));
}

function readExecutions(root) {
  return withoutProvenance(readExecutionsRaw(root));
}
