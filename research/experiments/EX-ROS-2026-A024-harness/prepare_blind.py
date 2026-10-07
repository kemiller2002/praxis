#!/usr/bin/env python3
"""EX-ROS-2026-A024 blinding (frozen before any arm ran).

Maps the three terminal arm snapshots to neutral labels with the frozen seed,
builds scrubbed evaluation packages, and publishes each package as an orphan
commit on its own branch, so a blinded session can be started on a checkout
that contains nothing but the package.

  python3 prepare_blind.py mapping --seed SEED --nonce NONCE
      prints the mapping commitment (sha256 of the canonical JSON of the mapping
      and the nonce) and writes nothing; the mapping is printed only with --reveal.
      The permutation is seeded with "SEED:NONCE": the frozen seed fixes the
      procedure before any arm ran, and the secret nonce (drawn once at blinding,
      revealed only after the blind evaluation is committed) stops anyone holding
      the public seed from recomputing the mapping. If the nonce were ever lost,
      identity is still established afterwards by exact source-blob identity.
  python3 prepare_blind.py score --seed SEED --start START --heads HEADS.json --out DIR [--publish]
      one package per label (inputs + that label only) -> experiment/a024-blind-score-LABEL
  python3 prepare_blind.py eval --seed SEED --start START --heads HEADS.json --acceptance DIR --out DIR [--publish]
      one package with all labels + acceptance/ -> experiment/a024-blind-eval

HEADS.json maps condition ("continuous", "code-only", "handoff") to the
terminal commit of that arm.
"""
import argparse
import hashlib
import io
import json
import os
import random
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
from functools import reduce

BASELINE = "8b4ffa392e93b19bf39f6672a608954c934cb815"
CONDITIONS = ("continuous", "code-only", "handoff")
LABELS = ("snapshot-K", "snapshot-P", "snapshot-W")
HARNESS = "research/experiments/EX-ROS-2026-A024-harness"
EXCLUDED = (".ros", "research/experiments/EX-ROS-2026-A024-handoffs")
REPO = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()


def mapping(seed, nonce):
    """Condition -> label, a permutation seeded with "SEED:NONCE" (Python random.Random, version 2 seeding)."""
    return dict(zip(CONDITIONS, random.Random(f"{seed}:{nonce}").sample(LABELS, len(LABELS))))


def commitment(seed, nonce):
    canonical = json.dumps({"experiment": "EX-ROS-2026-A024", "mapping": mapping(seed, nonce), "nonce": nonce},
                           sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(canonical.encode()).hexdigest()


def scrub_rules(label):
    return (
        (re.compile(r"experiment/a024-arm-[123]"), f"experiment/a024-{label}"),
        (re.compile(r"\barm-[123](?:-0[1-5])?\b"), label),
        (re.compile(r"https://claude\.ai/code/session_[A-Za-z0-9]+"), "<session redacted>"),
        (re.compile(r"session_[0-9A-Za-z]{20,}"), "<session redacted>"),
        (re.compile(r"(?m)^\s*Claude-Session:.*\n?"), ""),
        (re.compile(r"(?i)hand-?offs?"), "[record]"),
    )


def scrub(text, label, subjects=False):
    rules = scrub_rules(label) if subjects else scrub_rules(label)[:-1]
    return reduce(lambda acc, rule: rule[0].sub(rule[1], acc), rules, text)


def git(*args, text=True, **kwargs):
    return subprocess.run(["git", "-C", REPO, *args], capture_output=True, check=True, text=text, **kwargs).stdout


def extract(commit, dest, paths=()):
    os.makedirs(dest, exist_ok=True)
    with tarfile.open(fileobj=io.BytesIO(git("archive", commit, *paths, text=False))) as archive:
        archive.extractall(dest, filter="tar")


FIXED_TIME = 1791331200  # 2026-10-07T00:00:00Z: no extracted file carries an arm's commit time
CODE_SUFFIXES = (".fs", ".fsi", ".fsx", ".fsproj", ".mjs", ".js", ".py", ".sh", ".cs")


def scrub_tree(root, label):
    for folder, _, files in os.walk(root):
        for name in files:
            path = os.path.join(folder, name)
            if os.path.islink(path):
                continue
            os.utime(path, (FIXED_TIME, FIXED_TIME))
            try:
                original = open(path, encoding="utf-8").read()
            except (UnicodeDecodeError, OSError):
                continue
            # Prose and data also lose the word "handoff"; code keeps it so the tree still builds.
            cleaned = scrub(original, label, subjects=not name.endswith(CODE_SUFFIXES))
            if cleaned != original:
                open(path, "w", encoding="utf-8").write(cleaned)


def snapshot(label, start, head, dest):
    tree = os.path.join(dest, label, "tree")
    extract(head, tree)
    for excluded in EXCLUDED:
        shutil.rmtree(os.path.join(tree, excluded), ignore_errors=True)
    extract(start, tree, [".ros"])  # identical bookkeeping for every snapshot, so tests run
    scrub_tree(tree, label)
    pathspec = ["--", "."] + [f":(exclude){p}" for p in EXCLUDED]
    open(os.path.join(dest, label, "diff.patch"), "w", encoding="utf-8").write(
        scrub(git("diff", "--no-color", start, head, *pathspec), label, subjects=True))
    commits = [c for c in git("rev-list", "--reverse", "--first-parent", f"{start}..{head}").split() if git("diff-tree", "--no-commit-id", "--name-only", "-r", c, *pathspec).strip()]
    open(os.path.join(dest, label, "commits.txt"), "w", encoding="utf-8").write(
        "".join(f"{n + 1:03d} {scrub(git('log', '-1', '--format=%s', c).strip(), label, subjects=True)}\n" for n, c in enumerate(commits)))


def inputs(start, dest):
    folder = os.path.join(dest, "inputs")
    os.makedirs(folder, exist_ok=True)
    queue = json.loads(git("show", f"{BASELINE}:.ros/work/queue.json"))
    items = [i for i in queue["items"] if i["id"].startswith("PRAXIS-GROUP-0")]
    json.dump(items, open(os.path.join(folder, "work-items.json"), "w"), indent=2, ensure_ascii=False)
    open(os.path.join(folder, "PLANNING-WORK-GROUPS.md"), "w").write(git("show", f"{BASELINE}:requirements/PLANNING-WORK-GROUPS.md"))
    frozen = json.load(open(os.path.join(REPO, HARNESS, "manifest.json")))["hashes"]
    for name in ("acceptance-criteria.json", "rubric.md"):
        data = open(os.path.join(REPO, HARNESS, name), "rb").read()
        if hashlib.sha256(data).hexdigest() != frozen[name]:
            sys.exit(f"{name} does not match its frozen manifest hash")
        open(os.path.join(folder, name), "wb").write(data)
    open(os.path.join(folder, "start-commit.txt"), "w").write(f"common start (sanitized harness commit): {start}\nbaseline: {BASELINE}\n")


def publish(directory, branch, message):
    """Orphan commit of DIRECTORY's content on BRANCH; refuses to overwrite an existing remote branch."""
    with tempfile.TemporaryDirectory() as scratch:
        env = {**os.environ, "GIT_INDEX_FILE": os.path.join(scratch, "index")}
        run = lambda *a: subprocess.run(["git", "-C", REPO, f"--work-tree={directory}", *a], capture_output=True, text=True, check=True, env=env).stdout.strip()
        run("add", "-A", "-f", ".")
        commit = run("commit-tree", run("write-tree"), "-m", message)
        pushed = subprocess.run(["git", "-C", REPO, "push", "origin", f"{commit}:refs/heads/{branch}"], capture_output=True, text=True)
        if pushed.returncode != 0:
            sys.exit(f"push to {branch} failed (existing branches are never overwritten): {pushed.stderr.strip()}")
        return commit


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("mapping", "score", "eval"))
    parser.add_argument("--seed", type=int, required=True)
    parser.add_argument("--nonce", required=True)
    parser.add_argument("--start")
    parser.add_argument("--heads")
    parser.add_argument("--acceptance")
    parser.add_argument("--out")
    parser.add_argument("--publish", action="store_true")
    parser.add_argument("--reveal", action="store_true")
    options = parser.parse_args()
    labels = mapping(options.seed, options.nonce)
    if options.mode == "mapping":
        print(json.dumps({"commitment": commitment(options.seed, options.nonce), **({"mapping": labels} if options.reveal else {})}))
        return
    heads = json.load(open(options.heads))
    results = {}
    if options.mode == "score":
        for condition, label in sorted(labels.items(), key=lambda kv: kv[1]):
            dest = os.path.join(options.out, label)
            inputs(options.start, dest)
            snapshot(label, options.start, heads[condition], dest)
            results[label] = publish(dest, f"experiment/a024-blind-score-{label}", f"EX-ROS-2026-A024 blinded acceptance package {label}") if options.publish else None
    else:
        dest = options.out
        inputs(options.start, dest)
        for condition, label in sorted(labels.items(), key=lambda kv: kv[1]):
            snapshot(label, options.start, heads[condition], dest)
        shutil.copytree(options.acceptance, os.path.join(dest, "acceptance"), dirs_exist_ok=True)
        results["eval"] = publish(dest, "experiment/a024-blind-eval", "EX-ROS-2026-A024 blinded architecture evaluation package") if options.publish else None
    print(json.dumps({"commitment": commitment(options.seed, options.nonce), "published": results}))


if __name__ == "__main__":
    main()
