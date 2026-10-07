#!/usr/bin/env python3
"""Verifies EX-ROS-2026-A024 harness amendment 2 (one-line key fallback in prepare_blind.py).

Amendment 1 renamed rubric.md to rubric.txt; the frozen manifest still keys the
rubric hash as "rubric.md", so prepare_blind's pinned-input check must fall back
to that key. This proves prepare_blind.py equals its frozen version (commit
91745d9) with exactly amendment 1's literal replacement and this one line.
"""
import subprocess
import sys

FROZEN = "91745d917e8d6722c027d7033a436271d1746504"
PATH = "research/experiments/EX-ROS-2026-A024-harness/prepare_blind.py"
OLD = b'        if hashlib.sha256(data).hexdigest() != frozen[name]:'
NEW = b'        if hashlib.sha256(data).hexdigest() != frozen.get(name, frozen.get(name.replace(".txt", ".md"))):'

original = subprocess.run(["git", "show", f"{FROZEN}:{PATH}"], capture_output=True, check=True).stdout
expected = original.replace(b"rubric.md", b"rubric.txt").replace(OLD, NEW)
ok = original.count(OLD) == 1 and open(PATH, "rb").read() == expected
print("amendment 2 verified: prepare_blind.py = frozen + rubric rename + key fallback" if ok else "CHANGED beyond amendment 2")
sys.exit(0 if ok else 1)
