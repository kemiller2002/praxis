#!/usr/bin/env bash
# EX-ROS-2026-A021 evaluation (PRX-GRP-084): build a blind two-arm bundle.
#
# Exports each arm branch as a buildable tree plus a filtered patch series
# under randomly assigned neutral names (arm-X, arm-Y), scrubbing the
# identifiers that name an arm. The mapping is written to a sealed file and
# only its SHA-256 commitment is printed, so the owner can verify it later.
#
# Usage: prepare-blind-bundle.sh [OUT_DIR]
#   env overrides: ARM_A_BRANCH, ARM_B_BRANCH, BASE (start commit B)
set -euo pipefail

readonly REPO="$(git rev-parse --show-toplevel)"
readonly OUT="$(realpath -m "${1:-$HOME/a021-eval}")"
readonly ARM_A_BRANCH="${ARM_A_BRANCH:-experiment/a021-control}"
readonly ARM_B_BRANCH="${ARM_B_BRANCH:-experiment/a021-grouped}"

# Paths whose names or contents identify an arm and carry no code to judge.
readonly EXCLUDED_PATHSPECS=(
  ':(exclude).ros'
  ':(exclude)research/experiments/EX-ROS-2026-A021-control'
  ':(exclude)research/experiments/EX-ROS-2026-A021-grouped'
)

log() { printf '%s\n' "$*" >&2; }

fetch_branch() { # branch
  git -C "$REPO" fetch --quiet origin "$1:refs/a021-eval/$(basename "$1")"
}

ref_of() { printf 'refs/a021-eval/%s' "$(basename "$1")"; }

# Replaces arm-identifying strings on stdin; the neutral name is $1.
scrub() { # neutral-name
  sed -E \
    -e "s#experiment/a021-(control|grouped)#experiment/$1#g" \
    -e "s#EX-ROS-2026-A021-(control|grouped)#EX-ROS-2026-A021-$1#g" \
    -e "s#\\b(control|grouped)-0[0-9]\\b#$1-session#g" \
    -e "s#\\b(control|grouped) arm\\b#an arm#gI" \
    -e "s#https://claude\\.ai/code/session_[A-Za-z0-9]+#<session redacted>#g" \
    -e "/^Claude-Session:/d"
}

export_tree() { # ref neutral-name destination
  local ref="$1" name="$2" dest="$3"
  mkdir -p "$dest"
  git -C "$REPO" archive "$ref" | tar -x -C "$dest"
  rm -rf "$dest/research/experiments/EX-ROS-2026-A021-control" \
         "$dest/research/experiments/EX-ROS-2026-A021-grouped"
  # Scrub text files in place; JSON stays valid because only string contents change.
  grep -rlIE 'a021-(control|grouped)|EX-ROS-2026-A021-(control|grouped)|claude\.ai/code/session_|(control|grouped)-0[0-9]' "$dest" 2>/dev/null \
    | while IFS= read -r file; do
        scrub "$name" < "$file" > "$file.scrubbed" && mv "$file.scrubbed" "$file"
      done
}

export_patches() { # base ref neutral-name destination
  local base="$1" ref="$2" name="$3" dest="$4"
  mkdir -p "$dest"
  git -C "$REPO" format-patch --quiet --no-signature -o "$dest" "$base..$ref" -- . "${EXCLUDED_PATHSPECS[@]}"
  find "$dest" -name '*.patch' -print0 | while IFS= read -r -d '' file; do
    scrub "$name" < "$file" > "$file.scrubbed" && mv "$file.scrubbed" "$file"
  done
  git -C "$REPO" diff --stat "$base" "$ref" -- . "${EXCLUDED_PATHSPECS[@]}" | scrub "$name" > "$dest/../diffstat.txt"
  git -C "$REPO" log --reverse --format='%s' "$base..$ref" | scrub "$name" > "$dest/../commit-subjects.txt"
}

# Shared inputs as they were at the start commit: identical for both arms.
export_inputs() { # base destination
  local base="$1" dest="$2"
  mkdir -p "$dest"
  git -C "$REPO" show "$base:requirements/PLANNING-WORK-GROUPS.md" > "$dest/PLANNING-WORK-GROUPS.md"
  git -C "$REPO" show "$base:research/experiments/EX-ROS-2026-A021--grouped-versus-independent-execution.md" > "$dest/EX-ROS-2026-A021-protocol.md"
  git -C "$REPO" show "$base:.ros/work/queue.json" \
    | python3 -c 'import json,sys; items=[i for i in json.load(sys.stdin)["items"] if i["id"].startswith("PRAXIS-GROUP-")]; json.dump(items, sys.stdout, indent=2)' \
    > "$dest/work-items-PRAXIS-GROUP-01..05.json"
  git -C "$REPO" archive "$base" research/experiments/EX-ROS-2026-A021-baseline | tar -x -C "$dest"
  printf '%s\n' "$base" > "$dest/start-commit.txt"
}

main() {
  [[ -e "$OUT" ]] && { log "refusing to overwrite $OUT"; exit 2; }
  fetch_branch "$ARM_A_BRANCH"
  fetch_branch "$ARM_B_BRANCH"
  local ref_a ref_b base
  ref_a="$(ref_of "$ARM_A_BRANCH")"
  ref_b="$(ref_of "$ARM_B_BRANCH")"
  base="${BASE:-$(git -C "$REPO" merge-base "$ref_a" "$ref_b")}"

  # Random assignment: X is arm A or arm B with equal probability.
  local x_ref y_ref x_branch y_branch
  if (( $(od -An -N1 -tu1 /dev/urandom) % 2 )); then
    x_ref="$ref_a"; x_branch="$ARM_A_BRANCH"; y_ref="$ref_b"; y_branch="$ARM_B_BRANCH"
  else
    x_ref="$ref_b"; x_branch="$ARM_B_BRANCH"; y_ref="$ref_a"; y_branch="$ARM_A_BRANCH"
  fi

  export_inputs "$base" "$OUT/bundle/inputs"
  export_tree "$x_ref" arm-X "$OUT/bundle/arm-X/tree"
  export_tree "$y_ref" arm-Y "$OUT/bundle/arm-Y/tree"
  export_patches "$base" "$x_ref" arm-X "$OUT/bundle/arm-X/patches"
  export_patches "$base" "$y_ref" arm-Y "$OUT/bundle/arm-Y/patches"

  mkdir -p "$OUT/sealed" "$OUT/output"
  local salt mapping
  salt="$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
  mapping="$(printf '{"arm-X":"%s","arm-Y":"%s","armXCommit":"%s","armYCommit":"%s","startCommit":"%s","salt":"%s"}\n' \
    "$x_branch" "$y_branch" "$(git -C "$REPO" rev-parse "$x_ref")" "$(git -C "$REPO" rev-parse "$y_ref")" "$base" "$salt")"
  printf '%s' "$mapping" > "$OUT/sealed/mapping.json"
  chmod 400 "$OUT/sealed/mapping.json"

  # The evaluator works from the bundle only; drop the fetched refs.
  git -C "$REPO" update-ref -d "$x_ref"
  git -C "$REPO" update-ref -d "$y_ref"

  printf 'bundle:     %s\n' "$OUT/bundle"
  printf 'output dir: %s\n' "$OUT/output"
  printf 'mapping commitment (sha256 of sealed/mapping.json): %s\n' "$(sha256sum "$OUT/sealed/mapping.json" | cut -d' ' -f1)"
}

main "$@"
