#!/usr/bin/env bash
# Bumps the package version as an attributed Praxis work item and pushes it,
# so native-release.yml can release it (see .github/workflows/release.yml).
#
#   scripts/praxis-release-bump.sh patch|minor|major|X.Y.Z
#
# The bump follows the work protocol like any other change: the work item
# begins before anything is mutated, the bump is committed and pushed, a
# durable checkpoint verifies it on the remote, and the work item completes
# before the Praxis state is committed and pushed. Nothing is published here.
#
# Requires: a clean checkout of a branch with an upstream, a built ./praxis
# (dotnet build Praxis.slnx --configuration Release), python3, and a Git
# identity. Prints `version=`,
# `base=` and `head=` lines (append them to $GITHUB_OUTPUT in Actions).
set -euo pipefail

usage() { echo "usage: $0 patch|minor|major|X.Y.Z" >&2; exit 2; }
now() { date -u +%Y-%m-%dT%H:%M:%S.000Z; }
fail() { echo "ERROR $*" >&2; exit 1; }

[ "$#" -eq 1 ] || usage
bump="$1"

cd "$(git rev-parse --show-toplevel)"

# The next version, derived without mutating anything.
next_version() {
  python3 - "$1" "$2" <<'PY'
import re, sys
current, bump = sys.argv[1], sys.argv[2]
core = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")
parts = core.match(current)
if not parts:
    sys.exit(f"current version {current} is not X.Y.Z")
major, minor, patch = map(int, parts.groups())
nxt = {"major": f"{major + 1}.0.0", "minor": f"{major}.{minor + 1}.0", "patch": f"{major}.{minor}.{patch + 1}"}.get(bump, bump)
if not core.match(nxt):
    sys.exit(f'"{bump}" is not patch, minor, major or X.Y.Z')
if tuple(map(int, nxt.split("."))) <= (major, minor, patch):
    sys.exit(f"{nxt} is not newer than {current}")
sys.stdout.write(nxt)
PY
}

# release.json is the single version source (Directory.Build.props).
set_version() {
  python3 - "$1" <<'PY'
import json, sys
with open("release.json") as source:
    release = json.load(source)
release["version"] = sys.argv[1]
with open("release.json", "w") as target:
    target.write(json.dumps(release, indent=2) + "\n")
PY
}

branch="$(git symbolic-ref --quiet --short HEAD)" || fail "HEAD is detached; check out the release branch"
git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' >/dev/null 2>&1 || fail "branch '$branch' has no upstream"
[ -z "$(git status --porcelain)" ] || fail "the working tree is not clean"
git fetch --quiet origin "$branch"
[ "$(git rev-parse HEAD)" = "$(git rev-parse '@{upstream}')" ] || fail "'$branch' is not at its upstream head; pull or push first"

base="$(git rev-parse HEAD)"
current="$(python3 -c 'import json; print(json.load(open("release.json"))["version"])')"
version="$(next_version "$current" "$bump")"
id="RELEASE-${version//./-}"
echo "releasing ${current} -> ${version} as ${id}" >&2

# Begin the work item before mutating anything.
./praxis add "Release Praxis ${version}" --id "$id" --type mechanical \
  --description "Bump release.json from ${current} to ${version} so native-release.yml releases it." >/dev/null
./praxis work backlog-transition --action ready --id "$id" --occurred-at "$(now)" >/dev/null
./praxis work start --id "$id" --type mechanical --occurred-at "$(now)" >/dev/null

set_version "$version"
git add release.json .ros
git commit --quiet -m "${id}: release Praxis ${version}"
git push --quiet origin "HEAD:${branch}"

./praxis work checkpoint --id "$id" --occurred-at "$(now)" \
  --summary "Bumped the package version from ${current} to ${version}" \
  --next-action "Run final completion transition" >&2
./praxis work complete --id "$id" --occurred-at "$(now)" >/dev/null
./praxis registry build >/dev/null
./praxis validate >&2

git add -A
git commit --quiet -m "${id}: final checkpoint and completion"
git push --quiet origin "HEAD:${branch}"

echo "version=${version}"
echo "base=${base}"
echo "head=$(git rev-parse HEAD)"
