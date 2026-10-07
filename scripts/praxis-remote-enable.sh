#!/usr/bin/env bash
# Takes Praxis remote execution live (GH-90, PRAXIS-REMOTE-11 prerequisites):
#
#   1. release  bump release.json, push to main, wait for the native release
#               workflow, and verify the release's checksum and build-provenance
#               attestation;
#   2. enable   pin that release in .echelon/toolchain.json and opt the
#               repository in with ros.json remote.capabilities;
#   3. smoke    dispatch a read-only praxis.describe request through
#               .github/workflows/praxis-remote.yml and print the structured
#               result.
#
# Every repository change is made under its own Praxis work item (begun,
# completed, validated) so the commits are attributed like any other work.
# Run it from a clean checkout of main of the repository to enable (for the
# release phase, that repository must be kemiller2002/praxis itself).
#
# Requirements: bash, git, gh (authenticated, with push access), the .NET
# SDK (for scripts/praxis-tooling.fsx; RQ-ROS-2026-A024), GNU date,
# and a Praxis CLI (`praxis`, or `./praxis` in a source
# checkout; override with PRAXIS=...).
#
# Usage:
#   scripts/praxis-remote-enable.sh --version 3.5.0 [options]
#
# Options:
#   --version X.Y.Z         Release version to publish and pin (required).
#   --capabilities LIST     Comma-separated remote capabilities to enable
#                           (default: read,mutate). Classes: read, mutate,
#                           complete, reconcile.
#   --skip-release          Pin an already published release instead of
#                           publishing one.
#   --skip-smoke            Do not dispatch the praxis.describe smoke request.
#   --via-pr                Land each change through a pull request (merged
#                           once its checks pass) instead of pushing to main.
#   --dry-run               Print what would happen; change nothing.
#   -h, --help              Show this help.
set -euo pipefail

VERSION=""
CAPABILITIES="read,mutate"
SKIP_RELEASE=false
SKIP_SMOKE=false
VIA_PR=false
DRY_RUN=false
REPOSITORY_SLUG="kemiller2002/praxis"

usage() { sed -n '2,/^set -euo pipefail$/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//'; }
die() { echo "praxis-remote-enable: $*" >&2; exit 1; }
step() { printf '\n==> %s\n' "$*"; }
run() {
  if $DRY_RUN; then printf '  [dry-run] %s\n' "$*"; else "$@"; fi
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --version) VERSION="${2:-}"; shift 2 ;;
    --capabilities) CAPABILITIES="${2:-}"; shift 2 ;;
    --skip-release) SKIP_RELEASE=true; shift ;;
    --skip-smoke) SKIP_SMOKE=true; shift ;;
    --via-pr) VIA_PR=true; shift ;;
    --dry-run) DRY_RUN=true; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown argument '$1'" ;;
  esac
done

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "--version must be an exact MAJOR.MINOR.PATCH"
for capability in ${CAPABILITIES//,/ }; do
  case "$capability" in read|mutate|complete|reconcile) ;; *) die "unknown capability '$capability'" ;; esac
done

# --- preflight ---------------------------------------------------------------

step "Preflight"
for tool in git gh dotnet date; do command -v "$tool" >/dev/null || die "$tool is required"; done
gh auth status >/dev/null 2>&1 || die "gh is not authenticated (run: gh auth login)"
ROOT="$(git rev-parse --show-toplevel)" || die "run this inside the repository to enable"
cd "$ROOT"
[ -z "$(git status --porcelain)" ] || die "the working tree is not clean"
[ "$(git symbolic-ref --quiet --short HEAD)" = "main" ] || die "check out main first"
git fetch -q origin main
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || die "main is not up to date with origin/main (git pull first)"
[ -f .github/workflows/praxis-remote.yml ] || die "this repository does not have .github/workflows/praxis-remote.yml; copy the adapter first (docs/remote-execution-operations.md)"
TARGET_SLUG="$(gh repo view --json nameWithOwner -q .nameWithOwner)"

if [ -n "${PRAXIS:-}" ]; then read -r -a PRAXIS_CMD <<<"$PRAXIS"
elif command -v praxis >/dev/null; then PRAXIS_CMD=(praxis)
elif [ -x ./praxis ]; then PRAXIS_CMD=(./praxis)
# A checkout from before the rename has only the compatibility launcher.
elif [ -x ./ros ]; then PRAXIS_CMD=(./ros)
else die "no Praxis CLI found (install praxis, or set PRAXIS=...)"; fi
praxis_cli() { "${PRAXIS_CMD[@]}" "$@"; }

# The person running this script is the actor on its work items: declared,
# never guessed. An explicit PRAXIS_ACTOR/PRAXIS_ACTOR_KIND (or the legacy
# ROS_ACTOR/ROS_ACTOR_KIND) wins.
export PRAXIS_ACTOR_KIND="${PRAXIS_ACTOR_KIND:-${ROS_ACTOR_KIND:-human}}"
export PRAXIS_ACTOR="${PRAXIS_ACTOR:-${ROS_ACTOR:-$(gh api user -q .login)}}"
# Compatibility: Praxis releases from before the rename read only the legacy
# names (DF-ROS-2026-A050).
export ROS_ACTOR_KIND="$PRAXIS_ACTOR_KIND" ROS_ACTOR="$PRAXIS_ACTOR"
echo "repository: $TARGET_SLUG   actor: $PRAXIS_ACTOR_KIND:$PRAXIS_ACTOR   praxis: ${PRAXIS_CMD[*]}   dry-run: $DRY_RUN"

# Millisecond precision: a whole-second timestamp can predate the execution
# that `work start` opened moments earlier, which validation rejects.
now() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }

# Repository JSON and version arithmetic are F# (RQ-ROS-2026-A024).
TOOLING="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)/praxis-tooling.fsx"
tooling() { dotnet fsi "$TOOLING" "$@"; }

# Begins a mechanical work item (no completion evidence required) so every
# change below is attributed; `finish_item` completes it and validates.
begin_item() {
  local id="$1" title="$2"
  run praxis_cli add "$title" --id "$id" --priority high --source manual --source-reference "https://github.com/$REPOSITORY_SLUG/issues/90"
  run praxis_cli work backlog-transition --action ready --id "$id" --occurred-at "$(now)"
  run praxis_cli work start --id "$id" --type mechanical --occurred-at "$(now)"
}
finish_item() {
  local id="$1"
  run praxis_cli work complete --id "$id" --occurred-at "$(now)"
  run praxis_cli registry build
  run praxis_cli validate
}

# Commits everything under one message and lands it on main, directly or via
# a pull request that is merged once its checks pass.
land() {
  local message="$1" branch="$2"
  run git add -A
  run git commit -q -m "$message"
  if $VIA_PR; then
    run git push -q origin "HEAD:refs/heads/$branch"
    run gh pr create --base main --head "$branch" --title "$message" --body "Automated by scripts/praxis-remote-enable.sh (GH-90)."
    run gh pr checks "$branch" --watch --fail-fast
    run gh pr merge "$branch" --merge --delete-branch
    run git fetch -q origin main
    run git reset -q --hard origin/main
  else
    run git push -q origin HEAD:main
  fi
}

# --- 1. release --------------------------------------------------------------

if ! $SKIP_RELEASE; then
  step "Release Praxis $VERSION"
  [ "$TARGET_SLUG" = "$REPOSITORY_SLUG" ] || die "the release phase runs in $REPOSITORY_SLUG; use --skip-release elsewhere"
  CURRENT="$(tooling json-get release.json version)"
  tooling semver-newer "$VERSION" "$CURRENT" || die "--version must be newer than the current $CURRENT"
  if gh release view "v$VERSION" --repo "$REPOSITORY_SLUG" >/dev/null 2>&1; then
    die "release v$VERSION already exists; pin it with --skip-release"
  fi

  ITEM="RELEASE-${VERSION//./-}"
  begin_item "$ITEM" "Release Praxis $VERSION with remote execution (GH-90)"
  run tooling json-set release.json version "$VERSION"
  finish_item "$ITEM"
  land "$ITEM: release Praxis $VERSION" "release/$VERSION"

  step "Wait for the native release workflow"
  if ! $DRY_RUN; then
    RELEASE_SHA="$(git rev-parse HEAD)"
    RUN_ID=""
    for _ in $(seq 1 60); do
      RUN_ID="$(gh run list --repo "$REPOSITORY_SLUG" --workflow native-release.yml --commit "$RELEASE_SHA" --json databaseId -q '.[0].databaseId' 2>/dev/null || true)"
      [ -n "$RUN_ID" ] && break
      sleep 10
    done
    [ -n "$RUN_ID" ] || die "no native-release run started for $RELEASE_SHA"
    gh run watch "$RUN_ID" --repo "$REPOSITORY_SLUG" --exit-status || die "the native release workflow failed (run $RUN_ID)"
  fi
fi

# --- verify -----------------------------------------------------------------
# The pin only ever advances to a release whose immutable assets exist and
# whose checksum and build-provenance attestation verify (PRX-QUAL-010),
# whether it was released above or already published (--skip-release).

step "Verify the published release v$VERSION"
if ! $DRY_RUN; then
  VERIFY="$(mktemp -d)"
  trap 'rm -rf "$VERIFY"' EXIT
  gh release download "v$VERSION" --repo "$REPOSITORY_SLUG" --dir "$VERIFY" --pattern 'praxis-linux-x64.tar.gz' --pattern 'native-checksums.txt'
  (cd "$VERIFY" && grep ' praxis-linux-x64.tar.gz$' native-checksums.txt | sha256sum -c -) || die "checksum verification failed"
  gh attestation verify "$VERIFY/praxis-linux-x64.tar.gz" --repo "$REPOSITORY_SLUG" || die "attestation verification failed"
  tar -xzf "$VERIFY/praxis-linux-x64.tar.gz" -C "$VERIFY"
  "$VERIFY/praxis-linux-x64/praxis" --help 2>&1 | grep -q 'remote execute' || die "v$VERSION does not contain 'remote execute'"
  # The release's declared state compatibility, as published at its tag.
  gh api "repos/$REPOSITORY_SLUG/contents/release.json?ref=v$VERSION" -H 'Accept: application/vnd.github.raw' > "$VERIFY/release.json" \
    || die "release.json of v$VERSION could not be read"
  echo "v$VERSION: checksum, attestation and remote commands verified"
fi

# --- 2. enable ---------------------------------------------------------------

step "Pin Praxis $VERSION and enable remote capabilities: $CAPABILITIES"
ITEM="REMOTE-ENABLE-${VERSION//./-}"
begin_item "$ITEM" "Pin Praxis $VERSION and enable remote execution ($CAPABILITIES) (GH-90)"
if ! $DRY_RUN; then
  tooling remote-enable "$VERSION" "$CAPABILITIES"
  # The self-hosting repository records the pinned release's declared state
  # compatibility; the premerge fence checks the pin against it (PRX-QUAL-010).
  if [ -f quality/release-compatibility.json ]; then
    tooling record-compatibility "$VERSION" "$VERIFY/release.json" || die "v$VERSION declares no state compatibility; it cannot be pinned here"
  fi
else
  echo "  [dry-run] set .echelon/toolchain.json praxis=$VERSION and ros.json remote.capabilities=[$CAPABILITIES]"
fi
finish_item "$ITEM"
land "$ITEM: pin Praxis $VERSION and enable remote execution" "remote-enable/$VERSION"

# --- 3. smoke ----------------------------------------------------------------

if ! $SKIP_SMOKE; then
  step "Dispatch a praxis.describe request"
  REQUEST_ID="req-describe-$(date -u +%Y%m%dT%H%M%SZ)"
  REQUEST="$(tooling remote-describe-request "$REQUEST_ID")"
  run gh workflow run praxis-remote.yml --repo "$TARGET_SLUG" --ref main -f request_id="$REQUEST_ID" -f request="$REQUEST"
  if ! $DRY_RUN; then
    RUN_ID=""
    for _ in $(seq 1 60); do
      RUN_ID="$(gh run list --repo "$TARGET_SLUG" --workflow praxis-remote.yml --json databaseId,displayTitle \
        -q ".[] | select(.displayTitle == \"praxis remote $REQUEST_ID\") | .databaseId" 2>/dev/null | head -n 1 || true)"
      [ -n "$RUN_ID" ] && break
      sleep 5
    done
    [ -n "$RUN_ID" ] || die "the praxis-remote run for $REQUEST_ID did not appear"
    gh run watch "$RUN_ID" --repo "$TARGET_SLUG" --exit-status || echo "warning: the run did not succeed; inspecting its response" >&2
    RESULT="$(mktemp -d)"
    gh run download "$RUN_ID" --repo "$TARGET_SLUG" --name praxis-remote-response --dir "$RESULT"
    tooling remote-describe-summary "$RESULT/response.json"
  fi
fi

step "Done"
cat <<EOF
Remote execution is live in $TARGET_SLUG with Praxis $VERSION (capabilities: $CAPABILITIES).
Next: PRAXIS-REMOTE-11, the end-to-end proof. Have a cloud agent with no local
.NET/Praxis follow docs/remote-agent-contract.md: discover, start its work
item, record a step and usage, then let a second agent continue.
EOF
