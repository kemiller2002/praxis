#!/usr/bin/env python3
"""Verifies EX-ROS-2026-A024 harness amendment 1 (rename only, no content change).

The frozen manifest (manifest.json, commit 91745d9) stays unmodified. Checks:
1. rubric.txt is byte-identical to the frozen rubric.md (manifest hash);
2. every other frozen file whose hash changed equals its frozen version with
   exactly the literal replacement "rubric.md" -> "rubric.txt" and no other byte
   changed (frozen versions are read from commit 91745d9);
3. every other frozen file is unchanged.
Exit 0 when all hold.
"""
import hashlib
import json
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REL = "research/experiments/EX-ROS-2026-A024-harness"
FROZEN = "91745d917e8d6722c027d7033a436271d1746504"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def frozen_bytes(name):
    return subprocess.run(["git", "show", f"{FROZEN}:{REL}/{name}"], capture_output=True, check=True).stdout


def main():
    hashes = json.load(open(os.path.join(HERE, "manifest.json")))["hashes"]
    current = lambda name: open(os.path.join(HERE, name), "rb").read()
    findings = []
    findings += [] if sha(current("rubric.txt")) == hashes["rubric.md"] else ["rubric.txt differs from the frozen rubric.md"]
    for name, frozen_hash in sorted(hashes.items()):
        if name == "rubric.md":
            continue
        now = current(name)
        if sha(now) == frozen_hash:
            continue
        original = frozen_bytes(name)
        ok = sha(original) == frozen_hash and original.replace(b"rubric.md", b"rubric.txt") == now
        findings += [] if ok else [f"{name} changed beyond the rubric rename"]
        print(("rename-only " if ok else "CHANGED ") + name)
    print("\n".join(findings) or "amendment 1 verified: only the rubric file name changed")
    sys.exit(1 if findings else 0)


if __name__ == "__main__":
    main()
