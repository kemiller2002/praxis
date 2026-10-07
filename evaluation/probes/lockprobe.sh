#!/bin/bash
# Holds the work-protocol lock with a live foreign process, then runs a mutating group command.
D=$1; cd "$D" || exit 1
H=$(printf '%s' work-protocol | sha256sum | cut -d' ' -f1)
mkdir -p .ros/locks; sleep 60 & P=$!
printf '{"pid":%d,"ownerToken":"probe","resource":"work-protocol","acquiredAt":"%s"}\n' $P "$(date -u +%Y-%m-%dT%H:%M:%S.0000000Z)" > .ros/locks/$H.lock
start=$(date +%s)
echo '$ ros work group create --id GROUP-LOCK-001 --member WI-0002 --member PRAXIS-REMOTE-12 --occurred-at 2026-10-07T11:00:00Z (work-protocol lock held by another live process)'
./ros work group create --id GROUP-LOCK-001 --member WI-0002 --member PRAXIS-REMOTE-12 --occurred-at 2026-10-07T11:00:00Z > /tmp/lp.out 2>&1; rc=$?
end=$(date +%s); head -3 /tmp/lp.out; echo "rc=$rc elapsed=$((end-start))s; GROUP-LOCK-001 in groups.json: $(grep -c GROUP-LOCK-001 .ros/work/groups.json 2>/dev/null)"
rm -f .ros/locks/$H.lock; kill $P 2>/dev/null
