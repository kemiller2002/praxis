#!/usr/bin/env bash
# EX-ROS-2026-A024: create the sanitized common start commit reproducibly.
#
# The commit's parent is exactly the frozen baseline; it adds only the
# experiment-local instrumentation listed in START_FILES (byte copies from this
# harness directory) under the same path. Author, committer and dates are fixed,
# so rerunning this script from the same harness files yields the same SHA.
# Prints the commit SHA. Usage: build_start.sh  (run from the repository root)
set -euo pipefail

readonly BASELINE=8b4ffa392e93b19bf39f6672a608954c934cb815
readonly HARNESS=research/experiments/EX-ROS-2026-A024-harness
readonly START_FILES=(handoff.schema.json harness-note.txt session_telemetry.py validate_handoff.py)
readonly SCRATCH="$(mktemp -d)"
trap 'rm -rf "$SCRATCH"' EXIT

export GIT_INDEX_FILE="$SCRATCH/index"
git read-tree "$BASELINE"
for file in "${START_FILES[@]}"; do
  blob="$(git hash-object -w "$HARNESS/$file")"
  git update-index --add --cacheinfo "100644,$blob,$HARNESS/$file"
done
tree="$(git write-tree)"

export GIT_AUTHOR_NAME="EX-ROS-2026-A024 harness" GIT_AUTHOR_EMAIL="noreply@anthropic.com"
export GIT_COMMITTER_NAME="$GIT_AUTHOR_NAME" GIT_COMMITTER_EMAIL="$GIT_AUTHOR_EMAIL"
export GIT_AUTHOR_DATE="2026-10-07T00:00:00Z" GIT_COMMITTER_DATE="2026-10-07T00:00:00Z"
git commit-tree "$tree" -p "$BASELINE" -m "EX-ROS-2026-A024: experiment harness (session telemetry and handoff validator)

Identical instrumentation commit at the start of every arm. It adds only
experiment-local files and changes no product code, tests, work items or
guidance."
