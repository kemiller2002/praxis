#!/usr/bin/env python3
"""EX-ROS-2026-A021 session metrics (PRX-GRP-085).

Deterministically summarizes the Claude Code transcript of the session that
runs it: usage, tool calls, reads, searches, writes, builds, tests, time to
first code mutation and compactions. Nothing is inferred by a model; a value
the transcript does not carry is null. Usage lines repeated per content block
are counted once per message id.

Usage: python3 session_metrics.py OUTPUT.json [--label LABEL]
"""
import collections
import datetime
import glob
import json
import os
import re
import sys

GOVERNANCE = ("AGENTS.md", "CLAUDE.md", "docs/00-governance/", "docs/work-protocol.md",
              "docs/planning.md", "docs/cli.md", "requirements/PLANNING-WORK-GROUPS.md",
              "docs/development-telemetry.md", "docs/agent-provenance.md")
# A read or search starts a command (after ^ ; & && or "("), never a pipe:
# "| head" and "| grep" filter output rather than read the repository.
READ_CMD = re.compile(r"(?:^|[;&(]\s*)(?:cat|head|tail|less|sed\s+-n\s+'?[0-9,$p]+'?)((?:\s+[^\s;|&<>]+)+)")
SEARCH_CMD = re.compile(r"(?:^|[;&(]\s*)(?:grep|rg|find|git\s+grep)\b")
PATH_TOKEN = re.compile(r"^[\w./-]+\.[\w]+$|^[\w.-]+/[\w./-]+$")
BUILD_CMD = re.compile(r"dotnet\s+build|build:fsharp")
TEST_CMD = re.compile(r"Ros\.Tests\.dll|node\s+--test|dotnet\s+test|npm\s+(?:run\s+)?test")
WRITE_CMD = re.compile(r"(?:cat|tee)\s+>{1,2}\s*\S+|sed\s+-i|open\([^)]*['\"]w['\"]|>\s*(?:src|tests)/")


def transcript():
    files = glob.glob(os.path.expanduser("~/.claude/projects/*/*.jsonl"))
    if not files:
        sys.exit("no transcript found under ~/.claude/projects")
    return max(files, key=os.path.getmtime)


def stamp(value):
    return datetime.datetime.fromisoformat(value.replace("Z", "+00:00")) if value else None


def relative(path, cwd):
    if path and cwd and path.startswith(cwd.rstrip("/") + "/"):
        return path[len(cwd.rstrip("/")) + 1:]
    return path


def main():
    output = sys.argv[1]
    label = sys.argv[sys.argv.index("--label") + 1] if "--label" in sys.argv else None
    source = transcript()
    entries = [json.loads(line) for line in open(source, encoding="utf-8") if line.strip()]

    times = [stamp(e["timestamp"]) for e in entries if e.get("timestamp")]
    cwd = next((e.get("cwd") for e in entries if e.get("cwd")), None)
    usage, models = {}, collections.Counter()
    tools, reads, searches, writes = collections.Counter(), collections.Counter(), 0, collections.Counter()
    builds = tests = bash_writes = 0
    first_code = None
    tool_errors = 0

    for entry in entries:
        message = entry.get("message") or {}
        if entry.get("type") == "assistant":
            key = message.get("id") or entry.get("requestId") or entry.get("uuid")
            if message.get("usage"):
                usage[key] = message["usage"]
            if message.get("model"):
                models[message["model"]] += 0 if key in usage and models else 1
            for block in message.get("content") or []:
                if block.get("type") != "tool_use":
                    continue
                name, args = block.get("name"), block.get("input") or {}
                tools[name] += 1
                when = stamp(entry.get("timestamp"))
                if name == "Read":
                    reads[relative(args.get("file_path"), cwd)] += 1
                elif name in ("Grep", "Glob"):
                    searches += 1
                elif name in ("Edit", "Write", "NotebookEdit"):
                    path = relative(args.get("file_path"), cwd) or ""
                    writes[path] += 1
                    if first_code is None and path.startswith(("src/", "tests/")):
                        first_code = when
                elif name == "Bash":
                    command = args.get("command") or ""
                    for group in READ_CMD.findall(command):
                        for token in group.split():
                            if not token.startswith("-") and PATH_TOKEN.match(token):
                                reads[relative(token, cwd)] += 1
                    searches += len(SEARCH_CMD.findall(command))
                    builds += bool(BUILD_CMD.search(command))
                    tests += bool(TEST_CMD.search(command))
                    if WRITE_CMD.search(command):
                        bash_writes += 1
                        if first_code is None and re.search(r"\b(?:src|tests)/", command):
                            first_code = when
        elif entry.get("type") == "user":
            for block in message.get("content") or [] if isinstance(message.get("content"), list) else []:
                if isinstance(block, dict) and block.get("type") == "tool_result" and block.get("is_error"):
                    tool_errors += 1

    total = lambda field: sum((u.get(field) or 0) for u in usage.values())
    compactions = sum(1 for e in entries
                      if (e.get("type") == "system" and "compact" in str(e.get("subtype", "")))
                      or e.get("isCompactSummary"))
    cost = [e for e in entries if e.get("type") == "cost-state"]
    start, end = (min(times), max(times)) if times else (None, None)
    repeated = {path: count for path, count in reads.items() if path and count > 1}

    result = {
        "schema": "ex-ros-2026-a021.session-metrics/1",
        "label": label,
        "sessionId": next((e.get("sessionId") for e in entries if e.get("sessionId")), None),
        "transcript": os.path.basename(source),
        "models": sorted(models),
        "startedAt": start.isoformat() if start else None,
        "endedAt": end.isoformat() if end else None,
        "wallMs": int((end - start).total_seconds() * 1000) if start else None,
        "msToFirstCodeMutation": int((first_code - start).total_seconds() * 1000) if first_code and start else None,
        "modelRequests": len(usage),
        "tokens": {
            "input": total("input_tokens"),
            "output": total("output_tokens"),
            "cacheRead": total("cache_read_input_tokens"),
            "cacheCreation": total("cache_creation_input_tokens"),
        },
        "costUsd": {"value": cost[-1].get("totalCostUSD"), "source": "runtime cost-state (estimate; last record)"} if cost else None,
        "toolCalls": dict(sorted(tools.items())),
        "toolErrors": tool_errors,
        "fileReads": sum(reads.values()),
        "distinctFilesRead": len([p for p in reads if p]),
        "repeatedReads": dict(sorted(repeated.items())),
        "governanceReads": {p: c for p, c in sorted(reads.items()) if p and p.startswith(GOVERNANCE)},
        "searches": searches,
        "fileWrites": sum(writes.values()) + bash_writes,
        "distinctFilesEdited": len([p for p in writes if p]),
        "builds": builds,
        "testRuns": tests,
        "compactions": compactions,
        "notes": "reads include Read calls and cat/head/tail/sed -n in Bash; bash writes are pattern-matched and approximate",
    }
    os.makedirs(os.path.dirname(os.path.abspath(output)), exist_ok=True)
    with open(output, "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
        handle.write("\n")
    print(json.dumps({k: result[k] for k in ("label", "wallMs", "modelRequests", "tokens", "fileReads", "compactions")}))


if __name__ == "__main__":
    main()
