#!/usr/bin/env bash
# Relays praxis.remote requests committed to an inbox branch to
# praxis-remote.yml (DF-ROS-2026-A045, RQ-ROS-2026-A023).
#
#   scripts/praxis-remote-inbox.sh --before SHA --workspace DIR --default-ref REF
#
# The changed files come from Git itself, not from the push event's commit
# list, which omits files when a push creates the branch: `before..HEAD`, or
# when the branch is new (a zero or unknown `before`), the files changed since
# its merge base with the default branch.
#
# For every `.praxis-inbox/*.json` a push added or modified, this checks only
# what routing needs:
#   - the document is JSON with protocol "praxis.remote";
#   - it has a valid requestId;
#   - it has a routable repository.ref (refs/heads/NAME, and not an inbox
#     branch). A request with no ref is a read and goes to the default branch.
# It then dispatches praxis-remote.yml on that branch with the file's exact
# bytes. Praxis alone classifies, authorizes, binds, executes and journals
# the request; this script never interprets or alters it, and never supplies
# an actor.
#
# The dispatch command defaults to `gh workflow run`. Tests replace it with
# PRAXIS_INBOX_DISPATCH (a program called with the same arguments). Exits 1
# if any file was refused; valid files are still dispatched.
set -euo pipefail

usage="usage: $0 --before SHA --workspace DIR --default-ref refs/heads/NAME"
before="" workspace="" default_ref=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --before) before="${2:-}"; shift 2 ;;
    --workspace) workspace="${2:-}"; shift 2 ;;
    --default-ref) default_ref="${2:-}"; shift 2 ;;
    *) echo "$usage" >&2; exit 2 ;;
  esac
done
[ -d "$workspace" ] && [ -n "$default_ref" ] || { echo "$usage" >&2; exit 2; }

# The inbox files this push added or modified, one per line.
changed_inbox_files() {
  local base=""
  if [ -n "$before" ] && ! [[ "$before" =~ ^0+$ ]] && git -C "$workspace" cat-file -e "${before}^{commit}" 2>/dev/null; then
    base="$before"
  else
    base="$(git -C "$workspace" merge-base HEAD "origin/${default_ref#refs/heads/}" 2>/dev/null || true)"
  fi
  [ -n "$base" ] || { echo "::error::cannot determine what this push changed (no usable before commit or merge base)" >&2; return 1; }
  git -C "$workspace" diff --name-only --diff-filter=AM "$base" HEAD -- .praxis-inbox/
}

summary="${GITHUB_STEP_SUMMARY:-/dev/null}"

# Routing decisions, one per line, fields separated by the ASCII unit separator
# (0x1f; tab would collapse empty fields): status, path, requestId, branch, reason.
# Pure with respect to its inputs: reads the event and the files, writes stdout.
decide() {
  changed_inbox_files | python3 -c '
import json, os, re, sys

workspace, default_ref = sys.argv[1:3]
# A safe filename alphabet: paths reach workflow-command annotations.
inbox = re.compile(r"^\.praxis-inbox/[A-Za-z0-9._:-]+\.json$")
request_id = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$")
branch_ref = re.compile(r"^refs/heads/(?!praxis-inbox/)([A-Za-z0-9._/-]+)$")

paths = sorted({line for line in sys.stdin.read().splitlines() if inbox.match(line)})

def decide(path):
    full = os.path.join(workspace, path)
    if not os.path.isfile(full):
        return ("skipped", path, "", "", "no longer present at the pushed commit")
    try:
        doc = json.load(open(full, encoding="utf-8"))
    except (ValueError, UnicodeDecodeError) as error:
        return ("refused", path, "", "", f"not valid JSON: {error}")
    if not isinstance(doc, dict) or doc.get("protocol") != "praxis.remote":
        return ("refused", path, "", "", "not a praxis.remote request (protocol must be \"praxis.remote\")")
    rid = doc.get("requestId")
    if not isinstance(rid, str) or not request_id.match(rid):
        return ("refused", path, "", "", "requestId must be 8-128 characters of [A-Za-z0-9._:-], starting with a letter or digit")
    repo = doc.get("repository") or {}
    ref = repo.get("ref") if isinstance(repo, dict) else None
    ref = ref if ref is not None else default_ref
    match = branch_ref.match(ref) if isinstance(ref, str) else None
    if not match or ".." in ref:
        return ("refused", path, rid, "", "repository.ref must be refs/heads/NAME of a branch that is not an inbox branch")
    return ("dispatch", path, rid, match.group(1), "")

for row in map(decide, paths):
    print("\x1f".join(row))
' "$workspace" "$default_ref"
}

dispatch() {
  local file="$1" rid="$2" branch="$3"
  if [ -n "${PRAXIS_INBOX_DISPATCH:-}" ]; then
    "$PRAXIS_INBOX_DISPATCH" workflow run praxis-remote.yml --ref "$branch" -f request_id="$rid" -F request=@"$file"
  else
    gh workflow run praxis-remote.yml --ref "$branch" -f request_id="$rid" -F request=@"$file"
  fi
}

decisions="$(set -o pipefail; decide)"
refused=0

{
  echo "### praxis-remote inbox"
  echo ""
  echo "Inbox branch \`${GITHUB_REF_NAME:-unknown}\` at \`${GITHUB_SHA:-unknown}\`, pushed by \`${GITHUB_ACTOR:-unknown}\`."
  echo "Each request below is relayed unchanged; Praxis records the requester the request itself asserts."
  echo ""
} >> "$summary"

if [ -z "$decisions" ]; then
  echo "no .praxis-inbox/*.json was added or modified by this push"
  echo "- No inbox request in this push." >> "$summary"
  exit 0
fi

while IFS=$'\x1f' read -r status path rid branch reason; do
  case "$status" in
    dispatch)
      if dispatch "$workspace/$path" "$rid" "$branch"; then
        echo "dispatched $path as $rid on $branch"
        echo "- \`$path\`: dispatched \`praxis remote $rid\` on \`$branch\`." >> "$summary"
      else
        echo "::error file=$path::dispatch of $rid on $branch failed"
        echo "- \`$path\`: dispatch of \`$rid\` on \`$branch\` failed." >> "$summary"
        refused=1
      fi ;;
    skipped)
      echo "skipped $path: $reason"
      echo "- \`$path\`: skipped, $reason." >> "$summary" ;;
    *)
      echo "::error file=$path::$reason"
      echo "- \`$path\`: refused, $reason. Nothing was dispatched." >> "$summary"
      refused=1 ;;
  esac
done <<< "$decisions"

exit "$refused"
