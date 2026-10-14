#!/usr/bin/env bash
# EX-ROS-2026-A024: verify the sanitized start and the arm branches' starting trees.
#
# Checks: (1) the start commit's only parent is the frozen baseline; (2) the
# start adds exactly the four harness files and changes nothing else; (3) a
# fresh rebuild with build_start.sh reproduces the same SHA; (4) each named
# ref (default: the three arm branches on origin) either is the start commit or
# has the start commit as the first commit after the baseline on its
# first-parent chain, so every arm began from the identical tree.
# Usage: verify_start.sh START_SHA [REF...]
set -euo pipefail

readonly BASELINE=8b4ffa392e93b19bf39f6672a608954c934cb815
readonly START="$1"; shift
REFS=("$@")
((${#REFS[@]})) || REFS=(origin/experiment/a024-arm-1 origin/experiment/a024-arm-2 origin/experiment/a024-arm-3)
readonly HERE="$(cd "$(dirname "$0")" && pwd)"
fail() { echo "FAIL $*"; exit 1; }

[[ "$(git rev-parse "$START^@")" == "$BASELINE" ]] || fail "start parent is not the baseline"
expected=$'A\tresearch/experiments/EX-ROS-2026-A024-harness/handoff.schema.json\nA\tresearch/experiments/EX-ROS-2026-A024-harness/harness-note.txt\nA\tresearch/experiments/EX-ROS-2026-A024-harness/session_telemetry.py\nA\tresearch/experiments/EX-ROS-2026-A024-harness/validate_handoff.py'
[[ "$(git diff --name-status "$BASELINE" "$START")" == "$expected" ]] || fail "start changes more than the harness files"
[[ "$(bash "$HERE/build_start.sh")" == "$START" ]] || fail "build_start.sh does not reproduce $START"
echo "OK start $START: parent $BASELINE, harness-only, reproducible"
for ref in "${REFS[@]}"; do
  sha="$(git rev-parse --verify --quiet "$ref^{commit}")" || fail "$ref does not exist"
  first="$(git rev-list --first-parent --reverse "$BASELINE..$sha" | head -1)"
  [[ "$first" == "$START" ]] || fail "$ref ($sha) does not start at $START"
  echo "OK $ref $sha starts at tree $(git rev-parse "$START^{tree}")"
done
