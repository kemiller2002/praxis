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
# Requires: a clean checkout of a branch with an upstream, a built ./ros
# (npm run build:fsharp), node, and a Git identity. Prints `version=`,
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
  # The program is single-quoted on purpose: its ${...} are JavaScript.
  # shellcheck disable=SC2016
  node -e '
    const [current, bump] = process.argv.slice(1);
    const core = /^(\d+)\.(\d+)\.(\d+)$/;
    const parts = core.exec(current);
    if (!parts) { console.error(`current version ${current} is not X.Y.Z`); process.exit(1); }
    const [major, minor, patch] = parts.slice(1).map(Number);
    const next = {
      major: `${major + 1}.0.0`,
      minor: `${major}.${minor + 1}.0`,
      patch: `${major}.${minor}.${patch + 1}`
    }[bump] ?? bump;
    if (!core.test(next)) { console.error(`"${bump}" is not patch, minor, major or X.Y.Z`); process.exit(1); }
    const order = (v) => v.split(".").map(Number).reduce((sum, n) => sum * 1e6 + n, 0);
    if (order(next) <= order(current)) { console.error(`${next} is not newer than ${current}`); process.exit(1); }
    process.stdout.write(next);
  ' "$1" "$2"
}

branch="$(git symbolic-ref --quiet --short HEAD)" || fail "HEAD is detached; check out the release branch"
git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' >/dev/null 2>&1 || fail "branch '$branch' has no upstream"
[ -z "$(git status --porcelain)" ] || fail "the working tree is not clean"
git fetch --quiet origin "$branch"
[ "$(git rev-parse HEAD)" = "$(git rev-parse '@{upstream}')" ] || fail "'$branch' is not at its upstream head; pull or push first"

base="$(git rev-parse HEAD)"
current="$(node -p "require('./package.json').version")"
version="$(next_version "$current" "$bump")"
id="RELEASE-${version//./-}"
echo "releasing ${current} -> ${version} as ${id}" >&2

# Begin the work item before mutating anything.
./ros add "Release Praxis ${version}" --id "$id" --type mechanical \
  --description "Bump package.json and package-lock.json from ${current} to ${version} so native-release.yml releases it." >/dev/null
./ros work backlog-transition --action ready --id "$id" --occurred-at "$(now)" >/dev/null
./ros work start --id "$id" --type mechanical --occurred-at "$(now)" >/dev/null

npm version "$version" --no-git-tag-version --ignore-scripts >/dev/null
git add package.json package-lock.json .ros
git commit --quiet -m "${id}: release Praxis ${version}"
git push --quiet origin "HEAD:${branch}"

./ros work checkpoint --id "$id" --occurred-at "$(now)" \
  --summary "Bumped the package version from ${current} to ${version}" \
  --next-action "Run final completion transition" >&2
./ros work complete --id "$id" --occurred-at "$(now)" >/dev/null
./ros registry build >/dev/null
./ros validate >&2

git add -A
git commit --quiet -m "${id}: final checkpoint and completion"
git push --quiet origin "HEAD:${branch}"

echo "version=${version}"
echo "base=${base}"
echo "head=$(git rev-parse HEAD)"
