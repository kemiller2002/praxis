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

// Golden masters below were captured once from production's own Node
// implementation (tools/ros_telemetry.mjs's TELEMETRY_ADAPTERS constant and
// showTelemetry function) with the exact same call sequence as each test,
// then frozen here. Node is retained in this repository only as the web
// server's internal dependency (DF-ROS-2026-A033) and is no longer executed
// as a live oracle by this test suite.
const TELEMETRY_ADAPTERS = [
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

// A single active, task-type work item's execution record (after
// stripVolatile), captured from a fresh bootstrap + `startWork(root,
// ["WI-A"], { type: "task" })` -- confirmed byte-identical (after
// stripVolatile) across every test below that produces this exact shape.
const WI_A_TASK_RECORD = {
  "schemaVersion": "1.0.0",
  "workItemId": "WI-A",
  "status": "active",
  "identity": {
    "provider": "unknown",
    "model": null,
    "modelVersion": null,
    "runtime": "unknown",
    "runtimeVersion": null,
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
      "repository": "telemetry-show-differential"
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
    "workItemId": "WI-A",
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
};

// Same shape, for a "research"-type work item (only workItemId and
// classification.types differ from WI_A_TASK_RECORD -- confirmed by a
// line-level diff of the two real captures).
const WI_B_RESEARCH_RECORD = {
  ...WI_A_TASK_RECORD,
  workItemId: "WI-B",
  classification: { ...WI_A_TASK_RECORD.classification, types: ["research"] },
  links: { ...WI_A_TASK_RECORD.links, workItemId: "WI-B" }
};

function fixture(t, label) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), `ros-telemetry-show-${label}-`));
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
  initializeProject({ target: root, project: "Telemetry Show Differential" });
  execFileSync("git", ["init", "-q"], { cwd: root });
  execFileSync("git", ["config", "user.email", "test@example.invalid"], { cwd: root });
  execFileSync("git", ["config", "user.name", "ROS Test"], { cwd: root });
  execFileSync("git", ["add", "."], { cwd: root });
  execFileSync("git", ["commit", "-qm", "baseline"], { cwd: root });
  return root;
}

function runFsharp(root, args) {
  const result = spawnSync("dotnet", [fsharpCli, "--root", root, "telemetry", ...args], { cwd: repositoryRoot, encoding: "utf8", env: DETERMINISTIC_ENV });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

const VOLATILE_KEYS = new Set([
  "executionId", "startedAt", "discoveredAt", "lastAssessedAt", "recordedAt", "collectedAt",
  "measurementId", "commit", "branch", "dirtyPaths", "dirty", "commits", "occurredAt",
  "eventId", "updatedAt", "telemetryExecutionIds", "telemetryExecutions", "completedAt",
  "createdAt", "finalizedAt", "startCommit", "endCommit", "sessionId"
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

test("F# telemetry adapters matches production's static catalog exactly", (t) => {
  assert.ok(fs.existsSync(fsharpCli), "build:fsharp must produce the shadow CLI before this test runs");
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "ros-telemetry-adapters-"));
  t.after(() => {
    try {
      fs.rmSync(root, { recursive: true, force: true });
    } catch {
      // Cleanup best-effort: a leftover temp dir under CI I/O contention isn't a test failure.
    }
  });
  const fsharpResult = runFsharp(root, ["adapters"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.deepEqual(withoutProvenance(JSON.parse(fsharpResult.stdout)), TELEMETRY_ADAPTERS);
});

test("F# telemetry show with no target lists every execution record, matching production's real showTelemetry", (t) => {
  const fsharpRoot = fixture(t, "list-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "research"], { env: DETERMINISTIC_ENV });

  const fsharpResult = runFsharp(fsharpRoot, ["show"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecords = withoutProvenance(JSON.parse(fsharpResult.stdout));

  assert.equal(fsharpRecords.length, 2);
  assert.deepEqual([WI_A_TASK_RECORD, WI_B_RESEARCH_RECORD], stripVolatile(fsharpRecords));
  assert.equal(fsharpRecords[0].workItemId, "WI-A");
  assert.equal(fsharpRecords[1].workItemId, "WI-B");
});

test("F# telemetry show with no executions yet lists an empty array, matching production", (t) => {
  const fsharpRoot = fixture(t, "empty-fsharp");

  const fsharpResult = runFsharp(fsharpRoot, ["show"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  assert.deepEqual(withoutProvenance(JSON.parse(fsharpResult.stdout)), []);
});

test("F# telemetry show TARGET filters to a matching work item, and an unmatched id returns an empty array, matching production", (t) => {
  const fsharpRoot = fixture(t, "workitem-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });
  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-B", "--occurred-at", "2026-09-10T18:00:01.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });

  const fsharpResult = runFsharp(fsharpRoot, ["show", "WI-A"]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecords = withoutProvenance(JSON.parse(fsharpResult.stdout));

  assert.equal(fsharpRecords.length, 1);
  assert.deepEqual([WI_A_TASK_RECORD], stripVolatile(fsharpRecords));
  assert.equal(fsharpRecords[0].workItemId, "WI-A");

  const fsharpGhostResult = runFsharp(fsharpRoot, ["show", "WI-GHOST"]);
  assert.equal(fsharpGhostResult.status, 0, fsharpGhostResult.stderr);
  assert.deepEqual(JSON.parse(fsharpGhostResult.stdout), []);
});

test("F# telemetry show EXE-ID resolves the single matching record, and an unknown execution id is rejected with production's exact message", (t) => {
  const fsharpRoot = fixture(t, "execid-fsharp");

  execFileSync("dotnet", [fsharpCli, "--root", fsharpRoot, "work", "start", "--id", "WI-A", "--occurred-at", "2026-09-10T18:00:00.000Z", "--type", "task"], { env: DETERMINISTIC_ENV });

  const fsharpExecutionId = JSON.parse(runFsharp(fsharpRoot, ["show", "WI-A"]).stdout)[0].executionId;

  const fsharpResult = runFsharp(fsharpRoot, ["show", fsharpExecutionId]);
  assert.equal(fsharpResult.status, 0, fsharpResult.stderr);
  const fsharpRecord = withoutProvenance(JSON.parse(fsharpResult.stdout));

  assert.deepEqual(WI_A_TASK_RECORD, stripVolatile(fsharpRecord));
  assert.equal(fsharpRecord.workItemId, "WI-A");

  const fsharpGhostResult = runFsharp(fsharpRoot, ["show", "EXE-GHOST"]);
  assert.equal(fsharpGhostResult.status, 1);
  assert.match(fsharpGhostResult.stderr, /telemetry execution 'EXE-GHOST' was not found/);
});
