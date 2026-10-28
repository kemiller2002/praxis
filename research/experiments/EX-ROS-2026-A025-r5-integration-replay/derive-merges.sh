#!/usr/bin/env bash
# Derives inputs/merges.tsv: every two-parent merge of R5 main up to the
# cutoff, the stream side and integration target (rules below), whether the
# stream side is on that stream's branch, and whether the merge conflicted
# (git merge-tree, recomputed). Read-only.
#   usage: derive-merges.sh R5_CLONE CUTOFF > inputs/merges.tsv
# Side rules, from the merge subject:
#   "... into r5/S ..."                          stream S = parent 1, target = parent 2
#   "Merge remote-tracking branch 'origin/r5/S' into HEAD"
#   "Merge r5/S ... into main ..."               stream S = parent 2, target = parent 1
#   6b1239e, 3858417 (subject names no branch; the body/report place them on
#   r5/shell)                                    stream shell = parent 1, target = parent 2
#   "Merge preflight/..."                        stream preflight (not an R5 stream)
set -euo pipefail
repo=$1 cutoff=$2
cd "$repo"
printf 'merge\tcommitted\tparent1\tparent2\tstream\tstreamParent\ttargetParent\trule\tstreamSideOnBranch\tconflicted\tconflictFiles\tsubject\n'
git rev-list --merges --reverse "$cutoff" | while read -r m; do
  p1=$(git rev-parse "$m^1"); p2=$(git rev-parse "$m^2")
  subject=$(git log -1 --format=%s "$m"); committed=$(git log -1 --format=%cI "$m")
  if [[ $subject =~ into\ r5/([a-z]+) ]]; then s=${BASH_REMATCH[1]}; sp=$p1; tp=$p2; rule=into-stream
  elif [[ $subject =~ ^Merge\ remote-tracking\ branch\ \'origin/r5/([a-z]+)\'\ into\ HEAD ]]; then s=${BASH_REMATCH[1]}; sp=$p2; tp=$p1; rule=stream-into-head
  elif [[ $subject =~ ^Merge\ r5/([a-z]+).*into\ main ]]; then s=${BASH_REMATCH[1]}; sp=$p2; tp=$p1; rule=stream-into-main
  elif [[ $(git rev-parse --short=7 "$m") == 6b1239e || $(git rev-parse --short=7 "$m") == 3858417 ]]; then s=shell; sp=$p1; tp=$p2; rule=shell-by-report
  elif [[ $subject =~ ^Merge\ preflight/ ]]; then s=preflight; sp=$p2; tp=$p1; rule=preflight
  else s=unknown; sp=$p2; tp=$p1; rule=unmatched; fi
  onbranch=n/a
  if git rev-parse --verify --quiet "refs/remotes/origin/r5/$s" >/dev/null; then
    if git merge-base --is-ancestor "$sp" "refs/remotes/origin/r5/$s"; then onbranch=yes; else onbranch=no; fi
  fi
  set +e; out=$(git merge-tree --write-tree --name-only "$p1" "$p2" 2>/dev/null); rc=$?; set -e
  if [ $rc -eq 1 ]; then conflicted=yes; files=$(echo "$out" | sed -n '2,/^$/p' | grep -v '^$' | paste -sd, -); else conflicted=no; files=; fi
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$m" "$committed" "$p1" "$p2" "$s" "$sp" "$tp" "$rule" "$onbranch" "$conflicted" "$files" "$subject"
done
