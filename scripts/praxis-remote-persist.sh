#!/usr/bin/env sh
# Persists the state a `praxis remote execute` response reports, for a
# CI/remote adapter (PRAXIS-REMOTE-06, DF-ROS-2026-A041 sections 5, 6, 10).
#
# The adapter decides nothing about Praxis state. This script:
#   - commits exactly the paths the response lists in persistence.paths
#     (defensively re-checked to be Praxis-owned .ros/ state), and nothing
#     else, so a workflow can never blindly commit a working tree;
#   - commits as the executor (the Git author of these bytes), with
#     trailers naming the request and the requester's *asserted* actor --
#     the executor never poses as the agent, and the agent is never
#     recorded as the author of bytes a runner wrote;
#   - pushes without force (mode push), so a ref that moved since checkout
#     is refused by Git itself, or opens a pull request (mode
#     pull-request) for protected branches;
#   - writes a machine-readable adapter result.
#
# Usage: praxis-remote-persist.sh --response FILE --output FILE [--mode push|pull-request]
# Exit: 0 persisted or nothing to persist; 1 persistence failed (see output);
#       2 invalid arguments or a response that is not safe to persist.
set -eu

RESPONSE=""
OUTPUT=""
MODE="push"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --response) RESPONSE="${2:-}"; shift 2 ;;
    --output) OUTPUT="${2:-}"; shift 2 ;;
    --mode) MODE="${2:-}"; shift 2 ;;
    *) echo "praxis-remote-persist: unknown argument '$1'" >&2; exit 2 ;;
  esac
done

[ -n "$RESPONSE" ] && [ -f "$RESPONSE" ] && [ -n "$OUTPUT" ] || { echo "praxis-remote-persist: --response FILE and --output FILE are required" >&2; exit 2; }
case "$MODE" in push|pull-request) ;; *) echo "praxis-remote-persist: --mode must be push or pull-request" >&2; exit 2 ;; esac

# Everything this script needs from Praxis's own documents, read as JSON.
# Values that reach a commit message were validated by Praxis as tokens
# (no whitespace or control characters); they are re-checked here anyway.
FACTS="$(python3 - "$RESPONSE" <<'PY'
import json, re, sys
response = json.load(open(sys.argv[1], encoding="utf-8"))
token = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:/@+~-]{0,200}$")
def safe(value, fallback="unknown"):
    return value if isinstance(value, str) and token.match(value) else fallback
paths = (response.get("persistence") or {}).get("paths") or []
# Praxis lists only state it kept: a success's, or the accepted constituents
# of a batch that stopped part-way. Anything it refused was already undone.
if not paths:
    print("nothing")
    sys.exit(0)
for path in paths:
    if not isinstance(path, str) or not path.startswith(".ros/") or ".." in path.split("/") or "\n" in path:
        sys.exit(f"refusing to persist a path that is not Praxis-owned state: {path!r}")
journal = next((p for p in paths if p.startswith(".ros/remote/requests/")), None)
requester = "unknown:unknown"
if journal:
    try:
        actor = (json.load(open(journal, encoding="utf-8")).get("requester") or {}).get("actor") or {}
        requester = f"{safe(actor.get('kind'))}:{safe(actor.get('id'))}"
    except (OSError, ValueError):
        pass
executor = response.get("executor") or {}
print("persist")
print(safe(response.get("requestId")))
print(safe(response.get("operation")))
print(requester)
print(safe(executor.get("kind")) + " run " + safe(executor.get("runId"), "-") + " attempt " + safe(executor.get("runAttempt"), "-"))
print(safe(response.get("praxisVersion")))
print("\t".join(paths))
PY
)" || exit 2

result() {
  # $1 persisted (true|false) $2 commit $3 branch $4 pr $5 failure-code $6 retry $7 message
  python3 - "$OUTPUT" "$MODE" "$@" <<'PY'
import json, sys
output, mode, persisted, commit, branch, pr, code, retry, message = sys.argv[1:10]
none = lambda value: value or None
json.dump({
    "schema": "praxis.remote-adapter-result",
    "schemaVersion": 1,
    "mode": mode,
    "persisted": persisted == "true",
    "commit": none(commit),
    "branch": none(branch),
    "pullRequest": none(pr),
    "failure": {"code": code, "decidedBy": "executor", "retry": retry, "message": message} if code else None,
}, open(output, "w", encoding="utf-8"), indent=2)
PY
}

if [ "$(printf '%s\n' "$FACTS" | sed -n 1p)" = "nothing" ]; then
  result false "" "" "" "" "" ""
  exit 0
fi

REQUEST_ID="$(printf '%s\n' "$FACTS" | sed -n 2p)"
OPERATION="$(printf '%s\n' "$FACTS" | sed -n 3p)"
REQUESTER="$(printf '%s\n' "$FACTS" | sed -n 4p)"
EXECUTOR="$(printf '%s\n' "$FACTS" | sed -n 5p)"
VERSION="$(printf '%s\n' "$FACTS" | sed -n 6p)"
PATHS="$(printf '%s\n' "$FACTS" | sed -n 7p)"

TARGET_BRANCH="$(git symbolic-ref --quiet --short HEAD)" || { result false "" "" "" repository-write-failed same-request "the executor is not on a branch"; exit 1; }

# Stage exactly the reported paths (additions, modifications, deletions)...
old_ifs="$IFS"; IFS="$(printf '\t')"
# shellcheck disable=SC2086
set -- $PATHS
IFS="$old_ifs"
git add -A -- "$@"

# ...and refuse if anything else is staged.
unexpected="$(git diff --cached --name-only | while IFS= read -r staged; do
  keep=no
  for reported in "$@"; do [ "$staged" = "$reported" ] && keep=yes; done
  [ "$keep" = yes ] || printf '%s\n' "$staged"
done)"
[ -z "$unexpected" ] || { echo "praxis-remote-persist: refusing to commit unreported paths: $unexpected" >&2; exit 2; }

git -c user.name="${PRAXIS_COMMIT_NAME:-github-actions[bot]}" \
    -c user.email="${PRAXIS_COMMIT_EMAIL:-41898282+github-actions[bot]@users.noreply.github.com}" \
    commit -q -m "praxis: remote $OPERATION ($REQUEST_ID)" -m "Praxis-Request-Id: $REQUEST_ID
Praxis-Operation: $OPERATION
Praxis-Requester: $REQUESTER (asserted by the request)
Praxis-Executor: $EXECUTOR
Praxis-Version: $VERSION"
COMMIT="$(git rev-parse HEAD)"

push_failure() {
  # A ref that moved is a concurrency conflict (nothing was persisted; form a
  # new request); anything else is a write failure the same request can retry.
  if printf '%s' "$1" | grep -Eq 'non-fast-forward|fetch first|\[rejected\]|stale info'; then
    result false "" "$2" "" concurrency-conflict after-refresh "the ref moved before the state could be pushed; nothing was persisted"
  else
    result false "" "$2" "" repository-write-failed same-request "the push failed; nothing was persisted"
  fi
  exit 1
}

if [ "$MODE" = "push" ]; then
  if ! err="$(git push --porcelain origin "HEAD:refs/heads/$TARGET_BRANCH" 2>&1)"; then push_failure "$err" "$TARGET_BRANCH"; fi
  result true "$COMMIT" "$TARGET_BRANCH" "" "" "" ""
else
  # Request IDs may contain characters Git forbids in ref names (':' or
  # '..'), so the branch is named by a digest of the ID, which is always a
  # valid, deterministic ref; the ID itself is in the title and trailers.
  BRANCH="praxis/remote/$(printf '%s' "$REQUEST_ID" | python3 -c 'import hashlib,sys; print(hashlib.sha256(sys.stdin.buffer.read()).hexdigest()[:24])')"
  if ! err="$(git push --porcelain origin "HEAD:refs/heads/$BRANCH" 2>&1)"; then push_failure "$err" "$BRANCH"; fi
  PR="$(gh pr create --base "$TARGET_BRANCH" --head "$BRANCH" --title "praxis: remote $OPERATION ($REQUEST_ID)" \
        --body "Praxis state for remote request \`$REQUEST_ID\`. Requester: $REQUESTER (asserted by the request). Merge to persist; the journal entry makes the outcome recoverable.")" ||
    { result false "$COMMIT" "$BRANCH" "" repository-write-failed same-request "the state branch was pushed but the pull request could not be opened"; exit 1; }
  # Pending merge: persisted to a branch, not yet to the target ref.
  result false "$COMMIT" "$BRANCH" "$PR" "" "" ""
fi
