#!/usr/bin/env bash
# Computes the replay metrics from outputs/merge-checks.<variant>.jsonl and
# inputs/merges.tsv. Definitions (fixed before the outputs were inspected):
#   overlap flag      the stream side's targetOverlap (unmanaged paths both
#                     parents changed since their merge base) is non-empty
#   covers conflict   every file merge-tree reported in conflict is in it
#   ownership flag    the stream side has >= 1 finding (outside-ownership,
#                     interface-unapproved, approval-decision-not-found);
#                     not counted for the preflight merge (not an R5 stream)
#   narrow flag       overlap on a path the stream side does not plainly own
#                     (any basis other than "owned" or "interface-owner:*",
#                     or a finding)
#   false positive    a flag on a merge whose parents merge cleanly
#   usage: analyze.sh VARIANT  -> outputs/summary.VARIANT.json, outputs/merges.VARIANT.tsv,
#          outputs/reconcile-summary.VARIANT.json
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
variant=$1
jq -n --slurpfile checks "$here/outputs/merge-checks.$variant.jsonl" --rawfile merges "$here/inputs/merges.tsv" '
  ($merges | split("\n") | .[1:] | map(select(length > 0) | split("\t")
     | {merge: .[0], stream: .[4], rule: .[7], conflicted: (.[9] == "yes"), conflictFiles: (.[10] | if . == "" then [] else split(",") end), subject: .[11]})) as $rows
  | ($checks | map({key: .label, value: .}) | from_entries) as $byMerge
  | [ $rows[] | . as $row | $byMerge[$row.merge] as $check | $check.report.streams[0] as $s
      | ($s.targetOverlap) as $overlap
      | ([$s.covered[] | select(.basis == "owned" or (.basis | startswith("interface-owner:"))) | .path]) as $plain
      | {merge: $row.merge[0:7], stream: $row.stream, rule: $row.rule, conflicted: $row.conflicted,
         conflictFiles: $row.conflictFiles,
         overlap: $overlap,
         overlapFlag: ($overlap | length > 0),
         coversConflict: ($row.conflicted and ($row.conflictFiles - $overlap | length == 0)),
         narrowOverlap: [$overlap[] | select(. as $p | $plain | index($p) | not)],
         findings: (if $row.stream == "preflight" then [] else $s.findings end),
         readiness: $s.readiness,
         exit: $check.exit} ] as $per
  | def count(f): [ $per[] | select(f) ] | length;
    { variant: "'"$variant"'",
      merges: ($per | length),
      conflicted: count(.conflicted),
      overlapFlag: {conflicting: count(.conflicted and .overlapFlag), nonConflicting: count((.conflicted | not) and .overlapFlag)},
      coversConflictFiles: count(.coversConflict),
      narrowFlag: {conflicting: count(.conflicted and (.narrowOverlap | length > 0)), nonConflicting: count((.conflicted | not) and (.narrowOverlap | length > 0))},
      ownershipFlag: {conflicting: count(.conflicted and (.findings | length > 0)), nonConflicting: count((.conflicted | not) and (.findings | length > 0))},
      findings: {total: ([$per[].findings[]] | length),
                 byCode: ([$per[].findings[] | .code] | group_by(.) | map({key: .[0], value: length}) | from_entries),
                 distinctStreamPath: ([$per[] | .stream as $st | .findings[] | "\($st) \(.path)"] | unique | length),
                 distinctStreamPathByCode: ([$per[] | .stream as $st | .findings[] | "\(.code) \($st) \(.path)"] | unique | map(split(" ")[0]) | group_by(.) | map({key: .[0], value: length}) | from_entries)},
      readiness: ([$per[].readiness] | group_by(.) | map({key: .[0], value: length}) | from_entries),
      perMerge: $per }' > "$here/outputs/summary.$variant.json"
jq -r '.perMerge[] | [.merge, .stream, .rule, (if .conflicted then "yes" else "no" end), (.conflictFiles | join(",")), (.overlap | length), (if .coversConflict then "yes" else "-" end), (.narrowOverlap | length), (.findings | length), .readiness] | @tsv' "$here/outputs/summary.$variant.json" \
  | (printf 'merge\tstream\trule\tconflicted\tconflictFiles\toverlapPaths\tcoversConflict\tnarrowOverlapPaths\tfindings\treadiness\n'; cat) > "$here/outputs/merges.$variant.tsv"
# Reconcile attribution (when outputs/reconcile.VARIANT.jsonl exists): a
# reconcile commit is auto-attributable when every commit it reconciled is a
# unique match for the stream bound to its work item.
if [ -f "$here/outputs/reconcile.$variant.jsonl" ]; then
  jq -s --arg v "$variant" '{variant: $v, reconcileCommits: (map(.reconcileCommit) | unique | length), reconciledCommitRows: length,
      distinctCommits: (map(.commit) | unique | length), outcomes: (group_by(.outcome) | map({key: .[0].outcome, value: length}) | from_entries),
      autoAttributableReconcileCommits: (group_by(.reconcileCommit) | map(select(all(.outcome == "unique-match"))) | length),
      perReconcile: (group_by(.reconcileCommit) | map({commit: .[0].reconcileCommit[0:7], rows: length, unique: (map(select(.outcome == "unique-match")) | length), outcomes: (map(.outcome) | unique)}))}' \
    "$here/outputs/reconcile.$variant.jsonl" > "$here/outputs/reconcile-summary.$variant.json"
fi
