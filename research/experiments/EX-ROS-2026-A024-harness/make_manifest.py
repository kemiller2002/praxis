#!/usr/bin/env python3
"""Writes the EX-ROS-2026-A024 frozen manifest (manifest.json) from the harness files.

Every hash is recomputed from the committed files, so the manifest can be
re-derived and checked: `python3 make_manifest.py --check` exits 1 when any
frozen file no longer matches its recorded hash.
"""
import hashlib
import json
import os
import random
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REL = "research/experiments/EX-ROS-2026-A024-harness"
BASELINE = "8b4ffa392e93b19bf39f6672a608954c934cb815"
SEED = 2026100724
ITEMS = [f"PRAXIS-GROUP-0{n}" for n in range(1, 6)]


def sha(path):
    return hashlib.sha256(open(os.path.join(HERE, path), "rb").read()).hexdigest()


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True).stdout.strip()


def frozen_files():
    prompts = sorted(f"prompts/{p}" for p in os.listdir(os.path.join(HERE, "prompts")))
    return ["handoff.schema.json", "harness-note.txt", "session_telemetry.py", "validate_handoff.py",
            "decode_telemetry.py", "acceptance-criteria.json", "rubric.md", "analysis.py", "prepare_blind.py", "run_checks.sh",
            "build_start.sh", "verify_start.sh", "make_prompts.py"] + prompts


def manifest():
    criteria = json.load(open(os.path.join(HERE, "acceptance-criteria.json")))
    start = git("-c", "core.quotepath=off", "rev-parse", "HEAD") and subprocess.run(
        ["bash", os.path.join(HERE, "build_start.sh")], capture_output=True, text=True, check=True).stdout.strip()
    return {
        "schema": "ex-ros-2026-a024.manifest/1",
        "experimentId": "EX-ROS-2026-A024",
        "protocol": "research/experiments/EX-ROS-2026-A024--context-continuity-and-externalized-handoff.md",
        "baselineCommit": BASELINE,
        "sanitizedStartCommit": start,
        "sanitizedStartTree": git("rev-parse", f"{start}^{{tree}}"),
        "startProcedure": f"{REL}/build_start.sh (fixed identity and dates; reproducible) verified by {REL}/verify_start.sh",
        "cohort": ITEMS,
        "order": "fixed 01..05 within every arm",
        "workItems": [{"id": i["id"], "sha256": i["sha256"]} for i in criteria["items"]],
        "workItemHashAlgorithm": criteria["itemHashAlgorithm"],
        "arms": {
            "A": {"condition": "continuous", "branch": "experiment/a024-arm-1", "sessions": ["arm-1"], "prompt": "prompts/arm-1.txt"},
            "B": {"condition": "code-only", "branch": "experiment/a024-arm-2", "sessions": [f"arm-2-0{n}" for n in range(1, 6)],
                  "prompts": [f"prompts/arm-2-0{n}.txt" for n in range(1, 6)]},
            "C": {"condition": "handoff", "branch": "experiment/a024-arm-3", "sessions": [f"arm-3-0{n}" for n in range(1, 6)],
                  "prompts": [f"prompts/arm-3-0{n}.txt" for n in range(1, 6)]},
        },
        "branchNaming": "neutral arm-1/2/3 names instead of the orchestration prompt's suggested condition names, so branch names do not announce the treatment to arm agents",
        "executor": {
            "provider": "anthropic",
            "runtime": "Claude Code on the web (claude-code-remote sessions)",
            "runtimeVersion": "2.1.292 (container_cc_version and transcript 'version'; observed in orchestrator and preflight probe session_01LF9wTRatrsvBPSCz5tLK7G)",
            "model": "claude-opus-5-5",
            "modelSelection": "create_session model=claude-opus-5-5 for every session",
            "environmentId": "env_016QeyeEJj49imTiwTeC9Tgd",
            "environmentKind": "anthropic_cloud",
            "permissionMode": "auto (inherited from the orchestrator; create_session permission_mode omitted)",
            "reasoningEffort": "medium: the platform default for create_session children (not settable through create_session, not reported by get_session for children), observed as transcript 'effort' = medium on every request of preflight session_01Y6TTvXHcEs8e7eWAR88TsL and bound per request through the telemetry 'effort' field",
            "appendSystemPrompt": None,
            "extraAllowedTools": None,
            "subagents": "available to every session identically (Agent tool); subagent transcripts are included in telemetry",
            "bindingRules": [
                "every main-transcript model request of every implementation session reports model claude-opus-5-5; any other model on a main-transcript request invalidates that session",
                "the transcript 'effort' value is medium on every main-transcript request of every implementation session; a different value invalidates the affected session's arm unless it is corrected before any implementation begins",
                "get_session configured_model, session_context.model and external_metadata.last_served_model equal claude-opus-5-5 at session end",
                "a different Claude Code runtime version between sessions is recorded as a deviation and reported in a sensitivity note; it does not invalidate by itself",
            ],
        },
        "toolchain": {
            "dotnet": "dotnet-sdk-10.0 from Ubuntu apt (10.0.112 observed when frozen); build property -p:FSharpCoreImplicitPackageVersion=10.1.400",
            "node": "v22.22.0", "python": "3.13.16", "git": "2.43.0", "kernel": "6.18.44-fc-v77",
            "baselineBuild": "npm run build:fsharp -- -p:FSharpCoreImplicitPackageVersion=10.1.400 (passed at baseline)",
            "baselineTests": "dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll: 792 passed, 0 failed at baseline",
        },
        "hashes": {path: sha(path) for path in frozen_files()},
        "handoffSchemaSha256": sha("handoff.schema.json"),
        "telemetryExtractorSha256": sha("session_telemetry.py"),
        "rubricSha256": sha("rubric.md"),
        "evaluatorPromptSha256": sha("prompts/evaluator.txt"),
        "acceptanceScorerPromptSha256": sha("prompts/acceptance-scorer.txt"),
        "promptLaunchSubstitution": "{START_SHA} is the only launch-time substitution in an implementation prompt: the recorded branch head the session must start from",
        "randomizationSeed": SEED,
        "launchOrder": random.Random(SEED).sample(["A", "B", "C"], 3),
        "launchPolicy": "the three arms run concurrently, launched in launchOrder; within an arm items run serially 01..05; a B or C session starts only after the preceding session is terminal and its pushed head is recorded",
        "blinding": {"labels": ["snapshot-K", "snapshot-P", "snapshot-W"], "procedure": f"{REL}/prepare_blind.py (seeded permutation; commitment = sha256 of canonical mapping JSON)"},
        "measures": {
            "acceptance": "passed / scorable over the 36 frozen criteria in acceptance-criteria.json, scored by one blinded fresh scorer session per snapshot; indeterminate reported separately",
            "architecture": "blinded rubric composite 0-10 (rubric.md) from one fresh evaluator session that sees only the three anonymized snapshots",
            "discoveryPrimary": "file reads + searches summed over all sessions of the arm, main and subagent transcripts (A021 extractor definitions); the protocol's named fallback proxy, chosen because the bash read/search detection is pattern-based and a repeated-operation aggregate built on it would not be reliable enough to be primary",
            "discoverySecondary": "cross-session repeated reads (paths read in more than one session of an arm, plus within-session repeats) reported descriptively",
            "resources": "platform cost and tokens from get_session external_metadata.usage read after the session is terminal (whole session, including the tail after telemetry capture), plus telemetry tokens and wall time; arm elapsed time = sum of session wall times (created_at to terminal updated_at); missing is null, never zero",
            "telemetryTransport": "session_telemetry.py prints gzip+base64 metrics with a SHA-256 just before the session's final checkpoint; the orchestrator copies that tool output from the platform event record (list_events) and verifies it with decode_telemetry.py. Full raw transcripts are not exported from arm sessions: a preflight (session_01K2uq5tGob5heiRq38Eti7j) showed the platform's auto-mode classifier blocks pushing a session transcript as data exfiltration, and committing it to an arm branch would pass context forward. Raw platform records kept per session: get_session record and the terminal result events (cost, modelUsage, permission denials, turns); the full event stream stays in the platform under the recorded session IDs.",
            "handoffOverhead": "arm C telemetry handoff counters (tool calls touching the handoff directory, first/last timestamps) per session",
        },
        "deterministicChecks": f"{REL}/run_checks.sh at every arm head (build, full F# suite, ./ros validate)",
        "analysis": f"{REL}/analysis.py (recovery >= 0.50, correctness margin 0.05, unclipped ratios)",
        "isolationClassification": [
            "contamination (invalidates the session's arm): reading content, logs or diffs of main, another arm, an A021/R2 branch or evaluation, another session's telemetry, or a non-immediately-preceding handoff (arm C)",
            "deviation (recorded, not invalidating): ref-name exposure without content inspection, such as an unscoped fetch, a ref listing, or a pull-request listing, with no subsequent content read of a forbidden ref",
            "the session_telemetry.py isolationAudit lists candidate events; the orchestrator classifies each against these rules and records the classification",
        ],
        "retryPolicy": [
            "every attempted session gets a unique label and run ID and stays in sessions.json",
            "no silent retry; an orchestration-only failure (session failed to start, platform error before any repository change, harness defect) may be retried once under a new label with the original preserved",
            "a model-generated implementation failure is outcome data and is never rerun",
            "permission prompts, stale checkouts, missing pushes, merge conflicts, tool failures and context exhaustion are measured events",
        ],
        "stopConditions": [
            "the sanitized start cannot be reproduced",
            "arms do not share an identical start tree",
            "model/configuration differs across arms and cannot be corrected before any implementation begins",
            "fresh-session isolation cannot be established",
            "an arm sees another arm or a post-baseline implementation (classified contamination)",
            "the handoff schema is changed after arm C starts",
            "scoring or blinding cannot be frozen before outputs are examined",
        ],
    }


def main():
    path = os.path.join(HERE, "manifest.json")
    if "--check" in sys.argv:
        recorded = json.load(open(path))
        drift = [p for p, h in recorded["hashes"].items() if sha(p) != h]
        print("\n".join(f"CHANGED {p}" for p in drift) or "manifest hashes match")
        sys.exit(1 if drift else 0)
    json.dump(manifest(), open(path, "w"), indent=2)
    print(path)


if __name__ == "__main__":
    main()
