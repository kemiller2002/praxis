#!/usr/bin/env python3
"""Orchestrator ledger updates for EX-ROS-2026-A024 (sessions.json); append-only in spirit:
a session entry is added once and later only gains terminal fields.

  ledger.py launch LABEL ARM CONDITION ITEMS SESSION CREATED_AT START PROMPT [--attempt N]
  ledger.py finish LABEL STATUS HEAD UPDATED_AT USAGE_JSON [--note TEXT]
"""
import json
import sys

PATH = "research/experiments/EX-ROS-2026-A024-run/sessions.json"


def update(transform):
    ledger = json.load(open(PATH))
    json.dump(transform(ledger), open(PATH, "w"), indent=2)


def launch(label, arm, condition, items, session, created, start, prompt, attempt=1):
    entry = {"label": label, "arm": arm, "condition": condition, "items": items.split(","), "session": session,
             "createdAt": created, "configuredModel": "claude-opus-5-5", "branch": f"experiment/a024-{label[:5]}",
             "startCommit": start, "prompt": prompt, "promptSubstitution": {"START_SHA": start},
             "attempt": attempt, "status": "running"}
    update(lambda l: {**l, "sessions": l["sessions"] + [entry]})


def finish(label, status, head, updated, usage, note=None):
    def apply(ledger):
        return {**ledger, "sessions": [
            {**s, "status": status, "headAfter": head, "terminalAt": updated,
             "platformUsage": {"source": "get_session external_metadata.usage", **json.loads(usage)},
             **({"note": note} if note else {})} if s["label"] == label else s
            for s in ledger["sessions"]]}
    update(apply)


if __name__ == "__main__":
    args = sys.argv[1:]
    note = args[args.index("--note") + 1] if "--note" in args else None
    attempt = int(args[args.index("--attempt") + 1]) if "--attempt" in args else 1
    plain = [a for i, a in enumerate(args) if a not in ("--note", "--attempt") and (i == 0 or args[i - 1] not in ("--note", "--attempt"))]
    {"launch": lambda: launch(*plain[1:9], attempt=attempt), "finish": lambda: finish(*plain[1:6], note=note)}[plain[0]]()
