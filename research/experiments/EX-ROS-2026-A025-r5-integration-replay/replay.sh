#!/usr/bin/env bash
# Replays R5's merges through `praxis streams check`, one run per merge and
# contract variant, BEFORE each merge: the stream side's parent is the
# stream ref, the other parent is --against. Approvals (inputs/approvals.json)
# count for a merge only when their commit is in the history of one of its
# parents, i.e. had been recorded before the merge. Read-only on R5.
#   usage: replay.sh R5_CLONE PRAXIS_DLL OUT_DIR
# Variants: primary (inputs/contract.base.json as derived from documents);
# s1 = primary + commonPaths for documentation and records (an assumption no
# R5 document states; reported as a sensitivity check only).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$1 dll=$2 out=$3
mkdir -p "$out"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
contract() { # variant stream streamParent targetParent
  local variant=$1 stream=$2 sp=$3 tp=$4 approvals='[]'
  while read -r entry; do
    c=$(jq -r .commit <<<"$entry")
    if git -C "$repo" merge-base --is-ancestor "$c" "$sp" || git -C "$repo" merge-base --is-ancestor "$c" "$tp"; then
      approvals=$(jq -c --argjson e "$entry" '. + [{stream: $e.stream, paths: $e.paths, approvedBy: $e.approvedBy}]' <<<"$approvals")
    fi
  done < <(jq -c '.[]' "$here/inputs/approvals.json")
  jq --arg variant "$variant" --arg stream "$stream" --arg ref "$sp" --argjson approvals "$approvals" '
    (if $variant == "s1" then .commonPaths = ["docs/**", "research/**", "evidence/**", "*.md"] else . end)
    | (if ([.streams[].id] | index($stream)) == null
       then .streams += [{id: $stream, workItem: "WI-0001", ref: $ref, owns: [], readiness: ["overlap"]}]
       else .streams |= map(if .id == $stream then .ref = $ref else . end) end)
    | .interfaces |= map(if .id == "shell-files" then .approvals = $approvals else . end)' \
    "$here/inputs/contract.base.json"
}
run() { # label variant stream streamParent targetParent -> one JSON line
  local label=$1 variant=$2 stream=$3 sp=$4 tp=$5
  contract "$variant" "$stream" "$sp" "$tp" > "$work/contract.json"
  set +e
  report=$(dotnet "$dll" --root "$repo" streams check --contract "$work/contract.json" --against "$tp" --stream "$stream" --json)
  rc=$?
  set -e
  jq -c --arg label "$label" --arg variant "$variant" --arg stream "$stream" --arg sp "$sp" --arg tp "$tp" --argjson exit "$rc" \
    '{label: $label, variant: $variant, stream: $stream, streamParent: $sp, targetParent: $tp, exit: $exit, report: .}' <<<"$report"
}
for variant in primary s1; do
  tail -n +2 "$here/inputs/merges.tsv" | while IFS=$'\t' read -r m _ _ _ s sp tp _; do
    run "$m" "$variant" "$s" "$sp" "$tp"
  done > "$out/merge-checks.$variant.jsonl"
done
