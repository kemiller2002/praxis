#!/usr/bin/env python3
"""Assembles the EX-ROS-2026-A024 frozen-analysis input from the run bundle (after unblinding).

Arm membership: the completed session of every item slot (the valid attempt).
Pre-start launcher failures (no implementation, no telemetry) are excluded from
the primary measures and reported in a sensitivity block. Missing stays null.
Usage: python3 assemble_analysis.py MAPPING_JSON OUTPUT.json
"""
import datetime
import json
import sys

RUN = "research/experiments/EX-ROS-2026-A024-run"
ARMS = {"A": ("continuous", "arm-1"), "B": ("code-only", "arm-2"), "C": ("handoff", "arm-3")}


def stamp(v):
    return datetime.datetime.fromisoformat(v.replace("Z", "+00:00"))


def telemetry(label):
    return json.load(open(f"{RUN}/telemetry/{label}.json"))


def total(values):
    values = list(values)
    return None if any(v is None for v in values) else sum(values)


def arm_block(prefix, sessions, mapping, condition):
    done = [s for s in sessions if s["label"].startswith(prefix) and s["status"] == "completed"]
    failed = [s for s in sessions if s["label"].startswith(prefix) and s["status"] != "completed"]
    tel = [telemetry(s["label"]) for s in done]
    parts = lambda t: [t["main"]] + t["subagents"]
    label = mapping[condition]
    scores = json.load(open(f"{RUN}/acceptance/{label}.json"))
    status = [c["status"] for c in scores["criteria"]]
    evaluation = next(s for s in json.load(open(f"{RUN}/evaluation/scores.json"))["snapshots"] if s["label"] in (label, label.replace("snapshot-", "")))
    usage = lambda ss, k: total(s["platformUsage"].get(k) for s in ss)
    wall = lambda ss: sum(int((stamp(s["terminalAt"]) - stamp(s["createdAt"])).total_seconds() * 1000) for s in ss)
    return {
        "condition": condition, "snapshot": label, "valid": True, "invalidReasons": [],
        "sessions": [s["label"] for s in done], "excludedOrchestrationFailures": [s["label"] for s in failed],
        "acceptance": {"passed": status.count("pass"), "failed": status.count("fail"),
                       "indeterminate": status.count("indeterminate"), "confirmedDefects": len(scores.get("confirmedDefects", []))},
        "architecture": {"composite": evaluation["composite"], "dimensions": [d["score"] for d in evaluation["dimensions"]],
                         "divergencePoints": len(evaluation.get("divergencePoints", []))},
        "discovery": {
            "fileReads": sum(p["fileReads"] for t in tel for p in parts(t)),
            "searches": sum(p["searches"] for t in tel for p in parts(t)),
            "mainOnlyFileReads": sum(t["main"]["fileReads"] for t in tel),
            "mainOnlySearches": sum(t["main"]["searches"] for t in tel),
            "repeatedReadExcessWithinSessions": sum(t["main"]["repeatedReadExcess"] for t in tel),
            "crossSessionRepeatedPaths": len({p for i, t in enumerate(tel) for p in t["main"]["readPaths"]
                                              if any(p in u["main"]["readPaths"] for u in tel[:i])}),
            "governanceReads": sum(sum(t["main"]["governanceReads"].values()) for t in tel),
        },
        "resources": {
            "costUsd": usage(done, "cost_usd"), "outputTokens": usage(done, "output_tokens"),
            "inputTokens": usage(done, "input_tokens"), "cacheReadTokens": usage(done, "cache_read_tokens"),
            "cacheWriteTokens": usage(done, "cache_write_tokens"),
            "elapsedMs": wall(done), "telemetryWallMs": sum(t["main"]["wallMs"] for t in tel),
            "modelRequests": sum(t["main"]["modelRequests"] for t in tel),
            "toolCalls": sum(sum(t["main"]["toolCalls"].values()) for t in tel),
            "builds": sum(t["main"]["builds"] for t in tel), "testRuns": sum(t["main"]["testRuns"] for t in tel),
            "compactions": sum(t["main"]["compactions"] for t in tel), "sessions": len(done),
            "msToFirstCodeMutation": [t["main"]["msToFirstCodeMutation"] for t in tel],
            "peakContextTokens": max(t["main"]["peakContextTokens"] or 0 for t in tel),
        },
        "includingOrchestrationFailures": {"costUsd": usage(done + failed, "cost_usd"),
                                           "outputTokens": usage(done + failed, "output_tokens"),
                                           "elapsedMs": wall(done + failed)},
        "handoffOverhead": {s["label"]: telemetry(s["label"])["main"]["handoff"] for s in done} if prefix == "arm-3" else None,
    }


def main():
    mapping = json.load(open(sys.argv[1]))["mapping"]
    sessions = json.load(open(f"{RUN}/sessions.json"))["sessions"]
    arms = {k: arm_block(p, sessions, mapping, c) for k, (c, p) in ARMS.items()}
    json.dump({"assembled": "after unblinding", "mapping": mapping, "arms": arms}, open(sys.argv[2], "w"), indent=2)


if __name__ == "__main__":
    main()
