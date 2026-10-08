#!/usr/bin/env bash
# Orchestrator helper: extract and verify one session's printed telemetry from a saved
# list_events result file, and archive that file. Usage: collect.sh LABEL SAVED_FILE
set -euo pipefail
R=research/experiments/EX-ROS-2026-A024-run
grep -o 'A024-TELEMETRY-[A-Z0-9]* [A-Za-z0-9+/=]*' "$2" | sort -u > "$R/telemetry/$1.printed.txt"
python3 research/experiments/EX-ROS-2026-A024-harness/decode_telemetry.py "$R/telemetry/$1.printed.txt" "$R/telemetry/$1.json"
cp "$2" "$R/platform/$1.list_events-user-tail.txt"
python3 - "$R/telemetry/$1.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1])); m = d["main"]
print(d["label"], d["headAtCapture"][:10], m["executor"], "requests", m["modelRequests"], "reads", m["fileReads"], "searches", m["searches"],
      "handoff", m["handoff"]["toolCalls"], m["handoff"]["paths"], "audit", m["isolationAudit"]["count"], "subagents", len(d["subagents"]))
for e in m["isolationAudit"]["events"]:
    print("  AUDIT", e["rules"], e["text"][:200])
PY
