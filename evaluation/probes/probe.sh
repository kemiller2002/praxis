#!/bin/bash
# usage: probe.sh DIR   (DIR = built snapshot copy)
D=$1; cd "$D" || exit 1
git init -q 2>/dev/null; git add -A >/dev/null 2>&1; git -c user.email=e@x -c user.name=e commit -qm fx >/dev/null 2>&1
T=2026-10-07T10:00:00Z
run(){ echo "\$ ros $*"; out=$(./ros "$@" 2>/tmp/probe.err); rc=$?; echo "$out" | head -c 700; echo; echo "stderr: $(head -c 300 /tmp/probe.err)"; echo "rc=$rc"; echo; }
run work group show GROUP-NOPE-001 --json
run work group create --id GROUP-PROBE-001 --member NOPE-1 --member WI-0001 --occurred-at $T --json
run work group create --id GROUP-PROBE-001 --member WI-0002 --member PRAXIS-REMOTE-12 --occurred-at $T --json
run work group add --id GROUP-PROBE-001 --member NOPE-1 --occurred-at $T --json
run work group remove --id GROUP-PROBE-001 --member WI-0005 --occurred-at $T --json
run work group add --id GROUP-NOPE-001 --member WI-0005 --occurred-at $T --json
