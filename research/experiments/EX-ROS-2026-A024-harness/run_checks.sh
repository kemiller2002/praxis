#!/usr/bin/env bash
# EX-ROS-2026-A024 deterministic checks for one snapshot (frozen before any arm ran).
#
# In a clean detached worktree of COMMIT: build exactly as the harness note
# prescribes, run the full F# test suite and `./ros validate`, and keep the raw
# logs plus a JSON summary. Nothing is judged by a model.
# Usage: run_checks.sh COMMIT OUT_DIR
set -uo pipefail

readonly COMMIT="$1" OUT="$(realpath -m "$2")"
readonly WORK="$(mktemp -d)/tree"
mkdir -p "$OUT"
git worktree add --detach "$WORK" "$COMMIT" >/dev/null 2>&1 || { echo "cannot create worktree for $COMMIT"; exit 2; }
trap 'git worktree remove --force "$WORK" >/dev/null 2>&1' EXIT
cd "$WORK"

npm run build:fsharp -- -p:FSharpCoreImplicitPackageVersion=10.1.400 > "$OUT/build.log" 2>&1; build=$?
if [[ $build -eq 0 ]]; then
  timeout 3600 dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll > "$OUT/tests.log" 2>&1; tests=$?
  ./ros validate > "$OUT/validate.log" 2>&1; validate=$?
else
  tests=null; validate=null
fi
python3 - "$OUT" "$COMMIT" "$build" "$tests" "$validate" <<'PY'
import json, re, sys
out, commit, build, tests, validate = sys.argv[1:]
code = lambda v: None if v == "null" else int(v)
lines = open(f"{out}/tests.log", encoding="utf-8", errors="replace").read().splitlines() if code(tests) is not None else []
summary = next((l for l in reversed(lines) if re.search(r"\d+ test\(s\);", l)), None)
counts = dict(zip(("total", "passed", "failed"), map(int, re.findall(r"\d+", summary)[:3]))) if summary else None
json.dump({"schema": "ex-ros-2026-a024.checks/1", "commit": commit,
           "buildExit": code(build), "testsExit": code(tests), "validateExit": code(validate),
           "tests": counts, "failedTests": sorted(l[5:] for l in lines if l.startswith("FAIL ")),
           "command": {"build": "npm run build:fsharp -- -p:FSharpCoreImplicitPackageVersion=10.1.400",
                       "tests": "dotnet tests/Ros.Tests/bin/Release/net10.0/Ros.Tests.dll", "validate": "./ros validate"}},
          open(f"{out}/checks.json", "w"), indent=2)
print(open(f"{out}/checks.json").read())
PY
