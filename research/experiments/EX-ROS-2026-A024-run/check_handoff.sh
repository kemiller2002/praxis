#!/usr/bin/env bash
# Orchestrator helper: validate the arm-C handoff for ITEM at the current arm-3 head,
# list the handoff directory, and archive a copy. Usage: check_handoff.sh LABEL ITEM
set -euo pipefail
R=research/experiments/EX-ROS-2026-A024-run
git fetch -q origin experiment/a024-arm-3
W="$(mktemp -d)/wt"; git worktree add -q --detach "$W" origin/experiment/a024-arm-3
trap 'git worktree remove --force "$W"' EXIT
F=research/experiments/EX-ROS-2026-A024-handoffs/$2.json
ls "$W/research/experiments/EX-ROS-2026-A024-handoffs/"
(cd "$W" && python3 research/experiments/EX-ROS-2026-A024-harness/validate_handoff.py "$F" --item "$2")
cp "$W/$F" "$R/platform/$1.handoff-$2.json"; sha256sum "$R/platform/$1.handoff-$2.json"
