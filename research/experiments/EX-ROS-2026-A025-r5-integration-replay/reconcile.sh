#!/usr/bin/env bash
# For each "Praxis: reconcile" commit of R5 main up to the cutoff, asks
# whether the contract would have attributed the commits it reconciled by
# itself. Each reconciled commit C is checked with every stream's ref set to
# C and --against C^1, so each stream's pending change is exactly C's diff;
# approvals count when their commit is in C's history. A commit is
# attributable to a stream when that stream has no finding and at least one
# path it owns, owns as interface owner, or was approved for (common and
# managed paths alone attribute nothing).
#   usage: reconcile.sh R5_CLONE CUTOFF PRAXIS_DLL VARIANT > outputs/reconcile.VARIANT.jsonl
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
repo=$1 cutoff=$2 dll=$3 variant=$4
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
declare -A streamOf=([WI-0003]=core [WI-0004]=shell [WI-0005]=persist [WI-0006]=acc)
contract() { # commit
  local c=$1 approvals='[]'
  while read -r entry; do
    if git -C "$repo" merge-base --is-ancestor "$(jq -r .commit <<<"$entry")" "$c"; then
      approvals=$(jq -c --argjson e "$entry" '. + [{stream: $e.stream, paths: $e.paths, approvedBy: $e.approvedBy}]' <<<"$approvals")
    fi
  done < <(jq -c '.[]' "$here/inputs/approvals.json")
  jq --arg variant "$variant" --arg ref "$c" --argjson approvals "$approvals" '
    (if $variant == "s1" then .commonPaths = ["docs/**", "research/**", "evidence/**", "*.md"] else . end)
    | .streams |= map(.ref = $ref)
    | .interfaces |= map(if .id == "shell-files" then .approvals = $approvals else . end)' "$here/inputs/contract.base.json"
}
git -C "$repo" log --reverse --format='%H %s' "$cutoff" | grep ' Praxis: reconcile' | while read -r rc subject; do
  git -C "$repo" diff "$rc^" "$rc" -- .ros/events/events.jsonl | grep '^+{' | sed 's/^+//' \
    | jq -c 'select(.type == "work.attribution.reconciled") | {workItem, commits: [.gitEvidence.commits[].sha]}' \
    | while read -r event; do
        wi=$(jq -r .workItem <<<"$event")
        for c in $(jq -r '.commits[]' <<<"$event"); do
          contract "$c" > "$work/contract.json"
          set +e
          report=$(dotnet "$dll" --root "$repo" streams check --contract "$work/contract.json" --against "$c^1" --json)
          set -e
          jq -c --arg rc "$rc" --arg wi "$wi" --arg expected "${streamOf[$wi]:-}" --arg c "$c" --arg variant "$variant" '
            [.streams[] | select((.findings | length) == 0
               and ([.covered[] | select(.basis == "owned" or (.basis | startswith("interface-owner:")) or (.basis | startswith("approved:")))] | length) > 0)
             | .id] as $attributable
            | {reconcileCommit: $rc, variant: $variant, workItem: $wi, expectedStream: (if $expected == "" then null else $expected end), commit: $c,
               attributable: $attributable,
               outcome: (if $expected == "" then "no-stream"
                         elif $attributable == [$expected] then "unique-match"
                         elif ($attributable | index($expected)) != null then "ambiguous"
                         elif ($attributable | length) > 0 then "mismatch"
                         else "none" end),
               findings: ([.streams[] | select(.id == $expected) | .findings[] | "\(.code) \(.path)"])}' <<<"$report"
        done
      done
done
