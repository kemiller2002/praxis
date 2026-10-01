#!/usr/bin/env python3
"""EX-ROS-2026-A022: build the blind evaluation kits (blinding-protocol.txt).

Orchestrator-only. Reads the sealed reveal (outside the repository), and for
every neutral arm builds one squashed, buildable commit on the baseline whose
tree is the arm's final tree with:
  - .ros/ reset to the baseline (lifecycle, telemetry and session identities
    reveal the condition);
  - research/experiments/EX-ROS-2026-A022-runs/ removed (metrics, analysis);
  - experiment-revealing words in comments and documentation replaced by
    "[redacted]" by one mechanical rule applied identically to every arm;
    hits inside code are never altered, only logged;
  - a neutral author, committer, date and message.
For every kit it builds one commit on the baseline that adds
research/experiments/EX-ROS-2026-A022-blind-<kit>/ (patches, work items,
instructions, template, checksums, the commitment) and nothing else.

Nothing is pushed unless --push is given. The leak and redaction log is
written to OUT (outside the repository) and is withheld from evaluators until
the evaluation is committed.

Usage: prepare_blind_kit.py --reveal REVEAL.json --commitment HEX --out DIR [--push]
"""
import argparse
import hashlib
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
SETUP = HERE.parent
REPO = SETUP.parents[2]
BASELINE = "f806f817e4656e00550313d839b59ceea87a682d"
RUNS_DIR = "research/experiments/EX-ROS-2026-A022-runs"
NEUTRAL = {"GIT_AUTHOR_NAME": "A022 blind kit", "GIT_AUTHOR_EMAIL": "blind-kit@invalid",
           "GIT_COMMITTER_NAME": "A022 blind kit", "GIT_COMMITTER_EMAIL": "blind-kit@invalid",
           "GIT_AUTHOR_DATE": "2026-01-01T00:00:00+0000", "GIT_COMMITTER_DATE": "2026-01-01T00:00:00+0000"}
# Experiment-specific terms only: bare "a022" is not matched because the
# repository has unrelated records such as RQ-ROS-2026-A022 and HY-ROS-2026-A022.
LEAK = re.compile(
    r"(?i)\b(cohort|grouped[ -]execution|independent[ -]execution|worker[ -]session"
    r"|ex-ros-2026-a022|exp-a022[\w-]*|experiment/a022[\w/-]*|a022-runs?\b[\w/-]*"
    r"|shared[ -]reasoning|fresh[ -](session|context)"
    r"|claude-session|claude\.ai/code/session_\w+|session_[A-Za-z0-9]{12,}"
    r"|EXE-(?!00000000T)[0-9]{8}T[0-9]{9}Z-[0-9a-f]{8})\b")
TEXT_SUFFIXES = (".md", ".txt", ".rst")
# Execution identities and contribution times in research records and
# registries (provenance front matter) would show how many executions an arm
# used and when; they are neutralized in every added line of these paths.
PROVENANCE_PATHS = ("research/", "registries/", "requirements/")
EXE_ID = re.compile(r"EXE-[0-9]{8}T[0-9]{9}Z-[0-9a-f]{8}")
TIMESTAMP = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?Z")
GATE_SENTENCE = ("Not to be merged to main before experiment EX-ROS-2026-A022 is evaluated "
                 "and the repository owner decides.")
sys.path.insert(0, str(HERE))
from a022_commitment import verify  # noqa: E402


def git(*args, env=None, stdin=None):
    result = subprocess.run(["git", *args], cwd=REPO, check=True, capture_output=True,
                            input=stdin, env={**os.environ, **(env or {})})
    return result.stdout


def git_text(*args, env=None, stdin=None):
    return git(*args, env=env, stdin=stdin).decode("utf-8").strip()


def comment_start(line):
    """Index where a comment starts, ignoring markers inside double-quoted strings."""
    stripped = line.lstrip()
    if stripped.startswith(("*", "#")):
        return len(line) - len(stripped)
    candidates = [m.start() for m in re.finditer(r"//|\(\*", line) if line[:m.start()].count('"') % 2 == 0]
    return min(candidates) if candidates else None


def neutralize(path, line):
    """Neutral execution IDs and times in provenance-bearing paths."""
    if not path.startswith(PROVENANCE_PATHS):
        return line, []
    found = EXE_ID.findall(line) + [m.group(0) for m in TIMESTAMP.finditer(line)]
    new = TIMESTAMP.sub("1970-01-01T00:00:00.000Z", EXE_ID.sub("EXE-00000000T000000000Z-00000000", line))
    return new, found


def redact_line(path, line):
    """(new_line, redacted, residual_in_code) for one added line."""
    line, normalized = neutralize(path, line)
    hits = [m.group(0) for m in LEAK.finditer(line)]
    if not hits:
        return line, normalized, []
    if path.endswith(TEXT_SUFFIXES) or path.startswith(PROVENANCE_PATHS):
        return LEAK.sub("[redacted]", line), normalized + hits, []
    start = comment_start(line)
    if start is None:
        return line, normalized, hits
    head, tail = line[:start], line[start:]
    in_code = [m.group(0) for m in LEAK.finditer(head)]
    return head + LEAK.sub("[redacted]", tail), normalized + [m.group(0) for m in LEAK.finditer(tail)], in_code


def scan_file(path, text, baseline_lines=frozenset()):
    """Only lines the arm added (absent from the baseline file) are scanned, so
    pre-existing repository text is never altered."""
    keep = lambda line: (line, [], [])
    rows = [(n, *(keep(line) if line in baseline_lines else redact_line(path, line)))
            for n, line in enumerate(text.split("\n"), 1)]
    new_text = "\n".join(r[1] for r in rows)
    log = [{"path": path, "line": n, "redacted": red, "residualInCode": res}
           for n, _, red, res in rows if red or res]
    return new_text, log


def changed_paths(final):
    names = git_text("diff", "--name-only", "--no-renames", "--diff-filter=AM", BASELINE, final)
    return [p for p in names.split("\n") if p and not p.startswith((".ros/", RUNS_DIR + "/"))]


def blind_arm_commit(arm, final):
    with tempfile.TemporaryDirectory() as tmp:
        env = {**NEUTRAL, "GIT_INDEX_FILE": str(pathlib.Path(tmp) / "index")}
        entries = lambda rev, *paths: [e for e in git("ls-tree", "-r", "-z", "--full-tree", rev, "--", *paths)
                                       .decode("utf-8").split("\0") if e]
        excluded = lambda entry: entry.split("\t", 1)[1].startswith((".ros/", RUNS_DIR + "/"))
        kept = [e for e in entries(final) if not excluded(e)] + entries(BASELINE, ".ros")
        git("update-index", "--index-info", env=env, stdin="".join(e + "\n" for e in kept).encode("utf-8"))

        def apply(path):
            raw = git("show", f"{final}:{path}")
            try:
                text = raw.decode("utf-8")
            except UnicodeDecodeError:
                return []
            exists = subprocess.run(["git", "cat-file", "-e", f"{BASELINE}:{path}"], cwd=REPO,
                                    capture_output=True).returncode == 0
            before = frozenset(git("show", f"{BASELINE}:{path}").decode("utf-8", "replace").split("\n")) if exists else frozenset()
            new_text, log = scan_file(path, text, before)
            if new_text != text:
                blob = git_text("hash-object", "-w", "--stdin", stdin=new_text.encode("utf-8"))
                mode = git_text("ls-tree", final, "--", path).split()[0]
                git("update-index", "--cacheinfo", f"{mode},{blob},{path}", env=env)
            return log

        log = [entry for path in changed_paths(final) for entry in apply(path)]
        tree = git_text("write-tree", env=env)
        commit = git_text("commit-tree", tree, "-p", BASELINE, "-m", f"blind arm {arm}", env=NEUTRAL)
    return commit, log


def kit_commit(kit, cohort, arms, arm_commits, commitment_hex):
    files = {
        "README.txt": kit_readme(kit, arms, arm_commits),
        "work-items.txt": (SETUP / "criteria" / f"cohort-{cohort}.txt").read_text(encoding="utf-8")
                          .replace(GATE_SENTENCE, "[merge-timing note removed for blinding]"),
        "evaluator-instructions.txt": (HERE / "evaluator-instructions.txt").read_text(encoding="utf-8"),
        "findings-template.json": (HERE / "findings-template.json").read_text(encoding="utf-8"),
        "mapping-commitment.sha256": commitment_hex + "\n",
        **{f"{arm}.patch": git("diff", "--binary", BASELINE, arm_commits[arm], "--", ".",
                               ":(exclude).ros").decode("utf-8") for arm in arms},
    }
    sums = "".join(f"{hashlib.sha256(text.encode('utf-8')).hexdigest()}  {name}\n"
                   for name, text in sorted(files.items()))
    files = {**files, "SHA256SUMS": sums}
    prefix = f"research/experiments/EX-ROS-2026-A022-blind-{kit}"
    with tempfile.TemporaryDirectory() as tmp:
        env = {**NEUTRAL, "GIT_INDEX_FILE": str(pathlib.Path(tmp) / "index")}
        git("read-tree", BASELINE, env=env)
        for name, text in files.items():
            blob = git_text("hash-object", "-w", "--stdin", stdin=text.encode("utf-8"))
            git("update-index", "--add", "--cacheinfo", f"100644,{blob},{prefix}/{name}", env=env)
        tree = git_text("write-tree", env=env)
        return git_text("commit-tree", tree, "-p", BASELINE, "-m", f"blind kit {kit}", env=NEUTRAL)


def kit_readme(kit, arms, arm_commits):
    lines = [f"EX-ROS-2026-A022 blind evaluation kit {kit}", "=" * 44, "",
             "Two independent implementations of the same work items (work-items.txt),",
             f"each started from baseline {BASELINE}.", "",
             "Arms (neutral names; the mapping is sealed, see mapping-commitment.sha256):"]
    lines += [f"  {arm}  branch blind/a022/{arm}  commit {arm_commits[arm]}  patch {arm}.patch" for arm in arms]
    lines += ["", "Read evaluator-instructions.txt before anything else.",
              "Record findings in the shape of findings-template.json.", ""]
    return "\n".join(lines)


def main(argv):
    parser = argparse.ArgumentParser()
    parser.add_argument("--reveal", required=True)
    parser.add_argument("--commitment", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--push", action="store_true")
    args = parser.parse_args(argv)
    raw = pathlib.Path(args.reveal).read_bytes()
    problems = verify(raw, args.commitment)
    if problems:
        sys.exit("reveal does not verify against the commitment: " + "; ".join(problems))
    out = pathlib.Path(args.out).resolve()
    if out == REPO or REPO in out.parents:
        sys.exit("refusing to write the leak log inside the repository working tree")
    payload = json.loads(raw)
    arm_results = {arm: blind_arm_commit(arm, v["finalSha"]) for arm, v in sorted(payload["blindArms"].items())}
    arm_commits = {arm: c for arm, (c, _) in arm_results.items()}
    kits = {kit: kit_commit(kit, cohort,
                            sorted(a for a, v in payload["blindArms"].items() if v["cohort"] == cohort),
                            arm_commits, args.commitment.strip().lower())
            for kit, cohort in sorted(payload["kits"].items())}
    out.mkdir(parents=True, exist_ok=True)
    (out / "leak-log.json").write_text(json.dumps(
        {arm: log for arm, (_, log) in arm_results.items()}, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    refs = {**{f"blind/a022/{a}": c for a, c in arm_commits.items()}, **{f"blind/a022/{k}": c for k, c in kits.items()}}
    (out / "blind-refs.json").write_text(json.dumps(refs, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if args.push:
        git("push", "origin", *[f"{c}:refs/heads/{r}" for r, c in sorted(refs.items())])
    print(json.dumps(refs, indent=2, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
