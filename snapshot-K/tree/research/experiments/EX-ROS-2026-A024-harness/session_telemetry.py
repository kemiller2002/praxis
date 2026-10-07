#!/usr/bin/env python3
"""EX-ROS-2026-A024 session telemetry (versioned copy of the A021 extractor).

Deterministically summarizes the Claude Code transcript(s) of the session that
runs it and audits the session's own commands for experiment-isolation events.
It reads only local files and writes only outside the repository
(~/a024-telemetry/LABEL.json); it never commits or pushes, so nothing reaches
the arm branch where a later session could read it. The metrics are printed as
one JSON line with its SHA-256, which the orchestrator retrieves from the
session's platform event record. (A preflight showed the platform's auto-mode
classifier blocks pushing a transcript from an arm session.)

Nothing is inferred by a model. A value the transcript does not carry is null.
Usage lines repeated per content block are counted once per message id.
Metric definitions are those of EX-ROS-2026-A021 session_metrics.py
(schema ex-ros-2026-a021.session-metrics/1), extended with subagent
transcripts, per-request executor configuration, an isolation audit and
handoff-overhead counters.

Usage:
  python3 session_telemetry.py --label LABEL --branch ARM_BRANCH [--out DIR]
"""
import argparse
import base64
import gzip
import collections
import datetime
import glob
import hashlib
import json
import os
import re
import subprocess
import sys
from functools import reduce

SCHEMA = "ex-ros-2026-a024.session-telemetry/1"
ARM_BRANCHES = ("experiment/a024-snapshot-K", "experiment/a024-snapshot-K", "experiment/a024-snapshot-K")
HANDOFF_DIR = "research/experiments/EX-ROS-2026-A024-handoffs/"

GOVERNANCE = ("AGENTS.md", "CLAUDE.md", "docs/00-governance/", "docs/work-protocol.md",
              "docs/planning.md", "docs/cli.md", "requirements/PLANNING-WORK-GROUPS.md",
              "docs/development-telemetry.md", "docs/agent-provenance.md")
# A021 definitions, unchanged.
READ_CMD = re.compile(r"(?:^|[;&(]\s*)(?:cat|head|tail|less|sed\s+-n\s+'?[0-9,$p]+'?)((?:\s+[^\s;|&<>]+)+)")
SEARCH_CMD = re.compile(r"(?:^|[;&(]\s*)(?:grep|rg|find|git\s+grep)\b")
PATH_TOKEN = re.compile(r"^[\w./-]+\.[\w]+$|^[\w.-]+/[\w./-]+$")
BUILD_CMD = re.compile(r"dotnet\s+build|build:fsharp")
TEST_CMD = re.compile(r"Ros\.Tests\.dll|node\s+--test|dotnet\s+test|npm\s+(?:run\s+)?test")
WRITE_CMD = re.compile(r"(?:cat|tee)\s+>{1,2}\s*\S+|sed\s+-i|open\([^)]*['\"]w['\"]|>\s*(?:src|tests)/")

# Isolation audit (A024 contamination controls). A match is a candidate event
# for the orchestrator to classify under the frozen rules; it is not a verdict.
AUDIT_RULES = (
    ("main-ref", re.compile(r"\borigin/main\b|\brefs/heads/main\b|\bFETCH_HEAD\b|\b(?:checkout|switch|show|log|diff|merge|rebase|pull|fetch)\b[^\n;|&]*\bmain\b")),
    ("a021-ref", re.compile(r"a021-(?:control|grouped|r2|arm|evaluation)|EX-ROS-2026-A021-(?:R2|evaluation|control|grouped)", re.I)),
    ("unscoped-fetch", re.compile(r"\bgit\s+(?:fetch|pull)(?:\s+(?:--all|-a|--prune|-p|--tags|origin))*\s*(?:$|[;&|)])")),
    ("ref-listing", re.compile(r"\bgit\s+(?:ls-remote|branch\s+(?:-a|-r|--all|--remotes)|log\s+[^\n;|&]*--all|remote\s+show)")),
    ("telemetry-ref", re.compile(r"a024-telemetry")),
    ("github-remote-read", re.compile(r"api\.github\.com|github\.com/[^\s]+/(?:tree|blob|commits?|compare|pull)/")),
)
REMOTE_TOOLS = re.compile(r"^mcp__github__|^WebFetch$|^WebSearch$")
def stamp(value):
    return datetime.datetime.fromisoformat(value.replace("Z", "+00:00")) if value else None


def relative(path, cwd):
    root = (cwd or "").rstrip("/") + "/"
    return path[len(root):] if path and cwd and path.startswith(root) else path


def load(path):
    with open(path, encoding="utf-8") as handle:
        return [json.loads(line) for line in handle if line.strip()]


def transcripts():
    """The main transcript and that session's subagent transcripts.

    The session's own transcript is named by CLAUDE_CODE_SESSION_ID when the
    runtime sets it; otherwise the newest top-level transcript is used."""
    tops = glob.glob(os.path.expanduser("~/.claude/projects/*/*.jsonl"))
    own = [p for p in tops if os.path.basename(p) == f"{os.environ.get('CLAUDE_CODE_SESSION_ID', '')}.jsonl"]
    if not tops:
        sys.exit("no transcript found under ~/.claude/projects")
    main = own[0] if own else max(tops, key=os.path.getmtime)
    session_dir = main[: -len(".jsonl")]
    subs = sorted(glob.glob(os.path.join(session_dir, "**", "*.jsonl"), recursive=True))
    return main, subs


REDIRECT_TARGET = re.compile(r"(?:>{1,2}|\btee\s+(?:-a\s+)?|\bsed\s+-i\S*\s+(?:'[^']*'|\"[^\"]*\"|\S+)\s+)\s*['\"]?(?:\./|/home/user/[^/\s]+/)?((?:src|tests)/[^\s'\";|&)]+)")


def real(entry):
    """A placeholder written for an API error or interrupt is not a model request."""
    return (entry.get("message") or {}).get("model") != "<synthetic>"


def tool_uses(entries):
    return [(entry, block)
            for entry in entries if entry.get("type") == "assistant"
            for block in ((entry.get("message") or {}).get("content") or [])
            if isinstance(block, dict) and block.get("type") == "tool_use"]


def usage_by_request(entries):
    return reduce(lambda acc, e: {**acc, request_key(e): e["message"]["usage"]}
                  if e.get("type") == "assistant" and real(e) and (e.get("message") or {}).get("usage") else acc,
                  entries, {})


def request_key(entry):
    return (entry.get("message") or {}).get("id") or entry.get("requestId") or entry.get("uuid")


def per_request(assistant, field):
    """Counts a field once per model request (a request spans several transcript entries)."""
    return dict(collections.Counter(str(field(e)) for e in {request_key(e): e for e in assistant}.values()))


def bash_reads(command, cwd):
    return [relative(token, cwd)
            for group in READ_CMD.findall(command)
            for token in group.split()
            if not token.startswith("-") and PATH_TOKEN.match(token)]


def tool_facts(entry, block, cwd):
    """One normalized fact per tool call: what it read, searched, wrote, built, tested."""
    name, args = block.get("name"), block.get("input") or {}
    command = args.get("command") or "" if name == "Bash" else ""
    path = relative(args.get("file_path") or args.get("path") or args.get("notebook_path"), cwd) or ""
    reads = [path] if name == "Read" else bash_reads(command, cwd) if name == "Bash" else []
    writes = [path] if name in ("Edit", "Write", "NotebookEdit") else []
    bash_write = bool(name == "Bash" and WRITE_CMD.search(command))
    text = " ".join([command, path, json.dumps(args, sort_keys=True) if name not in ("Bash", "Read", "Edit", "Write") else ""])
    return {
        "name": name,
        "when": stamp(entry.get("timestamp")),
        "reads": reads,
        "searches": (1 if name in ("Grep", "Glob") else 0) + (len(SEARCH_CMD.findall(command)) if name == "Bash" else 0),
        "writes": writes,
        "bashWrite": bash_write,
        "build": bool(BUILD_CMD.search(command)),
        "test": bool(TEST_CMD.search(command)),
        "codeMutation": any(w.startswith(("src/", "tests/")) for w in writes)
                        or bool(name == "Bash" and REDIRECT_TARGET.search(command)),
        "command": command,
        "text": text,
        "handoff": HANDOFF_DIR in text,
    }


def audit(facts, own_branch):
    others = tuple(b for b in ARM_BRANCHES if b != own_branch)
    def hits(fact):
        text = fact["text"]
        rules = [rule for rule, pattern in AUDIT_RULES if pattern.search(text)]
        rules += ["other-arm-ref"] if any(b in text for b in others) else []
        rules += ["remote-tool"] if REMOTE_TOOLS.search(fact["name"] or "") else []
        return rules
    events = [{"at": f["when"].isoformat() if f["when"] else None, "tool": f["name"], "rules": r, "text": f["text"][:240]}
              for f in facts for r in [hits(f)] if r]
    return {"count": len(events), "events": events[:60]}


def summarize(entries, cwd, own_branch):
    times = sorted(t for t in (stamp(e.get("timestamp")) for e in entries) if t)
    usage = usage_by_request(entries)
    facts = [tool_facts(entry, block, cwd) for entry, block in tool_uses(entries)]
    reads = collections.Counter(p for f in facts for p in f["reads"] if p)
    writes = collections.Counter(p for f in facts for p in f["writes"] if p)
    first_code = next((f["when"] for f in facts if f["codeMutation"]), None)
    assistant = [e for e in entries if e.get("type") == "assistant" and real(e)]
    total = lambda field: sum((u.get(field) or 0) for u in usage.values())
    errors = sum(1 for e in entries if e.get("type") == "user"
                 for b in ((e.get("message") or {}).get("content") or [] if isinstance((e.get("message") or {}).get("content"), list) else [])
                 if isinstance(b, dict) and b.get("type") == "tool_result" and b.get("is_error"))
    start, end = (times[0], times[-1]) if times else (None, None)
    handoff_facts = [f for f in facts if f["handoff"]]
    return {
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
        "peakContextTokens": max((sum((u.get(k) or 0) for k in ("input_tokens", "cache_read_input_tokens", "cache_creation_input_tokens"))
                                  for u in usage.values()), default=None),
        "executor": {
            "countedPer": "model request",
            "models": per_request(assistant, lambda e: (e.get("message") or {}).get("model")),
            "effort": per_request(assistant, lambda e: e.get("effort")),
            "runtimeVersion": per_request(assistant, lambda e: e.get("version")),
            "entrypoint": per_request(assistant, lambda e: e.get("entrypoint")),
        },
        "toolCalls": dict(sorted(collections.Counter(f["name"] for f in facts).items())),
        "toolErrors": errors,
        "fileReads": sum(reads.values()),
        "distinctFilesRead": len(reads),
        "repeatedReadPaths": sum(1 for c in reads.values() if c > 1),
        "repeatedReadExcess": sum(c - 1 for c in reads.values() if c > 1),
        "repeatedReadsTop": dict(sorted(((p, c) for p, c in reads.items() if c > 1), key=lambda pc: (-pc[1], pc[0]))[:40]),
        "readPaths": sorted(reads),
        "governanceReads": dict(sorted((p, c) for p, c in reads.items() if p.startswith(GOVERNANCE))),
        "searches": sum(f["searches"] for f in facts),
        "fileWrites": sum(writes.values()) + sum(1 for f in facts if f["bashWrite"]),
        "distinctFilesEdited": len(writes),
        "builds": sum(1 for f in facts if f["build"]),
        "testRuns": sum(1 for f in facts if f["test"]),
        "compactions": (lambda boundaries, summaries: boundaries if boundaries else summaries)(
            sum(1 for e in entries if e.get("type") == "system" and e.get("subtype") == "compact_boundary"),
            sum(1 for e in entries if e.get("isCompactSummary"))),
        "syntheticEntries": sum(1 for e in entries if e.get("type") == "assistant" and not real(e)),
        "handoff": {
            "toolCalls": len(handoff_facts),
            "reads": sum(1 for f in handoff_facts if f["reads"]),
            "writes": sum(1 for f in handoff_facts if f["writes"] or f["bashWrite"]),
            "firstAt": handoff_facts[0]["when"].isoformat() if handoff_facts and handoff_facts[0]["when"] else None,
            "lastAt": handoff_facts[-1]["when"].isoformat() if handoff_facts and handoff_facts[-1]["when"] else None,
            "paths": sorted({p for f in handoff_facts for p in f["reads"] + f["writes"] if HANDOFF_DIR in p}),
        },
        "isolationAudit": audit(facts, own_branch),
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--label", required=True)
    parser.add_argument("--branch", required=True)
    parser.add_argument("--out", default=os.path.expanduser("~/a024-telemetry"))
    options = parser.parse_args()

    main_path, sub_paths = transcripts()
    main_entries = load(main_path)
    cwd = next((e.get("cwd") for e in main_entries if e.get("cwd")), None)
    head = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip() or None
    result = {
        "schema": SCHEMA,
        "label": options.label,
        "branch": options.branch,
        "headAtCapture": head,
        "capturedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "sessionId": next((e.get("sessionId") for e in main_entries if e.get("sessionId")), None),
        "transcript": os.path.basename(main_path),
        "transcriptSha256": {os.path.basename(p): hashlib.sha256(open(p, "rb").read()).hexdigest() for p in [main_path] + sub_paths},
        "main": summarize(main_entries, cwd, options.branch),
        "subagents": [{"file": os.path.basename(p), **summarize(load(p), cwd, options.branch)} for p in sub_paths],
        "notes": "A021 metric definitions; reads include Read calls and cat/head/tail/sed -n in Bash; bash writes are pattern-matched and approximate; isolationAudit lists candidate events for frozen-rule classification, not verdicts; capture precedes the session's final checkpoint, so the tail after capture is covered only by platform usage",
    }
    compact = json.dumps(result, sort_keys=True, separators=(",", ":"))
    os.makedirs(options.out, exist_ok=True)
    with open(os.path.join(options.out, f"{options.label}.json"), "w", encoding="utf-8") as handle:
        handle.write(compact + "\n")
    # The orchestrator reads these lines from the session's platform event record:
    # the compact JSON, gzip-compressed (mtime 0) and base64-encoded so it stays far
    # below the runtime's tool-output limit, plus the SHA-256 of the decoded JSON.
    print(f"A024-TELEMETRY-SHA256 {hashlib.sha256(compact.encode()).hexdigest()}")
    print(f"A024-TELEMETRY-GZB64 {base64.b64encode(gzip.compress(compact.encode(), mtime=0)).decode()}")
    print(json.dumps({"label": options.label, "modelRequests": result["main"]["modelRequests"],
                      "auditEvents": result["main"]["isolationAudit"]["count"]}))


if __name__ == "__main__":
    main()
